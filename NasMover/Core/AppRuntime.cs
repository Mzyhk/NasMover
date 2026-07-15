namespace NasMover.Core;


/// <summary>
/// 应用程序运行时状态，严格遵循以下状态机：
///
/// Idle → Scanning → Transferring → Pausing → Paused
///                                 → Completed
///                                 → Error
/// Paused → Transferring（继续）
/// 任意态 → Stopping → Idle（终止清空）
///
/// 互斥性：同一时间只有一个运行态（扫描/传输/暂停）可以生效。
/// </summary>
public enum AppMode
{
    /// <summary>空闲，等待用户操作</summary>
    Idle,
    /// <summary>正在扫描目录</summary>
    Scanning,
    /// <summary>正在传输文件</summary>
    Transferring,
    /// <summary>正在暂停中（等待当前传输的文件完成）</summary>
    Pausing,
    /// <summary>已暂停，剩余文件等待继续</summary>
    Paused,
    /// <summary>正在停止中（等待所有操作结束）</summary>
    Stopping,
    /// <summary>传输全部完成</summary>
    Completed,
    /// <summary>发生不可恢复的错误</summary>
    Error
}


/// <summary>
/// 应用程序运行时核心类。
///   1. 管理状态机（Idle → Scanning → Transferring → ...）
///   2. 编排工作流（加载配置 → 扫描 → 传输 → 完成）
///   3. 提供开始/停止/暂停/恢复命令
///   4. 触发事件供 UI 和状态上报模块订阅。
/// </summary>
public class AppRuntime
{
    // 状态字段
    private AppMode _currentMode = AppMode.Idle;

    // 状态锁：保护 _currentMode 的读写，因为 Worker 回调可能在后台线程
    private readonly object _stateLock = new();

    // 配置
    private AppConfig? _config;
    private string? _configError;

    // 工作流控制
    /// <summary>用于取消当前工作流的 CancellationTokenSource</summary>
    private CancellationTokenSource? _workCts;

    // 暂停/恢复支持
    /// <summary>最近一次扫描的全部候选文件</summary>
    private IReadOnlyList<ScanCandidate>? _allCandidates;

    /// <summary>已完成的文件路径集合（用于计算暂停后剩余文件）</summary>
    private readonly HashSet<string> _completedFiles = new();

    /// <summary>保护 _completedFiles 的锁（多 Worker 同时完成时线程安全）</summary>
    private readonly object _completedLock = new();

    /// <summary>暂停后剩余未传输的文件（由 Pausing 状态计算，Paused 状态持有）</summary>
    private List<ScanCandidate>? _pendingFiles;

    // 状态上报
    /// <summary>定时状态上报服务，由 LoadConfig 创建并启动</summary>
    private StatusReportService? _statusReport;

    // 云控调度
    /// <summary>云控后台调度循环的 CancellationTokenSource</summary>
    private CancellationTokenSource? _cloudCts;
    /// <summary>云控后台调度循环 Task</summary>
    private Task? _cloudLoopTask;
    /// <summary>上次云控检查的日期，避免同一调度窗口内重复触发</summary>
    private DateTime _lastCloudCheckDate = DateTime.MinValue;

    // OTA 更新调度
    /// <summary>更新检查后台调度循环的 CancellationTokenSource</summary>
    private CancellationTokenSource? _updateCts;
    /// <summary>更新检查后台调度循环 Task</summary>
    private Task? _updateLoopTask;
    /// <summary>上次更新检查的日期，避免同一调度窗口内重复触发</summary>
    private DateTime _lastUpdateCheckDate = DateTime.MinValue;

    // 事件（UI / 状态上报 订阅）
    /// <summary>状态机状态变化时触发</summary>
    public event Action<AppMode>? ModeChanged;

    /// <summary>传输进度更新</summary>
    public event Action<TransferProgress>? TransferProgress;

    /// <summary>配置加载成功</summary>
    public event Action<AppConfig>? ConfigLoaded;

    /// <summary>配置加载失败</summary>
    public event Action<string>? ConfigError;

    /// <summary>日志消息，用于 UI 日志区域显示</summary>
    public event Action<string>? LogMessage;


    /// <summary>当前状态（线程安全读取）</summary>
    public AppMode CurrentMode
    {
        get { lock (_stateLock) return _currentMode; }
    }

    /// <summary>当前加载的配置</summary>
    public AppConfig? Config => _config;

    /// <summary>配置加载时的错误消息</summary>
    public string? ConfigIssue => _configError;

