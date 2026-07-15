using NasMover.Core.Tools;

namespace NasMover.Core;

/// <summary>
/// 单文件转移结果状态枚举
/// Completed  = 成功完成全部5步
/// AlreadySynced = 远端已有相同文件，跳过复制（优化路径）
/// Failed     = 发生不可恢复的错误
/// </summary>
public enum TransferStatus
{
    Completed,
    AlreadySynced,
    Failed
}

/// <summary>
/// 单文件转移的执行结果。包含是否成功、状态枚举、错误信息和耗时；
/// 调用方（TransferEngine/UI）通过这些字段决定下一步动作和展示
/// </summary>
public class TransferResult
{
    /// <summary>是否成功（Completed 或 AlreadySynced 都算成功）</summary>
    public bool Success => Status != TransferStatus.Failed;

    /// <summary>具体状态</summary>
    public TransferStatus Status { get; init; }

    /// <summary>出错时的异常消息</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>源文件路径（用于日志/UI显示）</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>NAS 目标文件路径</summary>
    public string DestPath { get; init; } = string.Empty;

    /// <summary>实际传输的字节数（AlreadySynced 时为0）</summary>
    public long BytesTransferred { get; init; }

    /// <summary>本次转移总耗时</summary>
    public TimeSpan Duration { get; init; }

    public override string ToString() =>
        $"[{Status}] {Path.GetFileName(SourcePath)} → {DestPath}" +
        (ErrorMessage != null ? $" | ERROR: {ErrorMessage}" : "");
}

/// <summary>
/// 一次文件转移所需的全部参数。调用方（TransferEngine.Worker）
/// 从 ScanCandidate 和当前 HotConfig 组装这个对象传给 TransferService。
/// </summary>
public record TransferOptions
{
    /// <summary>本地源文件的完整路径</summary>
    public required string SourcePath { get; init; }

    /// <summary>NAS 目标文件的完整路径（含文件名）</summary>
    public required string DestPath { get; init; }

    /// <summary>是否在传输成功后删除本地源文件</summary>
    public bool DeleteAfterTransfer { get; init; }

    /// <summary>取消令牌，用于响应暂停/停止指令</summary>
    public CancellationToken CancellationToken { get; init; }
}


/// <summary>
/// 单文件安全转移流水线，严格遵循 5 步流程：
///   1. Copy   - 写入 NAS 临时文件（.tmp）
///   2. Verify - 仅比对大小（不做 Hash）
///   3. Mtime  - 同步修改时间
///   4. Rename - 重命名 .tmp 为正式文件名
///   5. Clean  - 按配置决定是否删除本地源文件
/// 目标已存在且 Size+Mtime 都一致 → 直接完成（AlreadySynced），不复制。
/// </summary>
public static class TransferService
{
    // 临时文件后缀。先写 .tmp 再 rename，防止 NAS 上出现半截文件
    private const string TmpExtension = ".tmp";

    // 大小校验不一致时的最大重试次数。只重试一次，不无限重试
    private const int RetryLimit = 1;


