using NasMover.Core.Tools;
using System.Collections.Concurrent;

namespace NasMover.Core;

/// <summary>
/// 一次扫描发现的文件。ScanService 产出这些候选对象，
/// TransferEngine 将它们通过 Channel 分发给 Worker 处理。
///
/// 关键字段是 RelativePath，Worker 用它拼接最终 NAS 目标路径：
///   dest = NasBasePath / MachineId / ActiveModel / RelativePath
/// </summary>
public class ScanCandidate
{
    /// <summary>本地源文件的完整绝对路径</summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// 相对于 base_dir 的路径（保留目录结构）。
    /// 例如 base_dir=D:\data\MODEL，文件=D:\data\MODEL\OK\pic.jpg，
    /// 则 RelativePath = OK\pic.jpg
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>文件大小（字节），用于 AlreadySynced 判断</summary>
    public long FileSize { get; init; }

    /// <summary>最后修改时间（UTC），用于 AlreadySynced 判断</summary>
    public DateTime LastWriteTimeUtc { get; init; }
}

/// <summary>
/// 扫描结果汇总。包含发现的文件列表和统计信息。
/// </summary>
public class ScanResult
{
    /// <summary>通过全部过滤条件的待传输文件</summary>
    public IReadOnlyList<ScanCandidate> Files { get; init; } = Array.Empty<ScanCandidate>();

    /// <summary>本次扫描总耗时</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>扫描过程中遇到的错误（不致命，只是某些目录读不了）</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>本次总共扫描了多少文件（过滤前后）</summary>
    public int TotalScanned { get; init; }

    /// <summary>这次扫描的完整路径列表（调试用）</summary>
    public IReadOnlyList<string> ScannedPaths { get; init; } = Array.Empty<string>();
}


/// <summary>
/// 多线程目录扫描服务。负责：
///   1. 递归扫描 base_dirs 下所有文件
///   2. 按文件规则过滤（扩展名、前缀、后缀、黑名单目录、今日目录跳过）
///   3. 生成 ScanCandidate 列表，供 TransferEngine 消费
/// 线程策略：每个 base_dir 独立线程扫描，结果合并。
/// 过滤顺序：扩展名 → 排除后缀 → 前缀 → 黑名单目录 → 今日目录跳过
/// </summary>
public static class ScanService
{
    /// <summary>
    /// 对 ModelConfig 中配置的全部 base_dirs 进行多线程递归扫描。
    /// 每个 base_dir 在一个独立 Task 中并行执行。
    /// </summary>
    /// <param name="model">当前激活机种的配置（包含 base_dirs 和过滤规则）</param>
    /// <param name="cancellationToken">用于响应停止/暂停操作</param>
    /// <param name="logger">可选日志回调</param>
    public static async Task<ScanResult> ScanAsync(ModelConfig model,
        CancellationToken cancellationToken = default, Action<string>? logger = null)
    {
        var startTime = DateTime.UtcNow;

        // 如果 base_dirs 为空，直接返回空结果
        if (model.BaseDirs == null || model.BaseDirs.Count == 0)
        {
            return new ScanResult
            {
                Files = Array.Empty<ScanCandidate>(),
                Duration = TimeSpan.Zero,
                TotalScanned = 0
            };
        }

        // 预计算 ignore_today （所有 base_dir 共用同一份）
        string todayStr = ConvertIgnoreTodayFormat(model.IgnoreTodayFormat);

        // 线程安全的候选列表——多个 Task 同时向它添加结果
        var allCandidates = new ConcurrentBag<ScanCandidate>();
        var allErrors = new ConcurrentBag<string>();
        int totalScanned = 0;

        // 为每个 base_dir 创建一个扫描任务，并发执行
        // Task.WhenAll 等待所有任务完成
        var scanTasks = model.BaseDirs.Select(baseDir =>
            ScanSingleDirectoryAsync(
                baseDir,
                model,
                todayStr,
                allCandidates,
                allErrors,
                cancellationToken,
                logger));

        // 等待所有扫描任务完成
        await Task.WhenAll(scanTasks);

        // 统计总共扫描了多少文件
        totalScanned = allCandidates.Count;

        return new ScanResult
        {
            Files = allCandidates.ToList().AsReadOnly(),
            TotalScanned = totalScanned,
            Errors = allErrors.ToList().AsReadOnly(),
            Duration = DateTime.UtcNow - startTime
        };
    }


