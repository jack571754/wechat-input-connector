using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Connector.Core;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Control = System.Windows.Controls.Control;
using HAlign = System.Windows.HorizontalAlignment;

namespace Connector.Probe;

internal sealed class DesktopWindow : FluentWindow
{
    private readonly Func<IReadOnlyList<HistoryEntry>> _read;
    private readonly Func<(bool Input, bool Enter, bool Topmost, string Status)> _settings;
    private readonly UIElement _connectionUI;
    private readonly ObservableCollection<HistoryRow> _rows = [];
    private readonly ListBox _list = new();
    private readonly TextBox _body = new();
    private readonly TextBlock _metadata = new();
    private readonly TextBlock _empty = new();
    private readonly TextBlock _historyCount = new();
    private readonly TextBlock _connectionStatusText = new();
    private readonly InfoBadge _connectionStatusBadge = new();
    private readonly StackPanel _connectedPlaceholder;

    private readonly ToggleSwitch _toggleInput = new();
    private readonly ToggleSwitch _toggleEnter = new();
    private readonly ToggleSwitch _toggleTopmost = new();

    private readonly Border _btnHistory;
    private readonly Border _btnSettings;
    private readonly TextBlock _txtHistory;
    private readonly TextBlock _txtSettings;
    private readonly SymbolIcon _iconHistory;
    private readonly SymbolIcon _iconSettings;

    private readonly UIElement _historyView;
    private readonly UIElement _settingsView;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private bool _updatingRows;
    private string? _selectedId;
    private string? _selectedText;

    private static readonly SolidColorBrush TextDark = new(Color.FromRgb(15, 23, 42));          // #0F172A
    private static readonly SolidColorBrush TextBody = new(Color.FromRgb(51, 65, 85));          // #334155
    private static readonly SolidColorBrush TextMuted = new(Color.FromRgb(100, 116, 139));      // #64748B
    private static readonly SolidColorBrush GreenWeChat = new(Color.FromRgb(7, 193, 96));       // #07C160
    private static readonly SolidColorBrush BorderColor = new(Color.FromRgb(226, 232, 240));    // #E2E8F0
    private static readonly SolidColorBrush DividerColor = new(Color.FromRgb(241, 245, 249));   // #F1F5F9

    public DesktopWindow(
        Func<IReadOnlyList<HistoryEntry>> read,
        Func<(bool Input, bool Enter, bool Topmost, string Status)> settings,
        Action<bool> input,
        Action<bool> enter,
        Action<bool> topmost,
        Action reconnect,
        UIElement connectionUI,
        Action? screenshot = null)
    {
        _read = read;
        _settings = settings;
        _connectionUI = connectionUI;

        // Window appearance
        Title = "微信输入连接器";
        Width = 980;
        Height = 670;
        MinWidth = 860;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = false;
        WindowBackdropType = WindowBackdropType.None;
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
        FontSize = 14;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // Custom modern app icon
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawEllipse(GreenWeChat, null, new System.Windows.Point(16, 16), 14, 14);
            drawing.DrawLine(new System.Windows.Media.Pen(Brushes.White, 2.2), new System.Windows.Point(10, 13), new System.Windows.Point(22, 13));
            drawing.DrawLine(new System.Windows.Media.Pen(Brushes.White, 2.2), new System.Windows.Point(10, 19), new System.Windows.Point(18, 19));
        }
        var icon = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        icon.Render(visual);
        icon.Freeze();
        Icon = icon;

