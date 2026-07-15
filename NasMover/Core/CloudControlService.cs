using System.Net.Http.Headers;
using Newtonsoft.Json;

namespace NasMover.Core;

/// <summary>
/// CloudControlService.CheckForUpdateAsync 的返回结果。
/// AppRuntime 根据此结果决定下一步动作：
///   - HotConfigChanged = true  → 立即热更新（重新加载配置）
///   - ColdConfigChanged = true → 标记"待重启生效"
/// </summary>
public class CloudCheckResult
{
    /// <summary>是否发现了新版本并成功拉取</summary>
    public bool HasUpdate { get; init; }

    /// <summary>新配置中 hot_config 部分有变化（可立即生效）</summary>
    public bool HotConfigChanged { get; init; }

    /// <summary>新配置中 cold_config 部分有变化（需重启）</summary>
    public bool ColdConfigChanged { get; init; }

    /// <summary>新版本号</summary>
    public string? NewVersion { get; init; }

    /// <summary>解析后的新配置对象（AppRuntime 可直接读取热更新字段）</summary>
    public AppConfig? NewConfig { get; init; }

    /// <summary>失败原因或"无更新"的原因</summary>
    public string? Message { get; init; }
}


/// <summary>
///   1. 按配置拉取远端 config-manifest.json
///   2. 比较版本号，判断是否需更新
///   3. 需要更新时拉取 config.json，校验结构
///   4. 写入本地 cloud-cache/ 目录（config.json + cloud-state.json）
///   5. 返回变更结果，供 AppRuntime 决定是否热更新
///
/// 生命周期：
///   依附于 AppRuntime，AppRuntime 启动时创建，程序退出时 Dispose。
///   定时检查由 AppRuntime 驱动，CloudControlService 自身不做定时。
///
/// 线程安全：
///   不在多个线程同时调用 CheckForUpdateAsync。
/// </summary>
public class CloudControlService : IDisposable
{
    // cloud-cache/             # 缓存目录结构
    //   ├─ config.json         # 最新拉取的配置
    //   ├─ config.prev.json    # 上一版配置（备份，方便回退）
    //   └─ cloud-state.json    # Manifest 元数据缓存
    private const string CacheDirName = "cloud-cache";
    private const string StateFileName = "cloud-state.json";
    private const string ConfigFileName = "config.json";
    private const string PrevConfigFileName = "config.prev.json";


    private readonly CloudControlConfig _config;
    private readonly string _cacheDir;
    private readonly string _machineId;
    private readonly string _machineGroup;
    private readonly string _appVersion;
    private readonly HttpClient _httpClient;


    /// <summary>日志输出，供 UI 或 AppRuntime 订阅</summary>
    public event Action<string>? OnLog;