    /// <summary>
    /// 扫描一个 base_dir，递归遍历所有子目录，
    /// 对每个文件进行过滤规则检查，通过的加入 candidates。
    /// </summary>
    /// <param name="baseDir">这个扫描任务的根目录（来自 ModelConfig.BaseDirs）</param>
    /// <param name="model">机种配置（包含过滤规则）</param>
    /// <param name="todayStr">今天日期的格式化字符串（ignore_today 用）</param>
    /// <param name="candidates">线程安全的候选集合</param>
    /// <param name="errors">错误收集</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="logger">日志回调</param>
    private static async Task ScanSingleDirectoryAsync(
        string baseDir,
        ModelConfig model,
        string todayStr,
        ConcurrentBag<ScanCandidate> candidates,
        ConcurrentBag<string> errors,
        CancellationToken ct,
        Action<string>? logger)
    {
            // 用 Task.Run 把同步的目录枚举放到线程池上执行
            // 原因：Directory.EnumerateFiles 是同步 API，没有 async 版本
        await Task.Run(() =>
        {
            try
            {
                // 对 base_dir 加长路径前缀，支持超长路径
                string extendedBase = PathHelper.ToExtendedLength(baseDir);

                // 检查 base_dir 是否存在。如果不存在，收集错误并跳过
                if (!Directory.Exists(extendedBase))
                {
                    errors.Add($"扫描目录不存在: {baseDir}");
                    logger?.Invoke($"[Scan] 目录不存在，跳过: {baseDir}");
                    return;
                }

                logger?.Invoke($"[Scan] 开始扫描: {baseDir}");

                // 手动递归遍历所有子目录
                var directories = new Queue<string>();
                directories.Enqueue(extendedBase);

                while (directories.Count > 0)
                {
                    // 检查取消请求
                    ct.ThrowIfCancellationRequested();

                    string currentDir = directories.Dequeue();

                    // 检查：当前目录是否应跳过
                    if (ShouldSkipDirectory(currentDir, model.ExcludeDirs, todayStr, baseDir))
                        continue;

                    // 处理当前目录中的文件
                    try
                    {
                        // Directory.EnumerateFiles 是"懒加载"的——它不会一次性把所有文件名加载到内存
                        foreach (string filePath in Directory.EnumerateFiles(currentDir))
                        {
                            ct.ThrowIfCancellationRequested();

                            string fileName = Path.GetFileName(filePath);

                            // 用文件过滤规则检查此文件是否应该传输
                            if (ShouldIncludeFile(fileName, model.FileRules))
                            {
                                // 计算相对路径
                                string relativePath = GetRelativePath(filePath, baseDir);

                                var fileInfo = new FileInfo(PathHelper.ToExtendedLength(filePath));

                                candidates.Add(new ScanCandidate
                                {
                                    SourcePath = filePath,
                                    RelativePath = relativePath,
                                    FileSize = fileInfo.Length,
                                    LastWriteTimeUtc = fileInfo.LastWriteTimeUtc
                                });
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 没有权限读这个目录——跳过，不中断整体扫描
                        errors.Add($"无权限访问目录: {currentDir}");
                        logger?.Invoke($"[Scan] 无权限，跳过目录: {currentDir}");
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // 目录被外部删除了——跳过
                        continue;
                    }

                    // 递归：获取子目录，加入队列
                    try
                    {
                        foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                        {
                            directories.Enqueue(subDir);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 同上
                        errors.Add($"无权限访问子目录: {currentDir}");
                        continue;
                    }
                }

                logger?.Invoke($"[Scan] 扫描完成: {baseDir}");
            }
            catch (OperationCanceledException)
            {
                // 取消操作不记录为错误
                logger?.Invoke($"[Scan] 扫描已取消: {baseDir}");
            }
            catch (Exception ex)
            {
                // 其他异常（如 IO 错误）收集但不中断其他 base_dir 的扫描
                errors.Add($"扫描 {baseDir} 时出错: {ex.Message}");
                logger?.Invoke($"[Scan] 扫描出错: {baseDir} | {ex.Message}");
            }
        }, ct);
    }


    /// <summary>
    /// 判断一个文件是否应该纳入传输候选。
    /// 依次检查：扩展名白名单 → 排除后缀黑名单 → 前缀白名单。
    /// 全部通过才返回 true。
    /// </summary>
    private static bool ShouldIncludeFile(string fileName, FileRules rules)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        // 1. 扩展名检查
        // 例如 "pic.jpg" → "jpg"，"pic.JPG" → "jpg"
        string ext = Path.GetExtension(fileName)?.TrimStart('.').ToLowerInvariant() ?? "";

        if (rules.AllowedExtensions.Count > 0)
        {
            // 如果扩展名不在白名单中，跳过
            if (!rules.AllowedExtensions.Any(e =>
                string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // 2. 排除后缀检查
        // 例如 exclude_suffix = ["_NG"]，文件 "pic_NG.jpg" 将被排除
        if (rules.ExcludeSuffix.Count > 0)
        {
            // 去掉扩展名的文件名部分用于检查
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (rules.ExcludeSuffix.Any(suffix =>
                nameWithoutExt.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // 3. 前缀检查
        // 文件名必须以这些前缀之一开头
        if (rules.RequirePrefix.Count > 0)
        {
            if (!rules.RequirePrefix.Any(prefix =>
                fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // 全部规则通过
        return true;
    }


    /// <summary>
    /// 判断一个目录是否应该被跳过（不扫描其内容）。
    /// 跳过条件（满足任一即跳过）：
    ///   1. 目录名包含 exclude_dirs 中的关键字
    ///   2. ignore_today 启用时，路径中包含今天的日期字符串
    /// </summary>
    /// <param name="dirPath">当前目录的完整路径</param>
    /// <param name="excludeDirs">黑名单关键字列表</param>
    /// <param name="todayStr">今天日期的格式化字符串（空字符串=不启用）</param>
    /// <param name="baseDir">扫描根目录（用于路径显示）</param>
    private static bool ShouldSkipDirectory(
        string dirPath,
        List<string> excludeDirs,
        string todayStr,
        string baseDir)
    {
        // 1. 排除黑名单目录
        // 如果目录路径包含 exclude_dirs 中的任意关键字，跳过
        // 例如 exclude_dirs = ["TestRun", "Debug"]
        // D:\data\MODEL\TestRun\xxx → 跳过
        // D:\data\MODEL\Debug\yyy → 跳过
        if (excludeDirs.Count > 0)
        {
            // 获取目录的"相对路径"部分（去掉 baseDir），只检查相对部分
            // 避免 baseDir 本身路径巧合匹配
            string relativeDir = GetRelativePath(dirPath, baseDir);
            if (excludeDirs.Any(ex =>
                relativeDir.Contains(ex, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // 2. 忽略今日目录
        // 如果 todayStr 不为空，且 dirPath 包含今天日期的格式化字符串，
        // 说明这是"正在写入的今日目录"，跳过
        // 例如 todayStr = "2026_07_07"，路径 ...\2026_07_07\... → 跳过
        if (!string.IsNullOrEmpty(todayStr))
        {
            string relativeDir = GetRelativePath(dirPath, baseDir);
            if (relativeDir.Contains(todayStr, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

 
    /// <summary>
    /// 计算文件/目录相对于 baseDir 的路径。
    /// 例如：
    ///   filePath  = D:\data\MODEL_A_PRO\OK\pic.jpg
    ///   baseDir   = D:\data\MODEL_A_PRO
    ///   返回     = OK\pic.jpg
    /// </summary>
    private static string GetRelativePath(string fullPath, string baseDir)
    {
        string relative = Path.GetRelativePath(baseDir, PathHelper.StripExtendedPrefix(fullPath));
        return relative;
    }

    /// <summary>
    /// 日期格式转换
    /// 原始 Rust 项目使用 strftime（如 %Y_%m_%d），
    /// C# 使用不同的格式说明符（如 yyyy_MM_dd）。
    ///
    /// 如果格式为空字符串，返回空字符串（表示不启用 ignore_today）。
    /// </summary>
    public static string ConvertDateFormat(string strftimeFormat)
    {
        if (string.IsNullOrEmpty(strftimeFormat))
            return string.Empty;

        string result = strftimeFormat
            .Replace("%Y", "yyyy")
            .Replace("%m", "MM")
            .Replace("%d", "dd")
            .Replace("%H", "HH")
            .Replace("%M", "mm")
            .Replace("%S", "ss");

        return result;
    }

    /// <summary>
    /// 将 ignore_today_format 配置转换成今天日期的字符串。
    /// 如果配置为空，返回空字符串（不启用此规则）。
    /// </summary>
    private static string ConvertIgnoreTodayFormat(string? ignoreTodayFormat)
    {
        if (string.IsNullOrEmpty(ignoreTodayFormat))
            return string.Empty;

        // 转换格式
        string dotnetFormat = ConvertDateFormat(ignoreTodayFormat);
        if (string.IsNullOrEmpty(dotnetFormat))
            return string.Empty;

        // %Y_%m_%d → yyyy_MM_dd → 2026_07_07
        return DateTime.Now.ToString(dotnetFormat);
    }
}
