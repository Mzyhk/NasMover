using System.Threading.Channels;

namespace NasMover.Core;

/// <summary>
/// TransferEngine 的实时进度快照，用于推送到 UI 展示或状态上报。
/// 每次文件传输完成时更新。
/// </summary>
public class TransferProgress
{
    /// <summary>本次任务的总文件数</summary>
    public int TotalFiles { get; init; }

    /// <summary>已成功完成的文件数（Completed + AlreadySynced）</summary>
    public int CompletedFiles { get; init; }

    /// <summary>失败的文件数</summary>
    public int FailedFiles { get; init; }

    /// <summary>总字节数（所有文件大小之和）</summary>
    public long TotalBytes { get; init; }

    /// <summary>已传输字节数</summary>
    public long TransferredBytes { get; init; }

    /// <summary>当前正在处理的文件名（用于 UI 显示"正在传输：xxx.jpg"）</summary>
    public string? CurrentFile { get; init; }

    /// <summary>进度百分比（0.0 ~ 1.0）</summary>
    public double Percent => TotalFiles > 0 ? (double)CompletedFiles / TotalFiles : 0;

    /// <summary>是否全部完成</summary>
    public bool IsCompleted => CompletedFiles + FailedFiles >= TotalFiles;
}

/// <summary>
/// 传输引擎，核心职责：
///   1. 接收 ScanCandidate 列表
///   2. 通过 Channel 分发给 N 个并发 Worker
///   3. 每个 Worker 调用 TransferService.TransferFileAsync 执行 5 步转移
///   4. 汇总进度、按事件推送给调用方（UI / 状态上报）
/// 线程模型：
///   - 主线程：写入 Channel + 启动 Worker
///   - N 个 Worker 线程：从 Channel 读取并处理
///   - 所有 Worker 结束后，主线程返回完整的传输结果
/// </summary>
public class TransferEngine
{
    // 每个 Worker 在拉取不到新任务时最多等待 500ms 后继续尝试
    // 这样即使 Channel 已空但还未调用 Complete()，Worker 能及时退出
    private static readonly TimeSpan WorkerPollTimeout = TimeSpan.FromMilliseconds(500);

    // 配置字段（从 HotConfig 传入）
    private readonly string _nasBasePath;
    private readonly string _machineId;
    private readonly string _activeModel;
    private readonly bool _deleteAfterTransfer;
    private readonly int _workerCount;

    // 运行时状态
    private readonly Channel<ScanCandidate> _channel;
    private readonly CancellationTokenSource _cts = new();

    // 进度跟踪（Interlocked 保证线程安全）
    private int _completedCount;
    private int _failedCount;
    private long _transferredBytes;
    private string? _currentFile;

    // 外部订阅者
    /// <summary>每次文件传输完成时触发（无论是成功还是失败）</summary>
    public event Action<TransferProgress>? OnProgress;
    /// <summary>每次文件传输完成时触发，携带具体结果（含源路径），用于 AppRuntime 跟踪已完成文件</summary>
    public event Action<TransferResult>? OnFileCompleted;
    /// <summary>传输引擎完成全部工作时触发</summary>
    public event Action<TransferProgress>? OnCompleted;
    /// <summary>日志输出</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// 创建传输引擎实例。
    /// 每次扫描传输任务创建一个新的 TransferEngine 实例，不要复用。
    /// </summary>
    public TransferEngine(HotConfig hotConfig, string machineId)
    {
        _nasBasePath = hotConfig.NasBasePath;
        _machineId = machineId;
        _activeModel = hotConfig.ActiveModel;
        _deleteAfterTransfer = hotConfig.DeleteAfterTransfer;
        _workerCount = hotConfig.EffectiveWorkerThreads;

        // 创建有界 Channel，容量为 Worker 数的 2 倍
        _channel = Channel.CreateBounded<ScanCandidate>(
            new BoundedChannelOptions(_workerCount * 2)
            {
                // 如果 Channel 满了，写入方（生产者）异步等待消费者读出后再写入
                FullMode = BoundedChannelFullMode.Wait,
                // 允许多个读者（N 个 Worker）
                SingleReader = false,
                // 只有一个写者（主线程写入）
                SingleWriter = true
            });
    }


    /// <summary>
    /// 开始执行传输任务。
    /// 先将所有文件写入 Channel，然后启动 Worker 消费。
    /// 这是一个异步方法，在所有 Worker 完成后返回。
    /// </summary>
    /// <param name="candidates">扫描结果中的文件候选列表</param>
    /// <param name="cancellationToken">来自外部的取消令牌</param>
    /// <returns>传输结果汇总</returns>
    public async Task<TransferProgress> ExecuteAsync(
        IReadOnlyList<ScanCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0)
        {
            Log("没有待传输的文件");
            var emptyProgress = new TransferProgress();
            OnCompleted?.Invoke(emptyProgress);
            return emptyProgress;
        }

        Log($"开始传输: {candidates.Count} 个文件, {_workerCount} 个 Worker");

        // 计算总字节数（用于最终进度汇总）
        var totalBytes = candidates.Sum(c => c.FileSize);

        // 第一步：先启动 Worker，让它们开始消费 Channel
        // 必须放在写入之前，否则 Bounded Channel 满后会死锁（写者等空间，但消费者还没启动）
        var workerTasks = new Task[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            int workerId = i + 1;
            workerTasks[i] = RunWorkerAsync(workerId, cancellationToken);
        }