    /// <summary>最近一次传输的最终进度（UI 显示用）</summary>
    public TransferProgress? LastProgress { get; private set; }

    /// <summary>配置文件的完整路径</summary>
    public string? ConfigFilePath => ConfigLoader.ConfigFilePath;

    /// <summary>配置文件所在目录</summary>
    public string? ConfigDirectory =>
        ConfigFilePath != null ? Path.GetDirectoryName(ConfigFilePath) : null;


    /// <summary>
    /// 加载并验证 config.json。
    /// 启动时调用一次，云控更新配置后也可调用。
    /// </summary>
    public bool LoadConfig()
    {
        try
        {
            Log("正在加载配置...");
            _config = ConfigLoader.LoadConfig();
            _configError = null;
            Log($"配置加载成功，机种: {_config.HotConfig.ActiveModel}");

            // 重启状态上报（如果配置变化）
            RestartStatusReport();

            // 重启云控调度器（如果配置变化）
            RestartCloudScheduler();

            // 重启 OTA 更新调度器（如果配置变化）
            RestartUpdateScheduler();

            ConfigLoaded?.Invoke(_config);
            return true;
        }
        catch (Exception ex)
        {
            _configError = ex.Message;
            Log($"配置加载失败: {ex.Message}");
            ConfigError?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 开始扫描传输流程。
    /// 可以从 Idle、Completed 或 Error 状态调用。
    /// </summary>
    public async Task StartScanAsync()
    {
        // ---------------------------------------------------------------
        // 前置条件检查
        // ---------------------------------------------------------------
        // 只能从 Idle、Completed、Error 三个"可启动"状态进入
        AppMode current;
        lock (_stateLock) current = _currentMode;

        if (current != AppMode.Idle && current != AppMode.Completed && current != AppMode.Error)
        {
            Log($"当前状态为 {current}，无法开始新的传输");
            return;
        }

        // 确保配置已加载
        if (_config == null && !LoadConfig())
        {
            Log("配置未加载，无法开始扫描");
            return;
        }

        // 获取当前激活的机种配置
        var modelConfig = _config!.HotConfig.ActiveModelConfig;
        if (modelConfig == null)
        {
            string msg = $"active_model [{_config.HotConfig.ActiveModel}] 在 models 中不存在";
            Log(msg);
            SetMode(AppMode.Error);
            return;
        }

        // 检查是否有可用的 base_dirs
        if (modelConfig.BaseDirs == null || modelConfig.BaseDirs.Count == 0)
        {
            Log("当前机种没有配置 base_dirs，无法扫描");
            SetMode(AppMode.Error);
            return;
        }

        // ---------------------------------------------------------------
        // 进入 Scanning 状态
        // ---------------------------------------------------------------
        // 创建新的 CancellationTokenSource，供 RequestStop / RequestPause 使用
        _completedFiles.Clear();
        _pendingFiles = null;
        _allCandidates = null;
        
        var cts = new CancellationTokenSource();
        _workCts = cts;

        SetMode(AppMode.Scanning);

        try
        {
            // ---------------------------------------------------------------
            // 阶段一：扫描
            // ---------------------------------------------------------------
            Log("开始扫描目录...");

            var scanResult = await ScanService.ScanAsync(
                modelConfig,
                cts.Token,
                msg => Log(msg));

            cts.Token.ThrowIfCancellationRequested();

            if (scanResult.Files.Count == 0)
            {
                Log("扫描完成，没有发现需要传输的文件");
                SetMode(AppMode.Completed);
                return;
            }

            Log($"扫描完成，发现 {scanResult.Files.Count} 个文件");

            // ---------------------------------------------------------------
            // 阶段二：传输
            // ---------------------------------------------------------------
            SetMode(AppMode.Transferring);

            _allCandidates = scanResult.Files;

            var engine = new TransferEngine(_config.HotConfig, _config.ColdConfig.MachineId);

            engine.OnProgress += p =>
            {
                LastProgress = p;
                TransferProgress?.Invoke(p);
            };
            engine.OnFileCompleted += OnFileCompleted;
            engine.OnLog += msg => Log(msg);

            var finalProgress = await engine.ExecuteAsync(
                scanResult.Files,
                cts.Token);

            LastProgress = finalProgress;

            cts.Token.ThrowIfCancellationRequested();

            Log($"传输完成: {finalProgress.CompletedFiles} 成功, {finalProgress.FailedFiles} 失败");
            SetMode(AppMode.Completed);
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock)
            {
                if (_currentMode == AppMode.Stopping)
                {
                    _completedFiles.Clear();
                    _allCandidates = null;
                    _pendingFiles = null;
                    Log("已停止");
                    SetMode(AppMode.Idle);
                }
                else if (_currentMode == AppMode.Pausing)
                {
                    ComputePendingFiles();
                    Log($"已暂停，剩余 {_pendingFiles?.Count ?? 0} 个文件待传输");
                    SetMode(AppMode.Paused);
                }
                else
                {
                    _completedFiles.Clear();
                    _allCandidates = null;
                    _pendingFiles = null;
                    SetMode(AppMode.Idle);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"传输出错: {ex.Message}");
            SetMode(AppMode.Error);
        }
        finally
        {
            // 确保 dispose 的同时清理 _workCts 引用
            cts.Dispose();
            if (_workCts == cts) _workCts = null;
        }
    }

    /// <summary>
    /// 请求停止传输。可以从任意非 Idle 状态调用。
    /// 设置状态为 Stopping 并取消工作流。
    /// 工作流收到取消信号后会干净地退出并回到 Idle。
    /// </summary>
    public void RequestStop()
    {
        // 已完成/空闲/停止中/出错 → 无需停止
        if (CurrentMode is AppMode.Idle or AppMode.Stopping
            or AppMode.Completed or AppMode.Error)
            return;

        Log("正在停止...");
        SetMode(AppMode.Stopping);
        _workCts?.Cancel();
    }

    /// <summary>
    /// 请求暂停传输。仅 Transferring 状态下有效。
    /// 设置状态为 Pausing 并取消工作流。
    /// 工作流在安全停止后进入 Paused 状态。
    /// </summary>
    public void RequestPause()
    {
        if (CurrentMode != AppMode.Transferring)
        {
            Log($"当前状态为 {CurrentMode}，无法暂停（仅在 Transferring 时可暂停）");
            return;
        }

        Log("正在暂停...");
        SetMode(AppMode.Pausing);
        _workCts?.Cancel();
    }

    /// <summary>
    /// 从暂停状态恢复传输。仅 Paused 状态下有效。
    /// 用之前保存的剩余文件列表重新启动工作流。
    /// </summary>
    public async Task RequestResumeAsync()
    {
        if (CurrentMode != AppMode.Paused)
        {
            Log($"当前状态为 {CurrentMode}，无法恢复（仅在 Paused 时可恢复）");
            return;
        }

        // 获取暂停时保存的剩余文件
        List<ScanCandidate>? remaining;
        lock (_stateLock)
        {
            remaining = _pendingFiles;
            _pendingFiles = null;
        }

        if (remaining == null || remaining.Count == 0)
        {
            Log("没有剩余文件需要传输");
            SetMode(AppMode.Completed);
            return;
        }

        Log($"恢复传输，剩余 {remaining.Count} 个文件");

        // 重置已完成跟踪（重新开始记数）
        _completedFiles.Clear();
        _allCandidates = remaining;

        var cts = new CancellationTokenSource();
        _workCts = cts;

        SetMode(AppMode.Transferring);

        try
        {
            var engine = new TransferEngine(_config!.HotConfig, _config.ColdConfig.MachineId);

            engine.OnProgress += p =>
            {
                LastProgress = p;
                TransferProgress?.Invoke(p);
            };
            engine.OnFileCompleted += OnFileCompleted;
            engine.OnLog += msg => Log(msg);

            var finalProgress = await engine.ExecuteAsync(remaining, cts.Token);
            LastProgress = finalProgress;

            cts.Token.ThrowIfCancellationRequested();

            Log($"恢复传输完成: {finalProgress.CompletedFiles} 成功, {finalProgress.FailedFiles} 失败");
            SetMode(AppMode.Completed);
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock)
            {
                if (_currentMode == AppMode.Stopping)
                {
                    _completedFiles.Clear();
                    _allCandidates = null;
                    _pendingFiles = null;
                    SetMode(AppMode.Idle);
                }
                else if (_currentMode == AppMode.Pausing)
                {
                    ComputePendingFiles();
                    SetMode(AppMode.Paused);
                }
                else
                {
                    SetMode(AppMode.Idle);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"恢复传输出错: {ex.Message}");
            SetMode(AppMode.Error);
        }
        finally
        {
            cts.Dispose();
            if (_workCts == cts) _workCts = null;
        }
    }


    /// <summary>
    /// 设置新的运行状态并触发 ModeChanged 事件。
    /// 所有状态变更必须通过此方法，确保事件一定被触发。
    /// </summary>
    private void SetMode(AppMode newMode)
    {
        AppMode oldMode;
        lock (_stateLock)
        {
            oldMode = _currentMode;
            _currentMode = newMode;
        }

        // 状态有变化时才触发事件
        if (oldMode != newMode)
        {
            Log($"状态: {oldMode} → {newMode}");
            ModeChanged?.Invoke(newMode);
        }
    }

    /// <summary>
    /// 当 TransferEngine 完成一个文件时调用。
    /// 将完成的文件路径记录到 _completedFiles，用于暂停后计算剩余文件。
    /// </summary>
    private void OnFileCompleted(TransferResult result)
    {
        if (result.Success)
        {
            lock (_completedLock)
            {
                _completedFiles.Add(result.SourcePath);
            }
        }
    }

    /// <summary>
    /// 计算暂停后剩余未传输的文件列表。
    /// 在进入 Paused 状态前调用。
    /// </summary>
    private void ComputePendingFiles()
    {
        if (_allCandidates == null)
        {
            _pendingFiles = null;
            return;
        }

        lock (_completedLock)
        {
            // 过滤出未完成的文件
            _pendingFiles = _allCandidates
                .Where(c => !_completedFiles.Contains(c.SourcePath))
                .ToList();

            Log($"暂停: 共 {_allCandidates.Count} 个文件, 已完成 {_completedFiles.Count} 个, 剩余 {_pendingFiles.Count} 个");
        }
    }

    // 私有方法：日志
    private void Log(string message)
    {
        LogMessage?.Invoke($"[AppRuntime] {message}");
    }

    // ======================================================================
    // 状态上报
    // ======================================================================

    /// <summary>
    /// 停止旧的状态上报服务（如有），按当前配置创建并启动新的。
    /// 配置热加载或云控更新配置后调用。
    /// </summary>
    private void RestartStatusReport()
    {
        // 停止旧服务
        if (_statusReport != null)
        {
            _statusReport.OnLog -= Log;
            _statusReport.Dispose(); // 内部会 Stop() + Dispose HttpClient
            _statusReport = null;
        }

        if (_config?.StatusReport == null)
        {
            Log("无状态上报配置，跳过");
            return;
        }

        _statusReport = new StatusReportService(_config.StatusReport, BuildStatusPayload);
        _statusReport.OnLog += Log;
        _statusReport.Start();
    }

    /// <summary>
    /// 构建状态上报 Payload。
    /// 每次上报循环触发时由 StatusReportService 调用。
    /// 只读取字段（线程安全），不持有锁。
    /// </summary>
    private StatusPayload BuildStatusPayload()
    {
        var config = _config;
        var progress = LastProgress;
        var candidates = _allCandidates;

        return new StatusPayload
        {
            MachineId = config?.ColdConfig?.MachineId ?? "",
            MachineGroup = config?.CloudControl?.MachineGroup ?? "",
            AppVersion = "1.0.0",
            ActiveModel = config?.HotConfig?.ActiveModel ?? "",
            Mode = CurrentMode,
            NasConnected = true, // TODO: 实现 NAS 可达性探测
            ConfigIssue = _configError,
            LastError = null,    // TODO: 跟踪最近一次错误
            PhaseLabel = progress?.CurrentFile,
            ProgressPercent = (progress?.Percent ?? 0d) * 100.0,
            ScannedFiles = candidates?.Count ?? 0,
            TotalFiles = progress?.TotalFiles ?? candidates?.Count ?? 0,
            TransferredFiles = progress?.CompletedFiles ?? 0,
            ErrorFiles = progress?.FailedFiles ?? 0,
            StatusTime = DateTime.Now
        };
    }

    // ======================================================================
    // 云控自动调度
    // ======================================================================

    /// <summary>
    /// 重启云控调度器：停止旧循环，按当前配置启动新循环。
    /// 配置热加载后自动调用。
    /// </summary>
    private void RestartCloudScheduler()
    {
        // 停止旧循环
        if (_cloudCts != null)
        {
            _cloudCts.Cancel();
            _cloudCts.Dispose();
            _cloudCts = null;
        }
        _cloudLoopTask = null;

        // 检查是否需要启动调度
        var cc = _config?.CloudControl;
        if (cc == null || !cc.Enabled || cc.CheckMode == RemoteCheckMode.Manual)
        {
            Log("云控调度: 未启用或 Manual 模式，跳过");
            return;
        }

        _cloudCts = new CancellationTokenSource();
        var token = _cloudCts.Token;

        _cloudLoopTask = Task.Run(() => CloudSchedulerLoopAsync(token), token);
        Log($"云控调度已启动: 模式={cc.CheckMode}");
    }

    /// <summary>
    /// 云控后台调度循环。
    /// 每分钟检查一次当前时间是否匹配调度条件，匹配则触发 CheckCloudControlAsync。
    /// 首次检查延迟 30 秒启动（给网络/系统稳定时间）。
    /// </summary>
    private async Task CloudSchedulerLoopAsync(CancellationToken ct)
    {
        try
        {
            // 启动后先等 30 秒再首次检查
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ShouldCheckCloudNow())
                    {
                        Log("云控调度: 触发定时检查");
                        await CheckCloudControlAsync();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"云控调度: 检查异常 (已隔离): {ex.Message}");
                }

                // 每分钟检查一次时间条件
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止，无需处理
        }
        catch (Exception ex)
        {
            Log($"云控调度: 循环异常退出: {ex.Message}");
        }
    }

