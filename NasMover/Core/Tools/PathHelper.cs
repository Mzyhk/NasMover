namespace NasMover.Core.Tools;

/// <summary>
/// Windows 长路径处理工具。自动为路径加上 \\?\ 扩展长度前缀，
/// 绕过 Windows 260 字符路径限制，支持本地盘符和 UNC 路径。
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// 为路径添加 \\?\ 扩展长度前缀（如果尚未添加）。
    /// 本地路径 D:\... → \\?\D:\...
    /// UNC 路径 \\server\share → \\?\UNC\server\share
    /// 相对路径或已带前缀的路径不做二次处理。
    /// </summary>
    public static string ToExtendedLength(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // 统一反斜杠
        string normalized = path.Replace('/', '\\');

        // 已经带前缀 → 不动
        if (normalized.StartsWith(@"\\?\"))
            return normalized;

        // 已经带 \\.\（设备路径，极少见，容错处理）
        if (normalized.StartsWith(@"\\.\"))
            return normalized;

        // UNC 路径（\\server\share）
        if (normalized.StartsWith(@"\\"))
        {
            // 去掉前导 \\，拼成 \\?\UNC\rest
            string withoutPrefix = normalized.TrimStart('\\');
            return @"\\?\UNC\" + withoutPrefix;
        }

        // 本地绝对路径
        if (Path.IsPathRooted(normalized))
            return @"\\?\" + normalized;

        // 相对路径 → 不处理，调用方先转绝对路径再用
        return normalized;
    }

    /// <summary>
    /// 去掉 \\?\ 前缀，还原成常规路径格式。
    /// \\?\UNC\server\share → \\server\share
    /// \\?\D:\... → D:\...
    /// 无前缀则原样返回。
    /// </summary>
    public static string StripExtendedPrefix(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        string normalized = path.Replace('/', '\\');

        // UNC 扩展路径：\\?\UNC\rest → \\rest
        if (normalized.StartsWith(@"\\?\UNC\"))
        {
            string rest = normalized.Substring(@"\\?\UNC\".Length);
            return @"\\" + rest;
        }

        // 本地扩展路径：\\?\D:\... → D:\...
        if (normalized.StartsWith(@"\\?\"))
        {
            return normalized.Substring(@"\\?\".Length);
        }

        return normalized;
    }

    /// <summary>
    /// 判断路径是否已带有 \\?\ 前缀
    /// </summary>
    public static bool HasExtendedPrefix(string path)
    {
        return !string.IsNullOrEmpty(path)
            && (path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\"));
    }
}
