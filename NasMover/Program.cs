// NAS-Mover 单实例桌面程序入口
//
// 职责：
//   1. Mutex 单实例守卫（防止多开）
//   2. 注册 DI 服务（Blazor WebView + AppRuntime）
//   3. 启动主窗体 Form1

using Microsoft.Extensions.DependencyInjection;
using NasMover.Core;
using NasMover.Core.Tools;

namespace NasMover;

static class Program
{
    /// <summary>全局唯一 Mutex ID</summary>
    private const string MutexId = "Global\\NAS-Mover-5A1B2C3D-4E5F-6789-0ABC-DEF012345678";

    [STAThread]
    static void Main()
    {
        // ---- 单实例守卫 ----
        bool createdNew;
        var singleInstanceMutex = new Mutex(true, MutexId, out createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "NAS-Mover 已在运行中。\n请检查系统托盘区。",
                "NAS-Mover",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // ---- DI 容器 ----
        var services = new ServiceCollection();
        services.AddWindowsFormsBlazorWebView();
        services.AddSingleton<AppRuntime>();

        var serviceProvider = services.BuildServiceProvider();

        // ---- 文件日志（程序目录/logs/yyyy-MM-dd.log） ----
        var logDir = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? ".",
            "logs");
        var fileLogger = new FileLogger(logDir);
        var runtime = serviceProvider.GetRequiredService<AppRuntime>();
        runtime.LogMessage += fileLogger.Write;

        // ---- 启动应用 ----
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new Form1(serviceProvider));

        // 应用退出时清理
        fileLogger.Dispose();
    }
}