    /// <summary>
    /// 判断当前时间是否满足云控调度条件。
    /// Daily：当前小时 == check_hour
    /// Weekly：当前星期几 == check_weekday 且 当前小时 == check_hour
    ///
    /// 同一自然日内只触发一次（_lastCloudCheckDate 去重）。
    /// </summary>
    private bool ShouldCheckCloudNow()
    {
        var cc = _config?.CloudControl;
        if (cc == null || !cc.Enabled) return false;

        var now = DateTime.Now;

        // 当天已经检查过了 → 跳过
        if (_lastCloudCheckDate.Date == now.Date)
            return false;

        bool timeMatched = cc.CheckMode switch
        {
            RemoteCheckMode.Daily when cc.CheckHour.HasValue =>
                now.Hour == cc.CheckHour.Value,

            RemoteCheckMode.Weekly when cc.CheckWeekday.HasValue && cc.CheckHour.HasValue =>
                // config: 1=Sunday … 7=Saturday
                // DayOfWeek: Sunday=0 … Saturday=6
                (int)now.DayOfWeek == (cc.CheckWeekday.Value - 1) % 7
                && now.Hour == cc.CheckHour.Value,

            _ => false
        };

        if (timeMatched)
            _lastCloudCheckDate = now;

        return timeMatched;
    }