    /// <summary>
    /// 执行一次完整的单文件转移。
    /// 外部调用者（TransferEngine.Worker）调用此方法并处理返回的 TransferResult。
    /// </summary>
    /// <param name="options">源路径/目标路径/是否删除等参数</param>
    /// <param name="logger">可选日志回调，由调用方注入</param>
    public static async Task<TransferResult> TransferFileAsync(
        TransferOptions options,
        Action<string>? logger = null)
    {
        // 记录开始时间，用于计算总耗时
        var startTime = DateTime.UtcNow;

        try
        {
            // 如果 NAS 目标文件已存在，且 Size + LastWriteTime 都与本地一致，
            // 说明之前已经传输过了，直接跳过整个流水线。
            if (IsAlreadySynced(options.SourcePath, options.DestPath))
            {
                // 如果开启了删除且已同步，仍然可以补删本地
                if (options.DeleteAfterTransfer)
                    DeleteLocalFile(options.SourcePath, logger);

                logger?.Invoke($"目标文件:{Path.GetFileName(options.SourcePath)}已存在且一致，跳过复制");
                return new TransferResult
                {
                    Status = TransferStatus.AlreadySynced,
                    SourcePath = options.SourcePath,
                    DestPath = options.DestPath,
                    BytesTransferred = 0,
                    Duration = DateTime.UtcNow - startTime
                };
            }

            // 执行正式流程
            long bytesCopied = await ExecutePipelineAsync(options, logger);

            return new TransferResult
            {
                Status = TransferStatus.Completed,
                SourcePath = options.SourcePath,
                DestPath = options.DestPath,
                BytesTransferred = bytesCopied,
                Duration = DateTime.UtcNow - startTime
            };
        }
        catch (OperationCanceledException)
        {
            // 取消是用户主动行为（点了暂停/停止），不算错误
            return new TransferResult
            {
                Status = TransferStatus.Failed,
                SourcePath = options.SourcePath,
                DestPath = options.DestPath,
                ErrorMessage = "操作被取消",
                Duration = DateTime.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            // 所有未预期的异常统一捕获，不让 Worker 崩溃
            return new TransferResult
            {
                Status = TransferStatus.Failed,
                SourcePath = options.SourcePath,
                DestPath = options.DestPath,
                ErrorMessage = ex.Message,
                Duration = DateTime.UtcNow - startTime
            };
        }
    }


    /// <summary>
    /// 核心流程：Copy → Verify → Mtime → Rename → Clean 五步。
    /// 每一步失败都会抛异常，被外层 TransferFileAsync 的 catch 捕获。
    /// 返回实际复制的字节数。
    /// </summary>
    private static async Task<long> ExecutePipelineAsync(
        TransferOptions options,
        Action<string>? logger)
    {
        // 组装临时文件路径：目标路径 + .tmp 后缀
        // 例如：\\NAS\Backup\pic.jpg → \\NAS\Backup\pic.jpg.tmp
        string tmpPath = options.DestPath + TmpExtension;

        // 确保目标目录存在
        string? destDir = Path.GetDirectoryName(options.DestPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(PathHelper.ToExtendedLength(destDir));
        }


        // 第 1 步：Copy —— 本地文件 → NAS .tmp 文件
        // 使用 FileStream 异步复制，不用 File.Copy 是为了能在复制过程中响应 CancellationToken。
        logger?.Invoke($"[Copy]复制: {options.SourcePath} → {tmpPath}");

        long bytesCopied;
        await using (var sourceStream = new FileStream(
            PathHelper.ToExtendedLength(options.SourcePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,          // 64KB 缓冲区，性能和内存的平衡点
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destStream = new FileStream(
            PathHelper.ToExtendedLength(tmpPath),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            FileOptions.Asynchronous))
        {
            await sourceStream.CopyToAsync(destStream, options.CancellationToken);
            // sourceStream 已经读取完毕，它的 Length 就是源文件大小
            bytesCopied = sourceStream.Length;
        }

        // 第 2 步：Verify —— 比较文件大小
        logger?.Invoke("[Verify]校验: 比对文件大小");

        // 获取本地源文件大小
        long sourceSize = new FileInfo(PathHelper.ToExtendedLength(options.SourcePath)).Length;
        // 获取.tmp 文件大小
        long tmpSize = new FileInfo(PathHelper.ToExtendedLength(tmpPath)).Length;

        // 如果大小不一致，重试（最多 RetryLimit 次）
        if (sourceSize != tmpSize)
        {
            for (int retry = 1; retry <= RetryLimit; retry++)
            {
                logger?.Invoke($"[Verify]大小不匹配 (源={sourceSize}, tmp={tmpSize})，第 {retry} 次重试");

                // 重新复制：先删掉写坏了的 .tmp，再重新写一次
                File.Delete(PathHelper.ToExtendedLength(tmpPath));

                // 重新执行 Copy 步骤（复用同一段逻辑）
                await using (var sourceStream = new FileStream(
                    PathHelper.ToExtendedLength(options.SourcePath),
                    FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destStream = new FileStream(
                    PathHelper.ToExtendedLength(tmpPath),
                    FileMode.Create, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous))
                {
                    await sourceStream.CopyToAsync(destStream, options.CancellationToken);
                    bytesCopied = sourceStream.Length;
                }

                // 重试后再比一次
                tmpSize = new FileInfo(PathHelper.ToExtendedLength(tmpPath)).Length;
                if (sourceSize == tmpSize)
                    break; // 重试成功，跳出循环

                // 最后一次重试还是失败 → 抛异常
                if (retry >= RetryLimit)
                {
                    throw new InvalidDataException(
                        $"文件大小校验失败（已重试 {RetryLimit} 次）：" +
                        $"源文件 {sourceSize} 字节，临时文件 {tmpSize} 字节，" +
                        $"路径 {options.SourcePath}");
                }
            }
        }

        // 第 3 步：Mtime —— 同步文件修改时间
        logger?.Invoke("[Mtime]同步: 修改时间");

        // 读取本地文件的修改时间（UTC）
        DateTime sourceMtime = File.GetLastWriteTimeUtc(PathHelper.ToExtendedLength(options.SourcePath));

        // 设置 .tmp 文件的修改时间
        File.SetLastWriteTimeUtc(PathHelper.ToExtendedLength(tmpPath), sourceMtime);

        // 第 4 步：Rename —— .tmp → 正式文件名
        logger?.Invoke($"[Rename]重命名: {tmpPath} → {options.DestPath}");

        //overwrite=true 这样即使之前有一个传输失败留下的半截文件，也能覆盖掉
        File.Move(
            PathHelper.ToExtendedLength(tmpPath),
            PathHelper.ToExtendedLength(options.DestPath),
            overwrite: true);

        // 第 5 步：Clean —— 可选删除本地源文件
        // 受配置控制：delete_after_transfer = true 时才会执行。
        if (options.DeleteAfterTransfer)
        {
            logger?.Invoke($"清理: 删除本地文件 {options.SourcePath}");
            DeleteLocalFile(options.SourcePath, logger);
        }

        return bytesCopied;
    }


    /// <summary>
    /// 判断远端文件是否与本地一致（已同步），判断条件是 Size + LastWriteTime 都一致。
    ///   - 目标文件不存在时返回 false（需要复制）
    ///   - 任意一项不匹配也返回 false
    /// </summary>
    private static bool IsAlreadySynced(string sourcePath, string destPath)
    {
        // 先检查目标文件是否存在。不存在肯定没同步过。
        // 这里用 FileInfo 包装一下，方便同时获取 Exists、Length、LastWriteTimeUtc
        var destInfo = new FileInfo(PathHelper.ToExtendedLength(destPath));
        if (!destInfo.Exists)
            return false;

        // 取本地源文件信息
        var sourceInfo = new FileInfo(PathHelper.ToExtendedLength(sourcePath));

        // 文件字节大小必须一致
        if (sourceInfo.Length != destInfo.Length)
            return false;

        // 修改时间必须一致（允许 1000 毫秒误差，因为 FAT/NTFS 的时间精度不同）
        TimeSpan timeDiff = (sourceInfo.LastWriteTimeUtc - destInfo.LastWriteTimeUtc).Duration();
        if (timeDiff.TotalMilliseconds > 1000)
            return false;

        return true;
    }

    /// <summary>
    /// 删除本地源文件。如果文件已被其他进程占用或已被删除，不抛异常。
    /// </summary>
    private static void DeleteLocalFile(string path, Action<string>? logger)
    {
        try
        {
            string extendedPath = PathHelper.ToExtendedLength(path);
            if (File.Exists(extendedPath))
            {
                File.Delete(extendedPath);
                logger?.Invoke($"[Delete]已删除本地文件: {path}");
            }
        }
        catch (Exception ex)
        {
            // 删除失败不抛异常，只记日志
            // 文件可能被其他进程临时打开，下次传输如果已同步，还能再删一次
            logger?.Invoke($"[Delete]删除本地文件失败（不影响传输）: {ex.Message}");
        }
    }
}
