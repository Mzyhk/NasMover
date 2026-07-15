using Microsoft.AspNetCore.Components.WebView.WindowsForms;

namespace NasMover;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    // ---- 控件 ----
    private BlazorWebView _blazorWebView;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private ToolStripMenuItem _menuShow;
    private ToolStripMenuItem _menuExit;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            _blazorWebView?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
        _blazorWebView = new BlazorWebView();
        _trayMenu = new ContextMenuStrip(components);
        _menuShow = new ToolStripMenuItem();
        _menuExit = new ToolStripMenuItem();
        _trayIcon = new NotifyIcon(components);
        _trayMenu.SuspendLayout();
        SuspendLayout();
        // 
        // _blazorWebView
        // 
        _blazorWebView.Location = new Point(0, 0);
        _blazorWebView.Margin = new Padding(2);
        _blazorWebView.Name = "_blazorWebView";
        _blazorWebView.Size = new Size(0, 0);
        _blazorWebView.TabIndex = 1;
        // 
        // _trayMenu
        // 
        _trayMenu.ImageScalingSize = new Size(20, 20);
        _trayMenu.Items.AddRange(new ToolStripItem[] { _menuShow, _menuExit });
        _trayMenu.Name = "_trayMenu";
        _trayMenu.Size = new Size(139, 52);
        // 
        // _menuShow
        // 
        _menuShow.Name = "_menuShow";
        _menuShow.Size = new Size(138, 24);
        _menuShow.Text = "显示窗口";
        _menuShow.Click += MenuShow_Click;
        // 
        // _menuExit
        // 
        _menuExit.Name = "_menuExit";
        _menuExit.Size = new Size(138, 24);
        _menuExit.Text = "退出";
        _menuExit.Click += MenuExit_Click;
        // 
        // _trayIcon
        // 
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Icon = (Icon)resources.GetObject("_trayIcon.Icon");
        _trayIcon.DoubleClick += TrayIcon_DoubleClick;
        // 
        // Form1
        // 
        AutoScaleDimensions = new SizeF(120F, 120F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(30, 30, 30);
        ClientSize = new Size(1184, 854);
        Controls.Add(_blazorWebView);
        Icon = (Icon)resources.GetObject("$this.Icon");
        Margin = new Padding(2);
        MinimumSize = new Size(1196, 900);
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "NAS-Mover";
        _trayMenu.ResumeLayout(false);
        ResumeLayout(false);
    }
}
