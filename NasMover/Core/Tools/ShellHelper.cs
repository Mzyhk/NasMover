using System.Diagnostics;

namespace NasMover.Core.Tools;

/// <summary>
/// 系统 Shell 操作辅助方法（打开文件/目录等）
/// </summary>
public static class ShellHelper
{
    /// <summary>
    /// 在资源管理器中打开指定路径。
    /// 如果是文件则选中该文件，如果是目录则直接打开。
    /// </summary>
    public static void OpenInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShellHelper] 打开失败: {ex.Message}");
        }
    }
}
