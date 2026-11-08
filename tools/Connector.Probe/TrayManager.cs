using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Connector.Probe;

internal sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly Icon _icon;
    private readonly List<Bitmap> _disposableBitmaps = [];

    public bool AutoInputEnabled { get; set; } = true;
    public bool AutoEnterEnabled { get; set; }
    public bool TopmostEnabled { get; set; } = true;

    public event Action? ToggleVisibilityRequested;
    public event Action? ScreenshotRequested;
    public event Action? HistoryRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayManager()
    {
        _contextMenu = new ContextMenuStrip
        {
            Renderer = new ModernFluentMenuRenderer(),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            ShowImageMargin = true,
            ImageScalingSize = new Size(18, 18),
            Padding = new Padding(4, 6, 4, 6),
            DropShadowEnabled = true
        };

        // Header Title
        var header = new ToolStripMenuItem("微信输入连接器")
        {
            Image = TrackBitmap(CreateHeaderIcon()),
            Enabled = false,
            Tag = "header",
            Padding = new Padding(6, 6, 14, 6)
        };
        _contextMenu.Items.Add(header);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Actions
        _contextMenu.Items.Add(CreateItem("消息历史记录", CreateHistoryIcon(), () => HistoryRequested?.Invoke()));
        _contextMenu.Items.Add(CreateItem("截取屏幕发微信", CreateCameraIcon(), () => ScreenshotRequested?.Invoke()));
        _contextMenu.Items.Add(CreateItem("连接与系统设置", CreateSettingsIcon(), () => SettingsRequested?.Invoke()));
        _contextMenu.Items.Add(CreateItem("显示 / 隐藏悬浮窗", CreateEyeIcon(), () => ToggleVisibilityRequested?.Invoke()));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(CreateItem("退出连接器", CreateExitIcon(), () => ExitRequested?.Invoke()));

        _icon = CreateIcon();
        _notifyIcon = new NotifyIcon
        {
            Text = "微信输入连接器",
            Icon = _icon,
            Visible = true,
            ContextMenuStrip = _contextMenu
        };
        _notifyIcon.DoubleClick += (_, _) => HistoryRequested?.Invoke();
    }

    private ToolStripMenuItem CreateItem(string title, Bitmap icon, Action action)
    {
        return new ToolStripMenuItem(title, TrackBitmap(icon), (_, _) => action())
        {
            Padding = new Padding(6, 6, 14, 6)
        };
    }

    private Bitmap TrackBitmap(Bitmap bmp)
    {
        _disposableBitmaps.Add(bmp);
        return bmp;
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

    private static Bitmap CreateHeaderIcon()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(7, 193, 96));
        g.FillEllipse(brush, 1, 1, 16, 16);
        using var pen = new Pen(Color.White, 1.8f);
        g.DrawLine(pen, 5, 7, 13, 7);
        g.DrawLine(pen, 5, 11, 10, 11);
        return bmp;
    }

    private static Bitmap CreateHistoryIcon()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(71, 85, 105), 1.6f);
        g.DrawEllipse(pen, 2, 2, 14, 14);
        g.DrawLine(pen, 9, 5, 9, 9);
        g.DrawLine(pen, 9, 9, 12, 9);
        return bmp;
    }

    private static Bitmap CreateCameraIcon()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(71, 85, 105), 1.5f);
        g.DrawRectangle(pen, 2, 5, 14, 10);
        g.DrawLine(pen, 6, 5, 8, 3);
        g.DrawLine(pen, 8, 3, 10, 3);
        g.DrawLine(pen, 10, 3, 12, 5);
        g.DrawEllipse(pen, 6, 7, 6, 6);
        return bmp;
    }

    private static Bitmap CreateSettingsIcon()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(71, 85, 105), 1.5f);
        g.DrawLine(pen, 2, 4, 16, 4);
        g.DrawEllipse(pen, 4, 2, 4, 4);
        g.DrawLine(pen, 2, 9, 16, 9);
        g.DrawEllipse(pen, 10, 7, 4, 4);
        g.DrawLine(pen, 2, 14, 16, 14);
        g.DrawEllipse(pen, 6, 12, 4, 4);
        return bmp;
    }

    private static Bitmap CreateEyeIcon()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(71, 85, 105), 1.5f);
        using var brush = new SolidBrush(Color.FromArgb(71, 85, 105));
        g.DrawArc(pen, 1, 2, 16, 13, 20, 140);
        g.DrawArc(pen, 1, 2, 16, 13, 200, 140);
        g.FillEllipse(brush, 7, 7, 4, 4);
        return bmp;
    }

    private static Bitmap CreateExitIcon()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(239, 68, 68), 1.6f);
        g.DrawArc(pen, 3, 3, 12, 12, -60, 300);
        g.DrawLine(pen, 9, 2, 9, 8);
        return bmp;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _icon.Dispose();
        foreach (var bmp in _disposableBitmaps) bmp.Dispose();
        _disposableBitmaps.Clear();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);

    private sealed class ModernFluentMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color BorderColor = Color.FromArgb(226, 232, 240);
        private static readonly Color HoverColor = Color.FromArgb(241, 245, 249);
        private static readonly Color TextDark = Color.FromArgb(15, 23, 42);
        private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);

        public ModernFluentMenuRenderer() : base(new ModernColorTable())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                using var brush = new SolidBrush(HoverColor);
                using var path = CreateRoundedPath(rect, 4);
                g.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (!e.Item.Enabled)
            {
                if (e.Item.Tag as string == "header")
                {
                    using var headerFont = new Font(e.Item.Font.FontFamily, 9.5f, FontStyle.Bold);
                    TextRenderer.DrawText(e.Graphics, e.Text, headerFont, e.TextRectangle, TextDark, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    return;
                }
                TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font, e.TextRectangle, TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }
            TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font, e.TextRectangle, TextDark, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var pen = new Pen(BorderColor);
            g.DrawRectangle(pen, rect);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // Clean margin without vertical dividing line
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class ModernColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => Color.FromArgb(226, 232, 240);
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color MenuItemSelected => Color.FromArgb(241, 245, 249);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(241, 245, 249);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(241, 245, 249);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color SeparatorDark => Color.FromArgb(226, 232, 240);
        public override Color SeparatorLight => Color.White;
    }
}
