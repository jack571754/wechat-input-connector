using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Connector.Probe;

internal sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly Icon _icon;
    public bool AutoInputEnabled { get; set; } = true;
    public bool AutoEnterEnabled { get; set; }
    public bool TopmostEnabled { get; set; } = true;
    public event Action? ToggleVisibilityRequested;
    public event Action? HistoryRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayManager()
    {
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(new ToolStripMenuItem("历史记录", null, (_, _) => HistoryRequested?.Invoke()));
        _contextMenu.Items.Add(new ToolStripMenuItem("设置", null, (_, _) => SettingsRequested?.Invoke()));
        _contextMenu.Items.Add(new ToolStripMenuItem("显示 / 隐藏悬浮窗", null, (_, _) => ToggleVisibilityRequested?.Invoke()));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("退出连接器", null, (_, _) => ExitRequested?.Invoke()));
        _icon = CreateIcon();
        _notifyIcon = new NotifyIcon { Text = "微信输入连接器", Icon = _icon, Visible = true, ContextMenuStrip = _contextMenu };
        _notifyIcon.DoubleClick += (_, _) => HistoryRequested?.Invoke();
    }

    public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        => _notifyIcon.ShowBalloonTip(3000, title, text, icon);

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var green = new SolidBrush(Color.FromArgb(7, 193, 96));
        graphics.FillEllipse(green, 2, 2, 28, 28);
        using var pen = new Pen(Color.White, 2);
        graphics.DrawLine(pen, 10, 13, 22, 13);
        graphics.DrawLine(pen, 10, 19, 18, 19);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false; _notifyIcon.Dispose(); _contextMenu.Dispose(); _icon.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