        // 第二步：把所有 ScanCandidate 写入 Channel
        // Channel 是 Bounded + Wait 模式，满了会异步等待 Worker 消费。
        // 用 Task.Run 避免阻塞主线程。
        var writer = _channel.Writer;
        await Task.Run(async () =>
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(candidate, cancellationToken);
            }
        }, cancellationToken);

        // 标记写入完成——所有 Worker 读到这个信号后,
        // 在处理完队列中剩余的任务后退出循环
        writer.Complete();

        Log($"Channel 写入完成，等待 {_workerCount} 个 Worker 结束");

        // ---------------------------------------------------------------
        // 第三步：等待所有 Worker 完成
        // ---------------------------------------------------------------
        await Task.WhenAll(workerTasks);

        // ---------------------------------------------------------------
        // 第四步：生成最终进度并通知
        // ---------------------------------------------------------------
        var finalProgress = new TransferProgress
        {
            TotalFiles = candidates.Count,
            CompletedFiles = Interlocked.CompareExchange(ref _completedCount, 0, 0),
            FailedFiles = Interlocked.CompareExchange(ref _failedCount, 0, 0),
            TotalBytes = totalBytes,
            TransferredBytes = Interlocked.Read(ref _transferredBytes)
        };

        Log($"传输完成: {finalProgress.CompletedFiles} 成功, {finalProgress.FailedFiles} 失败");
        OnCompleted?.Invoke(finalProgress);
        return finalProgress;
    }

    /// <summary>
    /// 单个 Worker 的无限循环：
    ///   1. 等待 Channel 中有可读的数据
    ///   2. 读取一个 ScanCandidate
    ///   3. 调用 TransferService.TransferFileAsync 执行 5 步转移
    ///   4. 更新进度
    ///   5. 回到步骤 1
    ///
    /// 当 Channel 被标记为 Complete 且队列清空后，WaitToReadAsync 返回 false，
    /// Worker 自然退出。
    /// </summary>
    private async Task RunWorkerAsync(int workerId, CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        var logger = CreateWorkerLogger(workerId);

        // 一直循环，直到 Channel 关闭且队列为空
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            // 从 Channel 中读取一个候选文件
            // TryRead 可能返回 false（多 Worker 争抢时被别的 Worker 读走了）
            // 所以用 while 循环反复尝试
            while (reader.TryRead(out var candidate))
            {
                // 更新当前正在处理的文件名（用于 UI 展示）
                string fileName = Path.GetFileName(candidate.SourcePath);
                Interlocked.Exchange(ref _currentFile, fileName);

                // 调用 Progress 事件更新 UI
                NotifyProgress();

                // 构造目标路径
                // 规则：nas_base_path / machine_id / active_model / RelativePath
                // 例如：\\192.168.1.100\Backup\SL_TEST_STATION\MODEL_A_PRO\OK\pic.jpg
                string destPath = Path.Combine(
                    _nasBasePath,
                    _machineId,
                    _activeModel,
                    candidate.RelativePath);

                // 准备 TransferOptions 并调用 5 步转移
                var options = new TransferOptions
                {
                    SourcePath = candidate.SourcePath,
                    DestPath = destPath,
                    DeleteAfterTransfer = _deleteAfterTransfer,
                    CancellationToken = cancellationToken
                };

                // 执行 5 步转移
                var result = await TransferService.TransferFileAsync(options, logger);

                // 更新进度统计
                if (result.Success)
                {
                    Interlocked.Increment(ref _completedCount);
                    Interlocked.Add(ref _transferredBytes, candidate.FileSize);
                }
                else
                {
                    Interlocked.Increment(ref _failedCount);
                    logger?.Invoke($"失败: {result.ErrorMessage}");
                }

                // 触发单文件完成事件（AppRuntime 用它跟踪已完成文件，支持暂停恢复）
                OnFileCompleted?.Invoke(result);

                // 通知进度更新
                NotifyProgress();

                // 检查外部取消（点了"停止"）
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// 立即停止传输引擎。
    /// 调用此方法后，正在传输的文件会继续完成，但队列中未处理的任务会被丢弃。
    /// </summary>
    public void Stop()
    {
        // 取消 CancellationTokenSource，所有 Worker 会收到 OperationCanceledException
        _cts.Cancel();
        // 标记 Channel 写入完成，Worker 读取完剩余任务后退出
        _channel.Writer.TryComplete();
    }

    /// <summary>通知进度监听者</summary>
    private void NotifyProgress()
    {
        var progress = new TransferProgress
        {
            TotalFiles = Math.Max(Interlocked.CompareExchange(ref _completedCount, 0, 0)
                + Interlocked.CompareExchange(ref _failedCount, 0, 0), 1),
            CompletedFiles = Interlocked.CompareExchange(ref _completedCount, 0, 0),
            FailedFiles = Interlocked.CompareExchange(ref _failedCount, 0, 0),
            TransferredBytes = Interlocked.Read(ref _transferredBytes),
            CurrentFile = _currentFile
        };

        OnProgress?.Invoke(progress);
    }

    /// <summary>为每个 Worker 创建专属的日志回调</summary>
    private Action<string>? CreateWorkerLogger(int workerId)
    {
        return msg => OnLog?.Invoke($"[Worker {workerId}] {msg}");
    }

    /// <summary>引擎级日志</summary>
    private void Log(string msg)
    {
        OnLog?.Invoke($"[Engine] {msg}");
    }
}
