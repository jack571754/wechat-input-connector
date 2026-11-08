using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Connector.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using TabControl = System.Windows.Controls.TabControl;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace Connector.Probe;

internal sealed class DesktopWindow : Window
{
    private readonly Func<IReadOnlyList<HistoryEntry>> _read;
    private readonly Func<(bool Input, bool Enter, bool Topmost, string Status)> _settings;
    private readonly ObservableCollection<HistoryRow> _rows = [];
    private readonly ListBox _list = new();
    private readonly TextBox _body = new();
    private readonly TextBlock _metadata = new();
    private readonly TextBlock _empty = new();
    private readonly TextBlock _connection = new();
    private readonly TabControl _tabs = new();
    private readonly CheckBox _input = new() { Content = "自动输入" };
    private readonly CheckBox _enter = new() { Content = "输入后回车" };
    private readonly CheckBox _topmost = new() { Content = "悬浮窗置顶" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private bool _updatingRows;
    private string? _selectedId;
    private string? _selectedText;

    public DesktopWindow(Func<IReadOnlyList<HistoryEntry>> read,
        Func<(bool Input, bool Enter, bool Topmost, string Status)> settings,
        Action<bool> input, Action<bool> enter, Action<bool> topmost, Action reconnect, UIElement connectionUI)
    {
        _read = read; _settings = settings;
        Title = "微信输入连接器"; Width = 860; Height = 560; MinWidth = 680; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(245, 246, 247));
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 14;
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _tabs.Background = Brushes.White; _tabs.BorderBrush = new SolidColorBrush(Color.FromRgb(227, 231, 233));
        _tabs.Items.Add(new TabItem { Header = "历史记录", Content = BuildHistory() });
        _tabs.Items.Add(new TabItem { Header = "设置", Content = BuildSettings(reconnect, connectionUI) });
        root.Children.Add(_tabs);
        var footer = new TextBlock { Text = "记录保存在本机 · 关闭此窗口后连接器继续运行", Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(4, 10, 0, 0) };
        Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        _input.Click += (_, _) => { if (!_refreshing) input(_input.IsChecked == true); };
        _enter.Click += (_, _) => { if (!_refreshing) enter(_enter.IsChecked == true); };
        _topmost.Click += (_, _) => { if (!_refreshing) topmost(_topmost.IsChecked == true); };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _body.Clear(); _rows.Clear(); };
    }

    public void SelectPage(bool settings) => _tabs.SelectedIndex = settings ? 1 : 0;

    private UIElement BuildHistory()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _list.ItemsSource = _rows; _list.BorderThickness = new Thickness(0, 0, 1, 0);
        _list.BorderBrush = new SolidColorBrush(Color.FromRgb(227, 231, 233));
        _list.SetValue(VirtualizingStackPanel.IsVirtualizingProperty, true);
        _list.SetValue(VirtualizingStackPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _list.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Margin="10,9" Width="230">
                    <DockPanel><TextBlock Text="{Binding Status}" DockPanel.Dock="Right" HorizontalAlignment="Right" FontSize="12" Foreground="#667078"/><TextBlock Text="{Binding Time}" FontSize="12" Foreground="#667078"/></DockPanel>
                    <TextBlock Text="{Binding Preview}" Margin="0,7,0,0" TextTrimming="CharacterEllipsis"/>
                </StackPanel>
            </DataTemplate>
            """);
        _list.SelectionChanged += (_, _) => { if (!_updatingRows) ShowSelection(); }; grid.Children.Add(_list);
        var panel = new Grid { Margin = new Thickness(24, 18, 24, 16) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var copy = new Button { Content = "复制正文", Padding = new Thickness(12, 6, 12, 6), MinHeight = 34, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(copy, Dock.Right); heading.Children.Add(copy);
        _metadata.Text = "消息正文"; _metadata.TextWrapping = TextWrapping.Wrap; _metadata.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_metadata); panel.Children.Add(heading);
        _body.IsReadOnly = true; _body.TextWrapping = TextWrapping.Wrap; _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.BorderThickness = new Thickness(0); _body.Padding = new Thickness(0, 0, 6, 0); _body.FontSize = 15; _body.Background = Brushes.White;
        Grid.SetRow(_body, 1); panel.Children.Add(_body);
        _empty.Text = "还没有收到消息"; _empty.Foreground = Brushes.DimGray; _empty.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(_empty, 1); panel.Children.Add(_empty);
        var note = new TextBlock { Text = "仅查看，不会补写或重发。已派发表示已调用输入操作。", Foreground = Brushes.DimGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(note, 2); panel.Children.Add(note); Grid.SetColumn(panel, 1); grid.Children.Add(panel);
        copy.Click += async (_, _) =>
        {
            if (_selectedText is null) return;
            try { System.Windows.Clipboard.SetText(_selectedText); copy.Content = "已复制"; }
            catch (System.Runtime.InteropServices.COMException) { copy.Content = "复制未完成"; }
            await Task.Delay(1200); if (IsLoaded) copy.Content = "复制正文";
        };
        return grid;
    }

    private UIElement BuildSettings(Action reconnect, UIElement connectionUI)
    {
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock { Text = "微信连接", FontSize = 18, FontWeight = FontWeights.SemiBold });
        _connection.Margin = new Thickness(0, 12, 0, 8); _connection.TextWrapping = TextWrapping.Wrap; panel.Children.Add(_connection);
        panel.Children.Add(connectionUI);
        var more = new Button { Content = "连接操作", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            var bind = new MenuItem { Header = "重新扫码绑定" };
            bind.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "这会结束当前连接并重新扫码绑定微信。继续吗？", "重新绑定", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    reconnect();
            };
            menu.Items.Add(bind); menu.PlacementTarget = more; menu.IsOpen = true;
        };
        panel.Children.Add(more);
        panel.Children.Add(new TextBlock { Text = "输入与显示", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 12) });
        AddOption(panel, _input, "新消息写入当前外部输入框，无需绑定应用。", 8);
        AddOption(panel, _enter, "输入后追加 Enter，具体作用由目标应用决定。", 8);
        AddOption(panel, _topmost, "只影响悬浮点和状态条。", 8);
        panel.Children.Add(new TextBlock { Text = "补写下一条待输入消息：Ctrl + Alt + V\n请先把光标放回外部输入框，再按快捷键。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 18, 0, 0), FontSize = 12 });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void AddOption(StackPanel parent, CheckBox control, string description, int spacing)
    {
        control.MinHeight = 32; control.Margin = new Thickness(0, spacing, 0, 0); parent.Children.Add(control);
        parent.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 6) });
    }

    private void Refresh()
    {
        try
        {
            var settings = _settings(); _refreshing = true;
            _input.IsChecked = settings.Input; _enter.IsChecked = settings.Enter; _topmost.IsChecked = settings.Topmost;
            _connection.Text = settings.Status; _refreshing = false;
            var rows = _read().Select(x => new HistoryRow(x)).ToArray();
            if (_rows.Select(x => x.Value).SequenceEqual(rows.Select(x => x.Value))) return;
            var selected = _selectedId;
            _updatingRows = true;
            for (var i = _rows.Count - 1; i >= 0; i--) if (!rows.Any(x => x.Value.Id == _rows[i].Value.Id)) _rows.RemoveAt(i);
            for (var i = 0; i < rows.Length; i++)
            {
                if (i < _rows.Count && _rows[i].Value.Id == rows[i].Value.Id)
                { if (_rows[i] != rows[i]) _rows[i] = rows[i]; }
                else _rows.Insert(i, rows[i]);
            }
            _list.SelectedItem = _rows.FirstOrDefault(x => x.Value.Id == selected) ?? _rows.FirstOrDefault();
            _updatingRows = false;
            ShowSelection();
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException)
        { _empty.Text = "本地记录读取失败，请关闭后重新打开。"; _empty.Visibility = Visibility.Visible; }
        finally { _refreshing = false; _updatingRows = false; }
    }

    private void ShowSelection()
    {
        var entry = (_list.SelectedItem as HistoryRow)?.Value;
        _empty.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        if (entry is null) { _selectedId = null; _selectedText = null; _body.Clear(); _metadata.Text = "消息正文"; return; }
        _metadata.Text = $"{entry.ReceivedAt?.ToLocalTime():MM-dd HH:mm}  {entry.Status}" + (entry.Detail.Length == 0 ? "" : $"\n{entry.Detail}");
        if (_selectedId != entry.Id || _selectedText != entry.Text) { _body.Text = entry.Text; _body.ScrollToHome(); }
        _selectedId = entry.Id; _selectedText = entry.Text;
    }

    private sealed record HistoryRow(HistoryEntry Value)
    {
        public string Time => Value.ReceivedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "时间未记录";
        public string Status => Value.Status;
        public string Preview => Value.Text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
