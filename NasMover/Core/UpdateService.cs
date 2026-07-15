using System.Net.Http.Headers;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace NasMover.Core;

/// <summary>
/// CheckForUpdateAsync 的返回结果。
/// AppRuntime 根据 HasUpdate 决定是否显示更新提醒或自动下载。
/// </summary>
public class UpdateCheckResult
{
    /// <summary>是否存在新版本</summary>
    public bool HasUpdate { get; init; }

    /// <summary>当前本地版本号</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>远端新版本号</summary>
    public string? NewVersion { get; init; }

    /// <summary>更新包下载 URL</summary>
    public string? PackageUrl { get; init; }

    /// <summary>更新包 SHA256 校验值</summary>
    public string? Sha256 { get; init; }

    /// <summary>是否强制更新</summary>
    public bool ForceUpdate { get; init; }

    /// <summary>发布说明</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>详情消息（成功/失败原因）</summary>
    public string? Message { get; init; }
}


/// <summary>
/// OTA 更新检查服务。
///
/// 职责：
///   1. 拉取远端 update-manifest.json，解析版本号
///   2. 与当前版本比较，判断是否需要更新
///   3. 需要更新时提供下载功能（含 SHA256 校验）
///
/// 生命周期：
///   每次检查创建一次实例（轻量），或由 AppRuntime 持有长期实例。
///   定时检查由 AppRuntime 驱动。
///
/// 线程安全：
///   不在多个线程同时调用 CheckForUpdateAsync。
/// </summary>
public class UpdateService : IDisposable
{
    private const string CacheDirName = "update-cache";

    private readonly UpdateControlConfig _config;
    private readonly string _cacheDir;
    private readonly string _currentVersion;
    private readonly HttpClient _httpClient;


    public event Action<string>? OnLog;


