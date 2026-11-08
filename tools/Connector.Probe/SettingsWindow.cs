using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using HAlign = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;

namespace Connector.Probe;

internal sealed class SettingsWindow : Window
{
    private readonly string _directory;
    private readonly TrayManager _tray;
    private readonly Action<bool> _onAutoInputChanged;
    private readonly Action<bool> _onAutoEnterChanged;
    private readonly Action<bool> _onTopmostChanged;
    private readonly Action _onReconnect;
    private readonly Action _onExit;

    private static readonly SolidColorBrush DarkBg = new(Color.FromArgb(246, 26, 26, 28));
    private static readonly SolidColorBrush BorderBrushColor = new(Color.FromArgb(120, 75, 75, 80));
    private static readonly SolidColorBrush GreenDot = new(Color.FromRgb(7, 193, 96));

    public SettingsWindow(
        string directory,
        TrayManager tray,
        bool autoInput,
        bool autoEnter,
        bool topmost,
        string accountName,
        Action<bool> onAutoInputChanged,
        Action<bool> onAutoEnterChanged,
        Action<bool> onTopmostChanged,
        Action onReconnect,
        Action onExit)
    {
        _directory = directory;
        _tray = tray;
        _onAutoInputChanged = onAutoInputChanged;
        _onAutoEnterChanged = onAutoEnterChanged;
        _onTopmostChanged = onTopmostChanged;
        _onReconnect = onReconnect;
        _onExit = onExit;

        Title = "设置 · 微信输入连接器";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 330;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = DarkBg,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1.2),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 4,
                Opacity = 0.5,
                Color = Colors.Black
            },
            Margin = new Thickness(8)
        };

        var mainPanel = new StackPanel { Margin = new Thickness(18, 16, 18, 18) };

        // 1. Header Row
        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse { Width = 8, Height = 8, Fill = GreenDot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var titleText = new TextBlock
        {
            Text = "连接器偏好设置",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI")
        };
        titleStack.Children.Add(dot);
        titleStack.Children.Add(titleText);
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            FontSize = 11,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false
        };
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        mainPanel.Children.Add(header);

        // 2. Account state pill
        var accountBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(80, 45, 45, 50)),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var accountText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(accountName) ? "微信未绑定" : $"当前微信: {accountName}",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        accountBorder.Child = accountText;
        mainPanel.Children.Add(accountBorder);

        // 3. Setting toggles
        var toggleAutoInput = new ModernToggle(autoInput);
        toggleAutoInput.IsCheckedChanged += (_, val) => _onAutoInputChanged(val);
        mainPanel.Children.Add(CreateSettingRow("自动打入当前输入框", "识别后自动输入至光标位置", toggleAutoInput));

        var toggleAutoEnter = new ModernToggle(autoEnter);
        toggleAutoEnter.IsCheckedChanged += (_, val) => _onAutoEnterChanged(val);
        mainPanel.Children.Add(CreateSettingRow("输入后自动回车 (Enter)", "输入后立即追加回车发送", toggleAutoEnter));

        var toggleTopmost = new ModernToggle(topmost);
        toggleTopmost.IsCheckedChanged += (_, val) => _onTopmostChanged(val);
        mainPanel.Children.Add(CreateSettingRow("悬浮窗置顶显示", "微型小图标始终处于最前", toggleTopmost));

        // 4. Hotkey tip
        var hotkeyTip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 10, 0, 14)
        };
        var hotkeyText = new TextBlock
        {
            Text = "💡 全局补打快捷键：Ctrl + Alt + V",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 175))
        };
        hotkeyTip.Child = hotkeyText;
        mainPanel.Children.Add(hotkeyTip);

        // 5. Action buttons
        var btnRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var reconnectBtn = new Button
        {
            Content = "重新扫码",
            Height = 32,
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromArgb(80, 50, 50, 55)),
            Foreground = Brushes.White,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        reconnectBtn.Click += (_, _) => { Close(); _onReconnect(); };
        Grid.SetColumn(reconnectBtn, 0);
        btnRow.Children.Add(reconnectBtn);

        var exitBtn = new Button
        {
            Content = "退出程序",
            Height = 32,
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromArgb(40, 220, 80, 80)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 220, 80, 80)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        exitBtn.Click += (_, _) => { Close(); _onExit(); };
        Grid.SetColumn(exitBtn, 2);
        btnRow.Children.Add(exitBtn);

        mainPanel.Children.Add(btnRow);

        outerBorder.Child = mainPanel;
        Content = outerBorder;

        // Drag window by clicking anywhere on the panel
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private static Grid CreateSettingRow(string title, string subtitle, UIElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        var t = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Normal
        };
        var s = new TextBlock
        {
            Text = subtitle,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 145)),
            Margin = new Thickness(0, 2, 0, 0)
        };
        textStack.Children.Add(t);
        textStack.Children.Add(s);

        Grid.SetColumn(textStack, 0);
        row.Children.Add(textStack);

        Grid.SetColumn(control, 1);
        row.Children.Add(control);

        return row;
    }
}
