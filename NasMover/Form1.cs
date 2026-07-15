using Microsoft.AspNetCore.Components.WebView.WindowsForms;

namespace NasMover;

/// <summary>
/// NAS-Mover 主窗体。
/// </summary>
public partial class Form1 : Form
{
    private readonly IServiceProvider _services;

    public Form1(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();

        // ---- 配置 BlazorWebView ----
        _blazorWebView.HostPage = "wwwroot/index.html";
        _blazorWebView.Services = services;
        _blazorWebView.RootComponents.Add<Main>("#app");
        _blazorWebView.Dock = DockStyle.Fill;
        _blazorWebView.MinimumSize = new Size(900, 700);

        // ---- 托盘图标 ----
        _trayIcon.Visible = true;

        // ---- 窗口事件 ----
        FormClosing += OnFormClosing;
    }



    private void MenuShow_Click(object? sender, EventArgs e) => ShowWindow();

    private void MenuExit_Click(object? sender, EventArgs e)
    {
        _trayIcon.Dispose();
        Application.Exit();
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e) => ShowWindow();


    /// <summary>关闭按钮 / Alt+F4 → 隐藏到托盘而非退出</summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideWindow();
        }
    }


    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void HideWindow()
    {
        Hide();
        _trayIcon.ShowBalloonTip(500, "NAS-Mover", "程序已最小化到系统托盘", ToolTipIcon.Info);
    }
}
