using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Connector.Probe;

internal sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _autoInputItem;
    private readonly ToolStripMenuItem _autoEnterItem;
    private readonly ToolStripMenuItem _topmostItem;

    public bool AutoInputEnabled
    {
        get => _autoInputItem.Checked;
        set => _autoInputItem.Checked = value;
    }

    public bool AutoEnterEnabled
    {
        get => _autoEnterItem.Checked;
        set => _autoEnterItem.Checked = value;
    }

    public bool TopmostEnabled
    {
        get => _topmostItem.Checked;
        set => _topmostItem.Checked = value;
    }

    public event Action? ToggleVisibilityRequested;
    public event Action<bool>? AutoInputChanged;
    public event Action<bool>? AutoEnterChanged;
    public event Action<bool>? TopmostChanged;
    public event Action? ReconnectRequested;
    public event Action? ExitRequested;

    public TrayManager()
    {
        _contextMenu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示 / 隐藏小药丸", null, (_, _) => ToggleVisibilityRequested?.Invoke());
        _contextMenu.Items.Add(showItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _autoInputItem = new ToolStripMenuItem("自动打入输入框") { Checked = true };
        _autoInputItem.Click += (_, _) =>
        {
            _autoInputItem.Checked = !_autoInputItem.Checked;
            AutoInputChanged?.Invoke(_autoInputItem.Checked);
        };
        _contextMenu.Items.Add(_autoInputItem);

        _autoEnterItem = new ToolStripMenuItem("输入后自动回车 (Enter)") { Checked = true };
        _autoEnterItem.Click += (_, _) =>
        {
            _autoEnterItem.Checked = !_autoEnterItem.Checked;
            AutoEnterChanged?.Invoke(_autoEnterItem.Checked);
        };
        _contextMenu.Items.Add(_autoEnterItem);

        _topmostItem = new ToolStripMenuItem("置顶显示") { Checked = true };
        _topmostItem.Click += (_, _) =>
        {
            _topmostItem.Checked = !_topmostItem.Checked;
            TopmostChanged?.Invoke(_topmostItem.Checked);
        };
        _contextMenu.Items.Add(_topmostItem);

        var reconnectItem = new ToolStripMenuItem("重新扫码绑定", null, (_, _) => ReconnectRequested?.Invoke());
        _contextMenu.Items.Add(reconnectItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "微信输入连接器",
            Icon = CreateWeChatTrayIcon(),
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _notifyIcon.DoubleClick += (_, _) => ToggleVisibilityRequested?.Invoke();
    }

    public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    private static Icon CreateWeChatTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Draw WeChat green circle badge
        using var brush = new SolidBrush(Color.FromArgb(7, 193, 96)); // WeChat Green
        g.FillEllipse(brush, 2, 2, 28, 28);

        // Draw a small white inner keyboard/speech dot
        using var dotBrush = new SolidBrush(Color.White);
        g.FillEllipse(dotBrush, 9, 10, 6, 6);
        g.FillEllipse(dotBrush, 17, 10, 6, 6);
        using var pen = new Pen(Color.White, 2);
        g.DrawArc(pen, 9, 12, 14, 10, 0, 180);

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