        var rootGrid = new Grid { Margin = new Thickness(22, 18, 22, 16) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. Header Bar: Brand + Ready Badge + Fluent Segmented Capsule Switcher
        var header = new DockPanel { Margin = new Thickness(4, 0, 4, 18) };

        var brandStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var logoBorder = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = GreenWeChat,
            Margin = new Thickness(0, 0, 10, 0)
        };
        logoBorder.Child = new SymbolIcon
        {
            Symbol = SymbolRegular.Chat24,
            FontSize = 18,
            Foreground = Brushes.White,
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        brandStack.Children.Add(logoBorder);

        var titleText = new TextBlock
        {
            Text = "微信输入连接器",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextDark,
            VerticalAlignment = VerticalAlignment.Center
        };
        brandStack.Children.Add(titleText);

        var readyBadge = new InfoBadge
        {
            Severity = InfoBadgeSeverity.Success,
            Value = "后台运行中",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        brandStack.Children.Add(readyBadge);
        header.Children.Add(brandStack);

        // Segmented Switcher (Capsule Switcher) with Vector Icons
        var switcher = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(140, 226, 232, 240)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            HorizontalAlignment = HAlign.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var switcherStack = new StackPanel { Orientation = Orientation.Horizontal };

        _iconHistory = new SymbolIcon { Symbol = SymbolRegular.History24, FontSize = 16, Foreground = TextDark, Margin = new Thickness(0, 0, 6, 0) };
        _txtHistory = new TextBlock { Text = "消息历史", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextDark, VerticalAlignment = VerticalAlignment.Center };
        var histContent = new StackPanel { Orientation = Orientation.Horizontal };
        histContent.Children.Add(_iconHistory);
        histContent.Children.Add(_txtHistory);

        _btnHistory = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.08, Color = Colors.Black },
            Child = histContent
        };

        _iconSettings = new SymbolIcon { Symbol = SymbolRegular.Settings24, FontSize = 16, Foreground = TextMuted, Margin = new Thickness(0, 0, 6, 0) };
        _txtSettings = new TextBlock { Text = "连接与设置", FontSize = 13, FontWeight = FontWeights.Normal, Foreground = TextMuted, VerticalAlignment = VerticalAlignment.Center };
        var settContent = new StackPanel { Orientation = Orientation.Horizontal };
        settContent.Children.Add(_iconSettings);
        settContent.Children.Add(_txtSettings);

