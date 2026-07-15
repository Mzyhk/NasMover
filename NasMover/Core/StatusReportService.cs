using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NasMover.Core;

// ============================================================================
// 状态上报数据模型
// ============================================================================
/// <summary>
/// 状态上报的 JSON Payload。
/// 由 AppRuntime 提供的工厂方法组装，StatusReportService 只负责序列化和发送。
///
/// 上报字段设计原则：
///   - 只上报远端需要的信息
///   - 不上报：全量日志、本地绝对路径、实时速度/ETA
/// </summary>
public class StatusPayload
{
    // ---- 设备身份 ----

    /// <summary>机台唯一标识符</summary>
    [JsonProperty("machine_id")]
    public string MachineId { get; init; } = string.Empty;

    /// <summary>机台分组（用于 manifest scope 匹配）</summary>
    [JsonProperty("machine_group")]
    public string MachineGroup { get; init; } = string.Empty;

    /// <summary>当前应用版本号</summary>
    [JsonProperty("app_version")]
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>当前激活的机种名称</summary>
    [JsonProperty("active_model")]
    public string ActiveModel { get; init; } = string.Empty;

    // ---- 运行状态 ----

    /// <summary>当前运行模式（字符串序列化，如 "transferring"）</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    [JsonProperty("mode")]
    public AppMode Mode { get; init; }

    /// <summary>NAS 是否可达（由 AppRuntime 探测）</summary>
    [JsonProperty("nas_connected")]
    public bool NasConnected { get; init; }

    /// <summary>配置异常说明，正常时为 null</summary>
    [JsonProperty("config_issue")]
    public string? ConfigIssue { get; init; }

    /// <summary>最近一次错误信息，正常时为 null</summary>
    [JsonProperty("last_error")]
    public string? LastError { get; init; }

    // ---- 传输进度 ----

    /// <summary>当前阶段说明（如"正在传输: Pick_001.jpg"）</summary>
    [JsonProperty("phase_label")]
    public string? PhaseLabel { get; init; }

    /// <summary>进度百分比（0.0 ~ 100.0）</summary>
    [JsonProperty("progress_percent")]
    public double ProgressPercent { get; init; }

    /// <summary>本次扫描发现的文件总数</summary>
    [JsonProperty("scanned_files")]
    public int ScannedFiles { get; init; }

    /// <summary>本次任务的文件总数</summary>
    [JsonProperty("total_files")]
    public int TotalFiles { get; init; }

    /// <summary>已成功传输的文件数</summary>
    [JsonProperty("transferred_files")]
    public int TransferredFiles { get; init; }

    /// <summary>传输失败的文件数</summary>
    [JsonProperty("error_files")]
    public int ErrorFiles { get; init; }

    // ---- 时间戳 ----

    /// <summary>本次状态采集时间</summary>
    [JsonProperty("status_time")]
    public DateTime StatusTime { get; init; } = DateTime.Now;
}


/// <summary>
/// 定时状态上报
///   1. 按照运行状态自动切换上报间隔（空闲 30s / 运行中 15s）
///   2. 状态变化时支持即时触发上报
///   3. 失败不阻塞主流程，只记录最近一次错误
///
/// 生命周期：
///   AppRuntime 启动后调用 Start()，关闭前调用 Stop()。
///   上报循环在后台 Task 中运行，通过 CancellationToken 控制停止。
///
/// 线程安全：
///   Start/Stop 应串行调用。上报循环在线程池上执行。
///   PayloadFactory 由 AppRuntime 提供，需保证线程安全。
/// </summary>
public class StatusReportService : IDisposable
{
    // ======================================================================
    // 字段
    // ======================================================================
    private readonly StatusReportConfig _config;
    private readonly Func<StatusPayload> _payloadFactory;
    private readonly HttpClient _httpClient;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;


    /// <summary>日志输出</summary>
    public event Action<string>? OnLog;

    /// <summary>最近一次上报失败的错误消息，成功上报后清空</summary>
    public string? LastReportError { get; private set; }

    /// <summary>上报循环是否正在运行</summary>
    public bool IsRunning
    {
        get { lock (_lock) return _cts != null; }
    }