    // ======================================================================
    // OTA 更新调度
    // ======================================================================

    /// <summary>
    /// 重启 OTA 更新调度器：停止旧循环，按当前配置启动新循环。
    /// </summary>
    private void RestartUpdateScheduler()
    {
        if (_updateCts != null)
        {
            _updateCts.Cancel();
            _updateCts.Dispose();
            _updateCts = null;
        }
        _updateLoopTask = null;

        var uc = _config?.UpdateControl;
        if (uc == null || !uc.Enabled || uc.CheckMode == RemoteCheckMode.Manual)
        {
            Log("OTA 更新调度: 未启用或 Manual 模式，跳过");
            return;
        }

        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;

        _updateLoopTask = Task.Run(() => UpdateSchedulerLoopAsync(token), token);
        Log($"OTA 更新调度已启动: 模式={uc.CheckMode}");
    }

    /// <summary>
    /// OTA 更新后台调度循环。
    /// </summary>
    private async Task UpdateSchedulerLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ShouldCheckUpdateNow())
                    {
                        Log("OTA 更新调度: 触发定时检查");
                        await CheckForUpdateAsync();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"OTA 更新调度: 检查异常 (已隔离): {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            Log($"OTA 更新调度: 循环异常退出: {ex.Message}");
        }
    }

    /// <summary>
    /// 判断当前时间是否满足 OTA 更新调度条件。
    /// </summary>
    private bool ShouldCheckUpdateNow()
    {
        var uc = _config?.UpdateControl;
        if (uc == null || !uc.Enabled) return false;

        var now = DateTime.Now;
        if (_lastUpdateCheckDate.Date == now.Date)
            return false;

        bool timeMatched = uc.CheckMode switch
        {
            RemoteCheckMode.Daily when uc.CheckHour.HasValue =>
                now.Hour == uc.CheckHour.Value,

            RemoteCheckMode.Weekly when uc.CheckWeekday.HasValue && uc.CheckHour.HasValue =>
                (int)now.DayOfWeek == (uc.CheckWeekday.Value - 1) % 7
                && now.Hour == uc.CheckHour.Value,

            _ => false
        };

        if (timeMatched)
            _lastUpdateCheckDate = now;

        return timeMatched;
    }

    /// <summary>
    /// 手动触发 OTA 更新检查。
    /// 返回结果描述文本。
    /// </summary>
    public async Task<string> CheckForUpdateAsync()
    {
        if (_config?.UpdateControl == null || !_config.UpdateControl.Enabled)
        {
            Log("OTA 更新未启用");
            return "更新未启用";
        }

        try
        {
            var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            if (appDir == null) return "无法获取程序目录";

            Log("正在检查 OTA 更新...");

            var service = new UpdateService(_config.UpdateControl, appDir, "1.0.0");
            service.OnLog += Log;

            var result = await service.CheckForUpdateAsync();

            if (result.HasUpdate)
            {
                Log($"发现新版本: {result.CurrentVersion} → {result.NewVersion}");

                // 自动下载更新包（后台静默下载）
                if (!string.IsNullOrEmpty(result.PackageUrl))
                {
                    Log("正在后台下载更新包...");
                    _ = DownloadUpdatePackageAsync(service, result);
                }

                return result.Message ?? $"发现新版本 {result.NewVersion}";
            }

            if (!string.IsNullOrEmpty(result.Message))
            {
                Log($"OTA 更新检查: {result.Message}");
                return result.Message;
            }

            Log("OTA 更新检查完成，已是最新版本");
            return "已是最新版本";
        }
        catch (Exception ex)
        {
            Log($"OTA 更新检查失败: {ex.Message}");
            return $"检查失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 后台下载更新包（fire-and-forget，不阻塞调用方）。
    /// </summary>
    private async Task DownloadUpdatePackageAsync(UpdateService service, UpdateCheckResult result)
    {
        var path = await service.DownloadUpdateAsync(
            result.PackageUrl!,
            result.Sha256);

        if (path != null)
        {
            Log($"更新包已下载到: {path}");
            Log("OTA 更新: 准备就绪，下次重启时将应用更新");
            // TODO: 通知 UI 显示"可更新"状态 + 重启按钮
        }
    }

    // ======================================================================
    // 配置管理
    // ======================================================================

    /// <summary>
    /// 从磁盘重新加载配置并替换当前配置。
    /// </summary>
    public bool ReloadConfig()
    {
        Log("正在重新加载配置...");
        return LoadConfig();
    }

    /// <summary>
    /// 将当前配置保存到磁盘。
    /// </summary>
    public bool SaveConfig()
    {
        if (_config == null)
        {
            Log("配置未加载，无法保存");
            return false;
        }

        try
        {
            ConfigLoader.SaveConfig(_config);
            Log("配置已保存");
            return true;
        }
        catch (Exception ex)
        {
            Log($"配置保存失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 手动触发云控检查。
    /// 返回结果描述文本。
    /// </summary>
    public async Task<string> CheckCloudControlAsync()
    {
        if (_config?.CloudControl == null || !_config.CloudControl.Enabled)
        {
            Log("云控未启用");
            return "云控未启用";
        }

        try
        {
            var configDir = ConfigDirectory;
            if (configDir == null) return "无法获取配置目录";

            Log("开始检查云控...");

            var service = new CloudControlService(
                _config.CloudControl,
                configDir,
                _config.ColdConfig.MachineId,
                _config.CloudControl.MachineGroup,
                "1.0.0");
            service.OnLog += Log; // 将 CloudControlService 的详细日志接入 AppRuntime 日志流

            var result = await service.CheckForUpdateAsync();

            if (result.HasUpdate)
            {
                Log($"云控检测到更新: {result.Message}");

                if (result.HotConfigChanged && result.NewConfig != null)
                {
                    _config = result.NewConfig;
                    Log("云控热配置已应用");
                    ConfigLoaded?.Invoke(_config);
                }

                return result.Message ?? "有新配置";
            }

            // 没有更新但有详细消息（如连接失败、scope 不匹配等）→ 透出
            if (!string.IsNullOrEmpty(result.Message))
            {
                Log($"云控检查: {result.Message}");
                return result.Message;
            }

            Log("云控检查完成，已是最新配置");
            return "已是最新配置";
        }
        catch (Exception ex)
        {
            Log($"云控检查失败: {ex.Message}");
            return $"检查失败: {ex.Message}";
        }
    }
}