    /// <param name="config">update_control 配置块</param>
    /// <param name="appDir">程序运行目录（update-cache 建在此目录下）</param>
    /// <param name="currentVersion">当前应用版本号</param>
    public UpdateService(UpdateControlConfig config, string appDir, string currentVersion)
    {
        _config = config;
        _cacheDir = Path.Combine(appDir, CacheDirName);
        _currentVersion = currentVersion;

        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, config.RequestTimeoutSeconds));

        if (!string.IsNullOrEmpty(config.AuthToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.AuthToken);
        }

        _httpClient.DefaultRequestHeaders.Add("User-Agent", $"NAS-Mover/{currentVersion}");
    }


    /// <summary>
    /// 核心流程：拉取 manifest → 版本比较 → 返回结果。
    /// 不自动下载，由调用方根据结果决定。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            Log("OTA 更新未启用，跳过检查");
            return new UpdateCheckResult { HasUpdate = false, Message = "更新未启用" };
        }

        if (string.IsNullOrWhiteSpace(_config.ManifestUrl))
        {
            Log("OTA manifest_url 为空，跳过检查");
            return new UpdateCheckResult { HasUpdate = false, Message = "manifest_url 未配置" };
        }

        try
        {
            Log($"检查更新: {_config.ManifestUrl}");

            // ---- 拉取 manifest ----
            string json;
            try
            {
                json = await _httpClient.GetStringAsync(_config.ManifestUrl, ct);
            }
            catch (Exception ex)
            {
                Log($"获取 update manifest 失败: {ex.Message}");
                return new UpdateCheckResult { HasUpdate = false, Message = $"拉取失败: {ex.Message}" };
            }

            // ---- 解析 ----
            var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
            if (manifest == null || string.IsNullOrEmpty(manifest.Version))
            {
                Log("update manifest 格式错误: version 缺失");
                return new UpdateCheckResult { HasUpdate = false, Message = "manifest 格式错误" };
            }

            if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                Log("update manifest 格式错误: package_url 缺失");
                return new UpdateCheckResult { HasUpdate = false, Message = "package_url 缺失" };
            }

            // ---- 版本比较 ----
            bool hasUpdate = CompareVersions(manifest.Version, _currentVersion) > 0;

            if (!hasUpdate)
            {
                Log($"当前已是最新版本: {_currentVersion}");
                return new UpdateCheckResult
                {
                    HasUpdate = false,
                    CurrentVersion = _currentVersion,
                    NewVersion = manifest.Version,
                    Message = "已是最新版本"
                };
            }

            // ---- 解析 package_url（支持相对路径） ----
            string packageUrl;
            if (Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var absoluteUri))
            {
                packageUrl = absoluteUri.ToString();
            }
            else
            {
                var baseUri = new Uri(_config.ManifestUrl);
                packageUrl = new Uri(baseUri, manifest.PackageUrl).ToString();
            }

            Log($"发现新版本: {_currentVersion} → {manifest.Version}");
            Log($"更新包: {packageUrl}");

            return new UpdateCheckResult
            {
                HasUpdate = true,
                CurrentVersion = _currentVersion,
                NewVersion = manifest.Version,
                PackageUrl = packageUrl,
                Sha256 = manifest.Sha256,
                ForceUpdate = manifest.ForceUpdate,
                ReleaseNotes = manifest.ReleaseNotes,
                Message = $"发现新版本 {manifest.Version}"
            };
        }
        catch (OperationCanceledException)
        {
            Log("更新检查被取消");
            return new UpdateCheckResult { HasUpdate = false, Message = "已取消" };
        }
        catch (HttpRequestException ex)
        {
            Log($"HTTP 请求失败: {ex.Message}");
            return new UpdateCheckResult { HasUpdate = false, Message = $"HTTP 请求失败: {ex.Message}" };
        }
        catch (Exception ex)
        {
            Log($"更新检查异常: {ex.Message}");
            return new UpdateCheckResult { HasUpdate = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// 下载更新包到本地缓存目录。
    /// 如果提供了 SHA256，下载完成后会自动校验。
    /// </summary>
    /// <param name="packageUrl">更新包 URL</param>
    /// <param name="expectedSha256">期望的 SHA256（可选），不匹配时会删除文件并返回 false</param>
    /// <returns>本地缓存文件路径，失败返回 null</returns>
    public async Task<string?> DownloadUpdateAsync(
        string packageUrl,
        string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_cacheDir);

        string fileName = $"nas-mover-{Guid.NewGuid():N}.zip";
        string filePath = Path.Combine(_cacheDir, fileName);

        try
        {
            Log($"开始下载更新包: {packageUrl}");

            var response = await _httpClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(filePath))
            {
                await stream.CopyToAsync(file, ct);
            }

            Log($"下载完成 ({new FileInfo(filePath).Length / 1024.0:F1} KB)");

            // ---- SHA256 校验 ----
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                bool valid = await VerifySha256Async(filePath, expectedSha256);
                if (!valid)
                {
                    Log("SHA256 校验失败，文件可能损坏，已删除");
                    File.Delete(filePath);
                    return null;
                }
                Log("SHA256 校验通过");
            }

            return filePath;
        }
        catch (OperationCanceledException)
        {
            Log("下载被取消");
            CleanFile(filePath);
            throw;
        }
        catch (Exception ex)
        {
            Log($"下载失败: {ex.Message}");
            CleanFile(filePath);
            return null;
        }
    }

    /// <summary>
    /// 计算文件的 SHA256 并与期望值比较（不区分大小写）。
    /// </summary>
    private static async Task<bool> VerifySha256Async(string filePath, string expectedSha256)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        string actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        string expected = expectedSha256.Trim().ToLowerInvariant();
        return actual == expected;
    }

    /// <summary>安全删除文件（忽略不存在的情况）</summary>
    private static void CleanFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 语义化版本比较：major.minor.patch。
    /// 返回正数 = v1 > v2，负数 = v1 < v2，0 = 相等。
    /// 解析失败时保守处理 = 不更新（返回 0）。
    /// </summary>
    private static int CompareVersions(string v1, string v2)
    {
        if (Version.TryParse(v1, out var ver1) && Version.TryParse(v2, out var ver2))
            return ver1.CompareTo(ver2);
        return 0;
    }


    private void Log(string msg) => OnLog?.Invoke($"[UpdateService] {msg}");

    public void Dispose()
    {
        _httpClient.Dispose();
    }


    /// <summary>update-manifest.json 的解析模型</summary>
    private class UpdateManifest
    {
        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonProperty("package_url")]
        public string PackageUrl { get; set; } = string.Empty;

        [JsonProperty("sha256")]
        public string? Sha256 { get; set; }

        [JsonProperty("force_update")]
        public bool ForceUpdate { get; set; }

        [JsonProperty("release_notes")]
        public string? ReleaseNotes { get; set; }
    }
}