    /// <param name="config">status_report 配置块</param>
    /// <param name="payloadFactory">返回当前最新 StatusPayload 的委托。
    /// 每次上报时调用，AppRuntime 需确保实现是线程安全的（只读字段访问即可）。</param>
    public StatusReportService(StatusReportConfig config, Func<StatusPayload> payloadFactory)
    {
        _config = config;
        _payloadFactory = payloadFactory;

        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, config.RequestTimeoutSeconds));

        if (!string.IsNullOrEmpty(config.AuthToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.AuthToken);
        }

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "NAS-Mover/StatusReport");
    }


    /// <summary>
    /// 启动上报循环。首次上报会在启动后立即触发一次。
    /// 如果已运行则忽略。
    /// </summary>
    public void Start()
    {
        if (!_config.Enabled)
        {
            Log("状态上报未启用");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.EndpointUrl))
        {
            Log("状态上报 URL 为空，跳过启动");
            return;
        }

        lock (_lock)
        {
            if (_cts != null)
            {
                Log("状态上报已在运行中");
                return;
            }

            _cts = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_cts.Token);
        }

        Log("状态上报已启动");
    }

    /// <summary>
    /// 停止上报循环。取消正在等待的 Task.Delay 和 HTTP 请求。
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        Log("状态上报已停止");
    }

    /// <summary>
    /// 立即触发一次异步上报。
    /// 用于状态变化时即时通知远端，不阻塞调用方。
    /// </summary>
    public void ReportNow()
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.EndpointUrl))
            return;

        // fire-and-forget —— 不等待结果
        _ = SendReportAsync();
    }



    /// <summary>
    /// 后台上报循环主体。
    /// 每次发送后根据当前模式决定下一次等待间隔。
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // 启动后立即上报一次
            await SendReportAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                // 根据当前运行模式决定间隔
                int intervalSec = ResolveInterval();
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);

                await SendReportAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止，不需要处理
        }
        catch (Exception ex)
        {
            // 防止循环意外退出
            Log($"上报循环异常退出: {ex.Message}");
        }
    }

    /// <summary>
    /// 执行一次上报：调用 PayloadFactory → 序列化 → POST。
    /// 异常不抛出，只记日志。
    /// </summary>
    private async Task SendReportAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = _payloadFactory();

            string json = JsonConvert.SerializeObject(payload, Formatting.None,
                new JsonSerializerSettings
                {
                    Converters = { new StringEnumConverter() }
                });

            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_config.EndpointUrl, content, ct);

            if (response.IsSuccessStatusCode)
            {
                LastReportError = null;
            }
            else
            {
                string responseBody = await response.Content.ReadAsStringAsync(ct);
                LastReportError = $"HTTP {(int)response.StatusCode}: {responseBody}";
                Log($"上报失败: {LastReportError}");
            }
        }
        catch (OperationCanceledException)
        {
            // 取消不是错误，不做任何处理
        }
        catch (HttpRequestException ex)
        {
            LastReportError = $"HTTP 请求失败: {ex.Message}";
            Log($"上报 HTTP 异常: {ex.Message}");
        }
        catch (Exception ex)
        {
            LastReportError = ex.Message;
            Log($"上报异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 根据当前运行模式决定上报间隔。
    /// 运行中（scanning/transferring/pausing/paused/stopping）用高频间隔，
    /// 空闲（idle/completed/error）用低频间隔。
    /// </summary>
    private int ResolveInterval()
    {
        try
        {
            var mode = _payloadFactory().Mode;
            bool isActive = mode is AppMode.Scanning or AppMode.Transferring
                or AppMode.Pausing or AppMode.Paused or AppMode.Stopping;

            return isActive
                ? Math.Max(1, _config.ActiveReportIntervalSeconds)
                : Math.Max(1, _config.HeartbeatIntervalSeconds);
        }
        catch
        {
            // PayloadFactory 可能抛异常（如果 AppRuntime 状态还没准备好）
            return Math.Max(1, _config.HeartbeatIntervalSeconds);
        }
    }



    private void Log(string msg) => OnLog?.Invoke($"[StatusReport] {msg}");

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}