    /// <param name="config">cloud_control 配置块</param>
    /// <param name="configDir">config.json 所在目录（cloud-cache 建在此目录下）</param>
    /// <param name="machineId">本机 machine_id，用于 scope 过滤</param>
    /// <param name="machineGroup">本机分组，用于 scope.machine_group 匹配</param>
    /// <param name="appVersion">当前应用版本号，用于 min_client_version 检查</param>
    public CloudControlService(
        CloudControlConfig config,
        string configDir,
        string machineId,
        string machineGroup,
        string appVersion)
    {
        _config = config;
        _cacheDir = Path.Combine(configDir, CacheDirName);
        _machineId = machineId;
        _machineGroup = machineGroup;
        _appVersion = appVersion;

        // 创建 HTTP 客户端（复用连接，避免每次检查都新建）
        _httpClient = new HttpClient();

        // 超时时间从配置读取，最少 1 秒
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, config.RequestTimeoutSeconds));

        // Bearer Token 鉴权
        if (!string.IsNullOrEmpty(config.AuthToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.AuthToken);
        }

        // 识别头
        _httpClient.DefaultRequestHeaders.Add("User-Agent", $"NAS-Mover/{appVersion}");
    }


    /// <summary>
    /// 核心流程：拉取 manifest → 版本比较 → 拉取 config → 校验 → 缓存 → 返回结果
    /// </summary>
    public async Task<CloudCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        // 未启用 → 直接返回无更新
        if (!_config.Enabled)
        {
            Log("云控未启用，跳过检查");
            return new CloudCheckResult { HasUpdate = false, Message = "云控未启用" };
        }

        try
        {
            Log($"开始检查配置更新: {_config.ManifestUrl}");

            // ===============================================================
            // 第 1 步：拉取 manifest
            // ===============================================================
            var manifest = await FetchManifestAsync(ct);
            if (manifest == null)
            {
                return new CloudCheckResult
                {
                    HasUpdate = false,
                    Message = "拉取 manifest 失败或返回格式无效"
                };
            }

            // ===============================================================
            // 第 2 步：Scope 过滤
            // ===============================================================
            // 如果 manifest 指定了 machine_group 或 machine_id_allowlist，
            // 本机不在范围中则跳过，避免错误机台拉错配置
            if (ShouldSkipByScope(manifest))
            {
                Log("manifest scope 不匹配，跳过本次更新");
                return new CloudCheckResult { HasUpdate = false, Message = "scope 不匹配" };
            }

            // ===============================================================
            // 第 3 步：MinClientVersion 检查
            // ===============================================================
            // 如果远端配置要求客户端版本高于当前版本，跳过
            // 避免旧客户端应用了需要新功能的配置
            if (ShouldSkipByMinVersion(manifest))
            {
                Log($"当前版本 {_appVersion} 低于 manifest 要求的最小版本 {manifest.MinClientVersion}，跳过");
                return new CloudCheckResult
                {
                    HasUpdate = false,
                    Message = $"客户端版本过低（最低 {manifest.MinClientVersion}，当前 {_appVersion}）"
                };
            }

            // ===============================================================
            // 第 4 步：版本比较
            // ===============================================================
            string? cachedVersion = LoadCachedVersion();
            if (cachedVersion != null && cachedVersion == manifest.Version)
            {
                Log($"版本无变化: {manifest.Version}");
                return new CloudCheckResult { HasUpdate = false, Message = "版本一致" };
            }

            Log($"发现新版本: {cachedVersion ?? "(无缓存)"} → {manifest.Version}");

            // ===============================================================
            // 第 5 步：拉取完整配置
            // ===============================================================
            string? configJson = await FetchConfigStringAsync(manifest, ct);
            if (string.IsNullOrEmpty(configJson))
            {
                return new CloudCheckResult
                {
                    HasUpdate = false,
                    Message = "拉取 config.json 失败或返回空"
                };
            }

            // ===============================================================
            // 第 6 步：解析 & 校验
            // ===============================================================
            var newConfig = ValidateRemoteConfig(configJson);
            if (newConfig == null)
            {
                return new CloudCheckResult
                {
                    HasUpdate = false,
                    Message = "远端配置验证失败，已拒绝应用"
                };
            }

            // ===============================================================
            // 第 7 步：读取旧缓存（用于对比变更类型）
            // ===============================================================
            var oldConfig = LoadCachedConfig();

            // ===============================================================
            // 第 8 步：写入本地缓存
            // ===============================================================
            SaveToCache(manifest.Version, manifest.UpdatedAt, configJson);

            // ===============================================================
            // 第 9 步：判断变更类型
            // ===============================================================
            bool hotChanged = ConfigJsonChanged(oldConfig?.HotConfig, newConfig.HotConfig);
            bool coldChanged = ConfigJsonChanged(oldConfig?.ColdConfig, newConfig.ColdConfig);

            Log($"更新完成: version={manifest.Version}, " +
                $"hot={(hotChanged ? "有变化" : "无变化")}, " +
                $"cold={(coldChanged ? "有变化（需重启）" : "无变化")}");

            return new CloudCheckResult
            {
                HasUpdate = true,
                HotConfigChanged = hotChanged,
                ColdConfigChanged = coldChanged,
                NewVersion = manifest.Version,
                NewConfig = newConfig,
                Message = "更新成功"
            };
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消（暂停/停止时 AppRuntime 会取消令牌）
            Log("检查被取消");
            return new CloudCheckResult { HasUpdate = false, Message = "已取消" };
        }
        catch (HttpRequestException ex)
        {
            Log($"HTTP 请求失败: {ex.Message}");
            return new CloudCheckResult { HasUpdate = false, Message = $"HTTP 请求失败: {ex.Message}" };
        }
        catch (Exception ex)
        {
            // 所有未预期的异常统一捕获，不让调用方崩溃
            Log($"检查更新异常: {ex.Message}");
            return new CloudCheckResult { HasUpdate = false, Message = ex.Message };
        }
    }


    /// <summary>
    /// 第 1 步：拉取 config-manifest.json 并反序列化
    /// </summary>
    private async Task<ConfigManifest?> FetchManifestAsync(CancellationToken ct)
    {
        string json;
        try
        {
            json = await _httpClient.GetStringAsync(_config.ManifestUrl, ct);
        }
        catch (Exception ex)
        {
            Log($"获取 manifest 失败: {ex.Message}");
            return null;
        }

        try
        {
            var manifest = JsonConvert.DeserializeObject<ConfigManifest>(json);
            if (manifest == null || string.IsNullOrEmpty(manifest.Version))
            {
                Log("manifest 格式错误: version 字段缺失或为空");
                return null;
            }
            if (string.IsNullOrEmpty(manifest.ConfigUrl))
            {
                Log("manifest 格式错误: config_url 字段缺失或为空");
                return null;
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            Log($"manifest JSON 解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 第 2 步：判断本机是否在 manifest scope 范围内
    /// </summary>
    private bool ShouldSkipByScope(ConfigManifest manifest)
    {
        if (manifest.Scope == null)
            return false;

        // MachineGroup 检查：scope.machine_group 不为空时，
        // 本机分组必须匹配（不区分大小写）
        if (!string.IsNullOrWhiteSpace(manifest.Scope.MachineGroup))
        {
            bool groupMatch = string.Equals(
                manifest.Scope.MachineGroup,
                _machineGroup,
                StringComparison.OrdinalIgnoreCase);
            if (!groupMatch)
            {
                Log($"machine_group 不匹配: manifest=[{manifest.Scope.MachineGroup}], 本机=[{_machineGroup}]");
                return true;
            }
        }

        // MachineId allowlist 检查：列表非空时，本机 ID 必须在其中
        if (manifest.Scope.MachineIdAllowlist.Count > 0)
        {
            bool idInList = manifest.Scope.MachineIdAllowlist.Contains(
                _machineId,
                StringComparer.OrdinalIgnoreCase);
            if (!idInList)
            {
                Log($"machine_id [{_machineId}] 不在 manifest allowlist 中");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 第 3 步：检查客户端版本是否满足 manifest 的最低要求
    /// </summary>
    private bool ShouldSkipByMinVersion(ConfigManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.MinClientVersion))
            return false;

        // 用 Version 做语义化版本比较
        // "1.0.0" → 主版.次版.修订 三段格式
        if (Version.TryParse(manifest.MinClientVersion, out var minVer)
            && Version.TryParse(_appVersion, out var currentVer))
        {
            return currentVer < minVer;
        }

        // 版本号解析失败时保守处理：跳过更新
        Log($"版本号解析失败: minClient={manifest.MinClientVersion}, current={_appVersion}，跳过更新");
        return true;
    }

    /// <summary>
    /// 第 5 步：根据 manifest 中的 config_url 拉取完整配置
    /// config_url 可能是相对路径（如 /config/config.json），
    /// 需要基于 manifest URL 解析得到完整 URL
    /// </summary>
    private async Task<string?> FetchConfigStringAsync(ConfigManifest manifest, CancellationToken ct)
    {
        string configUrl;

        // 如果是绝对 URL → 直接用
        if (Uri.TryCreate(manifest.ConfigUrl, UriKind.Absolute, out var absoluteUri))
        {
            configUrl = absoluteUri.ToString();
        }
        else
        {
            // 相对 URL → 基于 manifest URL 的 base 解析
            // 例如 manifest_url=http://host:9810/config/manifest.json
            // config_url=/config/config.json → http://host:9810/config/config.json
            var baseUri = new Uri(_config.ManifestUrl);
            configUrl = new Uri(baseUri, manifest.ConfigUrl).ToString();
        }

        Log($"下载配置: {configUrl}");

        try
        {
            return await _httpClient.GetStringAsync(configUrl, ct);
        }
        catch (Exception ex)
        {
            Log($"下载配置失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 第 6 步：解析并校验远端配置
    /// 校验内容：
    ///   - JSON 结构完整（能反序列化成 AppConfig）
    ///   - hot_config 必要字段齐全
    ///   - active_model 在 models 中存在
    ///   - 至少有一个 base_dir 存在（防止下发完全错误的配置）
    /// </summary>
    private AppConfig? ValidateRemoteConfig(string json)
    {
        AppConfig? config;

        try
        {
            config = JsonConvert.DeserializeObject<AppConfig>(json);
        }
        catch (JsonException ex)
        {
            Log($"远端配置 JSON 解析失败: {ex.Message}");
            return null;
        }

        if (config == null)
        {
            Log("远端配置反序列化返回 null");
            return null;
        }

        // ---- hot_config 校验 ----

        if (string.IsNullOrWhiteSpace(config.HotConfig.NasBasePath))
        {
            Log("校验失败: hot_config.nas_base_path 为空");
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.HotConfig.ActiveModel))
        {
            Log("校验失败: hot_config.active_model 为空");
            return null;
        }

        if (config.HotConfig.Models == null || config.HotConfig.Models.Count == 0)
        {
            Log("校验失败: hot_config.models 为空（至少需要一个机种配置）");
            return null;
        }

        if (!config.HotConfig.Models.ContainsKey(config.HotConfig.ActiveModel))
        {
            string available = string.Join(", ", config.HotConfig.Models.Keys);
            Log($"校验失败: active_model [{config.HotConfig.ActiveModel}] 不在 models 中。可用: [{available}]");
            return null;
        }

        var modelConfig = config.HotConfig.ActiveModelConfig!;
        if (modelConfig.BaseDirs.Count == 0)
        {
            Log("校验失败: 当前机种的 base_dirs 为空（至少需要一个目录）");
            return null;
        }

        // ---- cold_config 校验 ----

        if (string.IsNullOrWhiteSpace(config.ColdConfig.MachineId))
        {
            Log("校验失败: cold_config.machine_id 为空");
            return null;
        }

        Log("远端配置校验通过");
        return config;
    }

    // ======================================================================
    // 缓存读写方法
    // ======================================================================

    /// <summary>读取本地缓存的版本号</summary>
    private string? LoadCachedVersion()
    {
        string statePath = Path.Combine(_cacheDir, StateFileName);
        if (!File.Exists(statePath))
            return null;

        try
        {
            string json = File.ReadAllText(statePath);
            var state = JsonConvert.DeserializeObject<CachedCloudState>(json);
            return state?.Version;
        }
        catch
        {
            // 缓存文件损坏→忽略，重新拉取
            return null;
        }
    }

    /// <summary>读取本地缓存的配置对象（用于对比变更）</summary>
    private AppConfig? LoadCachedConfig()
    {
        string configPath = Path.Combine(_cacheDir, ConfigFileName);
        if (!File.Exists(configPath))
            return null;

        try
        {
            string json = File.ReadAllText(configPath);
            return JsonConvert.DeserializeObject<AppConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将拉取到的新配置写入本地缓存：
    ///   1. 现有 config.json → 备份为 config.prev.json
    ///   2. 写入新 config.json
    ///   3. 写入 cloud-state.json（记录版本号和拉取时间）
    /// </summary>
    private void SaveToCache(string version, DateTime updatedAt, string configJson)
    {
        // 确保缓存目录存在
        Directory.CreateDirectory(_cacheDir);

        // 第 1 步：备份上一版
        string configPath = Path.Combine(_cacheDir, ConfigFileName);
        string prevPath = Path.Combine(_cacheDir, PrevConfigFileName);
        if (File.Exists(configPath))
        {
            File.Copy(configPath, prevPath, overwrite: true);
            Log("已备份上一版配置 → config.prev.json");
        }

        // 第 2 步：写入新版
        File.WriteAllText(configPath, configJson);
        Log("已写入新版配置 → config.json");

        // 第 3 步：写入元数据
        var state = new CachedCloudState
        {
            Version = version,
            UpdatedAt = updatedAt,
            CachedAt = DateTime.Now
        };
        string stateJson = JsonConvert.SerializeObject(state, Formatting.Indented);
        File.WriteAllText(Path.Combine(_cacheDir, StateFileName), stateJson);
        Log("已更新 cloud-state.json");
    }

    /// <summary>
    /// 用 JSON 序列化比较两个配置对象是否一致。
    /// 一致 = 所有字段值相同（忽略 null / 默认值差异）。
    /// </summary>
    private static bool ConfigJsonChanged<T>(T? old, T? newObj) where T : class
    {
        if (old == null && newObj == null) return false;
        if (old == null || newObj == null) return true;

        string oldJson = JsonConvert.SerializeObject(old, Formatting.None);
        string newJson = JsonConvert.SerializeObject(newObj, Formatting.None);
        return oldJson != newJson;
    }

    // ======================================================================
    // 辅助方法
    // ======================================================================

    private void Log(string msg) => OnLog?.Invoke($"[CloudControl] {msg}");

    public void Dispose()
    {
        _httpClient.Dispose();
    }


    /// <summary>config-manifest.json 的解析模型</summary>
    private class ConfigManifest
    {
        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonProperty("config_url")]
        public string ConfigUrl { get; set; } = string.Empty;

        [JsonProperty("min_client_version")]
        public string? MinClientVersion { get; set; }

        [JsonProperty("scope")]
        public ManifestScope? Scope { get; set; }
    }

    /// <summary>manifest 中的 scope 段</summary>
    private class ManifestScope
    {
        [JsonProperty("machine_group")]
        public string MachineGroup { get; set; } = string.Empty;

        [JsonProperty("machine_id_allowlist")]
        public List<string> MachineIdAllowlist { get; set; } = new();
    }

    /// <summary>cloud-state.json 的模型</summary>
    private class CachedCloudState
    {
        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonProperty("cached_at")]
        public DateTime CachedAt { get; set; }
    }
}