        _btnSettings = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 0, 0, 0),
            Child = settContent
        };

        _btnHistory.MouseLeftButtonUp += (_, _) => SelectPage(false);
        _btnSettings.MouseLeftButtonUp += (_, _) => SelectPage(true);

        switcherStack.Children.Add(_btnHistory);
        switcherStack.Children.Add(_btnSettings);
        switcher.Child = switcherStack;
        DockPanel.SetDock(switcher, Dock.Right);
        header.Children.Add(switcher);
        rootGrid.Children.Add(header);

        // 2. Content Area: History View and Settings View
        var viewHost = new Grid();
        Grid.SetRow(viewHost, 1);

        _connectedPlaceholder = BuildConnectedPlaceholder();
        _settingsView = BuildSettings(reconnect, screenshot, connectionUI);
        _historyView = BuildHistory();

        viewHost.Children.Add(_historyView);
        viewHost.Children.Add(_settingsView);
        rootGrid.Children.Add(viewHost);

        // 3. Footer Bar: DPAPI Security Notice with Shield Icon
        var footer = new DockPanel { Margin = new Thickness(4, 12, 4, 0) };
        Grid.SetRow(footer, 2);
        var shieldIcon = new SymbolIcon
        {
            Symbol = SymbolRegular.ShieldCheckmark20,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        footer.Children.Add(shieldIcon);
        footer.Children.Add(new TextBlock
        {
            Text = "本地会话凭据与历史记录已采用 Windows DPAPI 硬件级加密 · 关闭此窗口后连接器将在后台持续运行",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        rootGrid.Children.Add(footer);

        Content = rootGrid;

        // Toggle event handlers
        _toggleInput.Click += (_, _) => { if (!_refreshing) input(_toggleInput.IsChecked == true); };
        _toggleEnter.Click += (_, _) => { if (!_refreshing) enter(_toggleEnter.IsChecked == true); };
        _toggleTopmost.Click += (_, _) => { if (!_refreshing) topmost(_toggleTopmost.IsChecked == true); };

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _body.Clear(); _rows.Clear(); };
    }

    public void SelectPage(bool settings)
    {
        if (settings)
        {
            _btnSettings.Background = Brushes.White;
            _btnSettings.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.08, Color = Colors.Black };
            _txtSettings.Foreground = TextDark;
            _txtSettings.FontWeight = FontWeights.SemiBold;
            _iconSettings.Foreground = TextDark;

            _btnHistory.Background = Brushes.Transparent;
            _btnHistory.Effect = null;
            _txtHistory.Foreground = TextMuted;
            _txtHistory.FontWeight = FontWeights.Normal;
            _iconHistory.Foreground = TextMuted;

            _settingsView.Visibility = Visibility.Visible;
            _historyView.Visibility = Visibility.Collapsed;
        }
        else
        {
            _btnHistory.Background = Brushes.White;
            _btnHistory.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.08, Color = Colors.Black };
            _txtHistory.Foreground = TextDark;
            _txtHistory.FontWeight = FontWeights.SemiBold;
            _iconHistory.Foreground = TextDark;

            _btnSettings.Background = Brushes.Transparent;
            _btnSettings.Effect = null;
            _txtSettings.Foreground = TextMuted;
            _txtSettings.FontWeight = FontWeights.Normal;
            _iconSettings.Foreground = TextMuted;

            _historyView.Visibility = Visibility.Visible;
            _settingsView.Visibility = Visibility.Collapsed;
        }
    }

    private UIElement BuildHistory()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: Message List Card
        var listCard = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = BorderColor,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 14, 0),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.04, Color = Colors.Black }
        };

        var listGrid = new Grid();
        listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var listHeader = new DockPanel { Margin = new Thickness(4, 2, 4, 10) };
        var listHeaderTitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        listHeaderTitle.Children.Add(new SymbolIcon { Symbol = SymbolRegular.History24, FontSize = 16, Foreground = TextDark, Margin = new Thickness(0, 0, 6, 0) });
        listHeaderTitle.Children.Add(new TextBlock
        {
            Text = "消息记录",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextDark,
            VerticalAlignment = VerticalAlignment.Center
        });
        listHeader.Children.Add(listHeaderTitle);

        _historyCount.Text = "共 0 条";
        _historyCount.FontSize = 12;
        _historyCount.Foreground = TextMuted;
        _historyCount.Background = DividerColor;
        _historyCount.Padding = new Thickness(8, 2, 8, 2);
        _historyCount.VerticalAlignment = VerticalAlignment.Center;
        _historyCount.HorizontalAlignment = HAlign.Right;
        DockPanel.SetDock(_historyCount, Dock.Right);
        listHeader.Children.Add(_historyCount);
        listGrid.Children.Add(listHeader);

        _list.ItemsSource = _rows;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.SetValue(VirtualizingStackPanel.IsVirtualizingProperty, true);
        _list.SetValue(VirtualizingStackPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        // Custom modern container styling for list items
        var itemContainerStyle = new Style(typeof(ListBoxItem));
        itemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemContainerStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 6)));
        itemContainerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemContainerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        itemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HAlign.Stretch));

        var itemTemplate = new ControlTemplate(typeof(ListBoxItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "ItemBorder";
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(12, 10, 12, 10));
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.White);
        borderFactory.SetValue(Border.BorderBrushProperty, BorderColor);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        borderFactory.AppendChild(presenter);
        itemTemplate.VisualTree = borderFactory;

        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(240, 253, 244)), "ItemBorder"));
        selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(167, 243, 208)), "ItemBorder"));
        itemTemplate.Triggers.Add(selectedTrigger);

        var hoverTrigger = new MultiTrigger();
        hoverTrigger.Conditions.Add(new Condition(ListBoxItem.IsMouseOverProperty, true));
        hoverTrigger.Conditions.Add(new Condition(ListBoxItem.IsSelectedProperty, false));
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(248, 250, 252)), "ItemBorder"));
        itemTemplate.Triggers.Add(hoverTrigger);

        itemContainerStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
        _list.ItemContainerStyle = itemContainerStyle;

        _list.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel>
                    <DockPanel>
                        <Border DockPanel.Dock="Right" CornerRadius="4" Padding="6,2" Background="{Binding StatusBg}">
                            <TextBlock Text="{Binding Status}" FontSize="11" FontWeight="Medium" Foreground="{Binding StatusFg}"/>
                        </Border>
                        <TextBlock Text="{Binding Time}" FontSize="12" Foreground="#64748B" VerticalAlignment="Center"/>
                    </DockPanel>
                    <TextBlock Text="{Binding Preview}" Margin="0,6,0,0" FontSize="13" Foreground="#1E293B" TextTrimming="CharacterEllipsis" MaxHeight="38" TextWrapping="Wrap"/>
                </StackPanel>
            </DataTemplate>
            """);

        _list.SelectionChanged += (_, _) => { if (!_updatingRows) ShowSelection(); };
        Grid.SetRow(_list, 1);
        listGrid.Children.Add(_list);
        listCard.Child = listGrid;
        grid.Children.Add(listCard);

        // Right: Detail View Card
        var detailCard = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = BorderColor,
            Padding = new Thickness(24, 20, 24, 20),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.04, Color = Colors.Black }
        };

        var detailGrid = new Grid();
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Detail Top Bar with WPF-UI Button
        var detailHeading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var copyBtn = new Button
        {
            Content = "复制正文",
            Icon = new SymbolIcon(SymbolRegular.Copy24),
            Appearance = ControlAppearance.Secondary,
            Padding = new Thickness(14, 6, 14, 6),
            MinHeight = 34
        };
        DockPanel.SetDock(copyBtn, Dock.Right);
        detailHeading.Children.Add(copyBtn);

        _metadata.Text = "消息正文";
        _metadata.FontSize = 14;
        _metadata.FontWeight = FontWeights.Medium;
        _metadata.Foreground = TextDark;
        _metadata.TextWrapping = TextWrapping.Wrap;
        _metadata.VerticalAlignment = VerticalAlignment.Center;
        detailHeading.Children.Add(_metadata);
        detailGrid.Children.Add(detailHeading);

        var divider = new Border { Height = 1, Background = DividerColor, Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(divider, 1);
        detailGrid.Children.Add(divider);

        _body.IsReadOnly = true;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.BorderThickness = new Thickness(0);
        _body.Padding = new Thickness(0, 0, 8, 0);
        _body.FontSize = 15;
        _body.Foreground = TextDark;
        _body.Background = Brushes.Transparent;
        Grid.SetRow(_body, 2);
        detailGrid.Children.Add(_body);

        _empty.Text = "还没有收到消息\n在手机微信中发送文字或指令，此处将实时展示。";
        _empty.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        _empty.FontSize = 14;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.HorizontalAlignment = HAlign.Center;
        Grid.SetRow(_empty, 2);
        detailGrid.Children.Add(_empty);

        var note = new TextBlock
        {
            Text = "💡 提示：历史记录保存在本地，仅供查阅核对。已派发表示已完成输入操作。",
            Foreground = TextMuted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(note, 3);
        detailGrid.Children.Add(note);

        copyBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(_selectedText)) return;
            try
            {
                System.Windows.Clipboard.SetText(_selectedText);
                copyBtn.Icon = new SymbolIcon(SymbolRegular.Checkmark24);
                copyBtn.Content = "已复制";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                copyBtn.Content = "复制未完成";
            }
            await Task.Delay(1300);
            if (IsLoaded)
            {
                copyBtn.Icon = new SymbolIcon(SymbolRegular.Copy24);
                copyBtn.Content = "复制正文";
            }
        };

        detailCard.Child = detailGrid;
        Grid.SetColumn(detailCard, 1);
        grid.Children.Add(detailCard);

        return grid;
    }

    private UIElement BuildSettings(Action reconnect, Action? screenshot, UIElement connectionUI)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left Column: WeChat Connection Card
        var connCard = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = BorderColor,
            Padding = new Thickness(22, 20, 22, 20),
            Margin = new Thickness(0, 0, 14, 0),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.04, Color = Colors.Black }
        };

        var connStack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        // Card Header with SymbolIcon & InfoBadge
        var connHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var connTitleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        connTitleStack.Children.Add(new SymbolIcon { Symbol = SymbolRegular.QrCode24, FontSize = 18, Foreground = TextDark, Margin = new Thickness(0, 0, 8, 0) });
        connTitleStack.Children.Add(new TextBlock
        {
            Text = "微信连接",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextDark,
            VerticalAlignment = VerticalAlignment.Center
        });
        connHeader.Children.Add(connTitleStack);

        _connectionStatusBadge.Severity = InfoBadgeSeverity.Attention;
        _connectionStatusBadge.Value = "状态查询中";
        _connectionStatusBadge.VerticalAlignment = VerticalAlignment.Center;
        _connectionStatusBadge.HorizontalAlignment = HAlign.Right;

        DockPanel.SetDock(_connectionStatusBadge, Dock.Right);
        connHeader.Children.Add(_connectionStatusBadge);
        connStack.Children.Add(connHeader);

        _connectionStatusText.Text = "正在初始化连接状态…";
        _connectionStatusText.FontSize = 13;
        _connectionStatusText.Foreground = TextMuted;
        _connectionStatusText.TextWrapping = TextWrapping.Wrap;
        _connectionStatusText.Margin = new Thickness(0, 0, 0, 14);
        connStack.Children.Add(_connectionStatusText);

        connStack.Children.Add(new Border { Height = 1, Background = DividerColor, Margin = new Thickness(0, 0, 0, 14) });

        // Container holding either the QR drawer or the Connected placeholder
        var qrContainer = new Grid { HorizontalAlignment = HAlign.Center };
        qrContainer.Children.Add(connectionUI);
        qrContainer.Children.Add(_connectedPlaceholder);
        connStack.Children.Add(qrContainer);

        // Action Buttons Row using WPF-UI Buttons
        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var reconnectBtn = new Button
        {
            Content = "重新扫码绑定",
            Icon = new SymbolIcon(SymbolRegular.ArrowClockwise24),
            Appearance = ControlAppearance.Secondary,
            Padding = new Thickness(14, 8, 14, 8)
        };
        reconnectBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "重新扫码绑定将中断当前微信连接并生成新二维码。确认继续吗？", "重新绑定微信", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                reconnect();
            }
        };
        actionsRow.Children.Add(reconnectBtn);

        if (screenshot is not null)
        {
            var screenshotBtn = new Button
            {
                Content = "截屏发微信",
                Icon = new SymbolIcon(SymbolRegular.Camera24),
                Appearance = ControlAppearance.Primary,
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(10, 0, 0, 0)
            };
            screenshotBtn.Click += (_, _) => screenshot();
            actionsRow.Children.Add(screenshotBtn);
        }

        connStack.Children.Add(actionsRow);
        connCard.Child = connStack;
        grid.Children.Add(connCard);

        // Right Column: Preferences & Guide Cards using CardControl
        var rightStack = new StackPanel();

        var prefHeader = new TextBlock
        {
            Text = "输入与交互偏好",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextDark,
            Margin = new Thickness(0, 0, 0, 14)
        };
        rightStack.Children.Add(prefHeader);

        // Preference Card 1: Auto Input
        var cardInput = new CardControl
        {
            Header = BuildCardHeader("自动写入当前输入框", "收到微信新消息后直接键入当前外部焦点输入框，无需绑定或切换特定应用。"),
            Icon = new SymbolIcon(SymbolRegular.Keyboard24),
            Content = _toggleInput,
            Margin = new Thickness(0, 0, 0, 10)
        };
        rightStack.Children.Add(cardInput);

        // Preference Card 2: Auto Enter
        var cardEnter = new CardControl
        {
            Header = BuildCardHeader("输入完成后追加 Enter", "自动键入 Enter 模拟发送或换行，具体行为由目标接收软件决定。"),
            Icon = new SymbolIcon(SymbolRegular.ArrowEnterLeft24),
            Content = _toggleEnter,
            Margin = new Thickness(0, 0, 0, 10)
        };
        rightStack.Children.Add(cardEnter);

        // Preference Card 3: Topmost
        var cardTopmost = new CardControl
        {
            Header = BuildCardHeader("悬浮胶囊置顶显示", "桌面迷你小绿点与状态胶囊始终保持在所有窗口顶层，支持随时拖拽移动。"),
            Icon = new SymbolIcon(SymbolRegular.Pin24),
            Content = _toggleTopmost,
            Margin = new Thickness(0, 0, 0, 20)
        };
        rightStack.Children.Add(cardTopmost);

        // Section 2: Shortcuts & Commands Guide Card
        var guideCard = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = BorderColor,
            Padding = new Thickness(22, 20, 22, 20),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.04, Color = Colors.Black }
        };

        var guideStack = new StackPanel();
        var guideHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        guideHeader.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Key24, FontSize = 18, Foreground = TextDark, Margin = new Thickness(0, 0, 8, 0) });
        guideHeader.Children.Add(new TextBlock
        {
            Text = "快捷键与微信指令指南",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextDark,
            VerticalAlignment = VerticalAlignment.Center
        });
        guideStack.Children.Add(guideHeader);

        AddGuideItem(guideStack, "Ctrl + Alt + V", "补写下一条待输入消息（先把光标聚焦到外部输入框，再按此快捷键）", false);
        AddGuideItem(guideStack, "截图 / 屏幕截图", "在手机微信中直接发送该词，电脑将自动截取当前屏幕并通过图片回传", true);
        AddGuideItem(guideStack, "/状态 或 帮助", "在手机微信中直接发送该指令，查询系统运行状态、队列与统计", true);

        guideCard.Child = guideStack;
        rightStack.Children.Add(guideCard);

        var rightScroll = new ScrollViewer { Content = rightStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(rightScroll, 1);
        grid.Children.Add(rightScroll);

        return grid;
    }

    private static StackPanel BuildCardHeader(string title, string description)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = TextDark
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = TextMuted,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private static StackPanel BuildConnectedPlaceholder()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 24, 0, 24),
            Visibility = Visibility.Collapsed
        };

        var checkIcon = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromRgb(240, 253, 244)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208)),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HAlign.Center,
            Child = new SymbolIcon
            {
                Symbol = SymbolRegular.Checkmark24,
                FontSize = 28,
                Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74)),
                HorizontalAlignment = HAlign.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        panel.Children.Add(checkIcon);

        panel.Children.Add(new TextBlock
        {
            Text = "微信已连接成功",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextDark,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 14, 0, 6)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "手机微信发送文字将直接输入到电脑光标处\n发送「截图」可即时抓取当前屏幕",
            FontSize = 12.5,
            Foreground = TextMuted,
            HorizontalAlignment = HAlign.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap
        });

        return panel;
    }

    private static void AddGuideItem(StackPanel parent, string keyBadge, string description, bool isWechatCommand)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };

        var badge = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = isWechatCommand ? new SolidColorBrush(Color.FromRgb(240, 253, 244)) : DividerColor,
            BorderBrush = isWechatCommand ? new SolidColorBrush(Color.FromRgb(187, 247, 208)) : BorderColor,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        badge.Child = new TextBlock
        {
            Text = keyBadge,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = isWechatCommand ? new SolidColorBrush(Color.FromRgb(21, 128, 61)) : TextDark,
            FontFamily = new FontFamily("Consolas, Segoe UI, Microsoft YaHei UI")
        };
        dock.Children.Add(badge);

        dock.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12.5,
            Foreground = TextBody,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        parent.Children.Add(dock);
    }

    private void Refresh()
    {
        try
        {
            var settings = _settings();
            _refreshing = true;
            _toggleInput.IsChecked = settings.Input;
            _toggleEnter.IsChecked = settings.Enter;
            _toggleTopmost.IsChecked = settings.Topmost;
            _connectionStatusText.Text = settings.Status;

            var isDrawerVisible = _connectionUI.Visibility == Visibility.Visible;
            _connectedPlaceholder.Visibility = isDrawerVisible ? Visibility.Collapsed : Visibility.Visible;

            if (settings.Status.Contains("微信已连接") || !isDrawerVisible)
            {
                _connectionStatusBadge.Severity = InfoBadgeSeverity.Success;
                _connectionStatusBadge.Value = "已连接";
            }
            else
            {
                _connectionStatusBadge.Severity = InfoBadgeSeverity.Attention;
                _connectionStatusBadge.Value = "等待扫码";
            }

            _refreshing = false;

            var rows = _read().Select(x => new HistoryRow(x)).ToArray();
            _historyCount.Text = $"共 {rows.Length} 条";

            if (_rows.Select(x => x.Value).SequenceEqual(rows.Select(x => x.Value))) return;

            var selected = _selectedId;
            _updatingRows = true;
            for (var i = _rows.Count - 1; i >= 0; i--)
            {
                if (!rows.Any(x => x.Value.Id == _rows[i].Value.Id)) _rows.RemoveAt(i);
            }
            for (var i = 0; i < rows.Length; i++)
            {
                if (i < _rows.Count && _rows[i].Value.Id == rows[i].Value.Id)
                {
                    if (_rows[i] != rows[i]) _rows[i] = rows[i];
                }
                else
                {
                    _rows.Insert(i, rows[i]);
                }
            }
            _list.SelectedItem = _rows.FirstOrDefault(x => x.Value.Id == selected) ?? _rows.FirstOrDefault();
            _updatingRows = false;
            ShowSelection();
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException)
        {
            _empty.Text = "本地记录读取失败，请稍后刷新重试。";
            _empty.Visibility = Visibility.Visible;
        }
        finally
        {
            _refreshing = false;
            _updatingRows = false;
        }
    }

    private void ShowSelection()
    {
        var entry = (_list.SelectedItem as HistoryRow)?.Value;
        _empty.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        if (entry is null)
        {
            _selectedId = null;
            _selectedText = null;
            _body.Clear();
            _metadata.Text = "消息正文";
            return;
        }
        _metadata.Text = $"📅 {entry.ReceivedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}   状态：{entry.Status}" + (string.IsNullOrWhiteSpace(entry.Detail) ? "" : $" ({entry.Detail})");
        if (_selectedId != entry.Id || _selectedText != entry.Text)
        {
            _body.Text = entry.Text;
            _body.ScrollToHome();
        }
        _selectedId = entry.Id;
        _selectedText = entry.Text;
    }

    private sealed record HistoryRow(HistoryEntry Value)
    {
        public string Time => Value.ReceivedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "时间未记录";
        public string Status => Value.Status;
        public string Preview => string.IsNullOrWhiteSpace(Value.Text) ? "（空文本）" : Value.Text.Replace('\r', ' ').Replace('\n', ' ');
        public Brush StatusBg => Value.Status switch
        {
            "已派发" => new SolidColorBrush(Color.FromRgb(236, 253, 245)),
            "屏幕截图" => new SolidColorBrush(Color.FromRgb(239, 246, 255)),
            "待处理" => new SolidColorBrush(Color.FromRgb(254, 243, 199)),
            _ => new SolidColorBrush(Color.FromRgb(241, 245, 249))
        };
        public Brush StatusFg => Value.Status switch
        {
            "已派发" => new SolidColorBrush(Color.FromRgb(5, 150, 105)),
            "屏幕截图" => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            "待处理" => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
            _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
        };
    }
}
