using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Connector.Core;
using ProbeState = Connector.WeChat.ConnectionState;
using Connector.WeChat;
using Connector.Windows;
using QRCoder;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using HAlign = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace Connector.Probe;

internal sealed class PillWindow : Window
{
    private readonly string _directory;
    private readonly HttpClient _http = ILinkClient.CreateHttpClient();
    private readonly ProtocolMetadata _metadata = ILinkClient.LoadPinnedMetadata();
    private readonly TrayManager _tray;
    private readonly InputAttemptGate _attemptGate = new();

    private CancellationTokenSource _session = new();
    private TaskCompletionSource<string>? _pendingCode;
    private ProbeState? _state;
    private ReceiptSession? _live;
    private readonly SemaphoreSlim _connectTransition = new(1, 1);
    private Task _connectionTask = Task.CompletedTask;
    private bool _autoInput = true;
    private bool _autoEnter = false;
    private bool _isMiniOrb = false;
    private DesktopWindow? _desktopWindow;
    private Point? _dragStart;
    private bool _dragged;
    private readonly DispatcherTimer _collapseTimer;

    // UI elements
    private readonly Border _cardBorder;
    private readonly Grid _miniOrbContent;
    private readonly StackPanel _fullPillContent;
    private readonly Ellipse _miniOrbDot;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly Button _replayBtn;
    private readonly Button _minimizeBtn;
    private readonly Border _drawer;
    private readonly System.Windows.Controls.Image _qrImage;
    private readonly Grid _verifyRow;
    private readonly System.Windows.Controls.TextBox _verifyBox;
    private readonly Button _verifySubmitBtn;
    private readonly TextBlock _drawerHint;

    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(7, 193, 96));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(142, 142, 147));
    private static readonly SolidColorBrush DarkBg = new(Colors.White);
    private static readonly SolidColorBrush BorderColor = new(Color.FromRgb(227, 231, 233));

    public PillWindow(string directory, TrayManager tray)
    {
        _directory = directory;
        _tray = tray;

        // Auto collapse timer (3 seconds)
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_drawer?.Visibility != Visibility.Visible && !IsMouseOver)
            {
                CollapseToMiniOrb();
            }
        };

        // Window style
        Title = "微信输入连接器";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        // Position: Top center of primary screen
        var workArea = SystemParameters.WorkArea;
        Left = (workArea.Width - 320) / 2 + workArea.Left;
        Top = workArea.Top + 18;

        // Outer Card Border
        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = DarkBg,
            BorderBrush = BorderColor,
            BorderThickness = new Thickness(1.2),
            Margin = new Thickness(6)
        };

        var rootGrid = new Grid();

        // 1. Mini Orb Content (Visible when collapsed: 36x36 circular button)
        _miniOrbContent = new Grid
        {
            Width = 36,
            Height = 36,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            ToolTip = "微信输入连接器 · 单击展开，可拖动"
        };
        _miniOrbDot = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = GreenBrush,
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _miniOrbContent.Children.Add(_miniOrbDot);

        _miniOrbContent.MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this); _dragged = false;
        };
        _miniOrbContent.MouseLeftButtonUp += (_, _) =>
        {
            if (!_dragged) ExpandToPill();
            _collapseTimer.Start();
        };
        _miniOrbContent.MouseRightButtonUp += (_, _) => ShowMoreMenu(_miniOrbContent);
        rootGrid.Children.Add(_miniOrbContent);

        // 2. Full Pill Content (Visible when expanded)
        _fullPillContent = new StackPanel { Width = 306 };

        var pillBar = new Grid { Height = 44, Cursor = Cursors.Hand };
        pillBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        pillBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pillBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        pillBar.MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this); _dragged = false;
        };
        pillBar.MouseRightButtonUp += (_, _) => ShowMoreMenu(pillBar);

        // Status Indicator Dot
        _statusDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = GreenBrush,
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_statusDot, 0);
        pillBar.Children.Add(_statusDot);

        // Status / Message Text
        _statusText = new TextBlock
        {
            Text = "正在连接微信…",
            Foreground = new SolidColorBrush(Color.FromRgb(32, 36, 40)),
            FontSize = 12.5,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "连接与派发状态；Ctrl+Alt+V 补写下一条待输入消息",
            Margin = new Thickness(0, 0, 8, 0)
        };

        Grid.SetColumn(_statusText, 1);
        pillBar.Children.Add(_statusText);

        // Right Actions Stack
        var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };

        _replayBtn = CreateIconButton("↵", "补写到当前光标 (Ctrl+Alt+V)");
        _replayBtn.Click += async (_, _) => await TriggerManualReplayAsync();


        var settingsBtn = CreateIconButton("历史", "查看历史记录");
        settingsBtn.Width = 40;
        settingsBtn.Click += (_, _) => ShowDesktop(false);
        actionsPanel.Children.Add(settingsBtn);

        _minimizeBtn = CreateIconButton("⋯", "更多操作");
        _minimizeBtn.Click += (_, _) => ShowMoreMenu(_minimizeBtn);
        actionsPanel.Children.Add(_minimizeBtn);

        Grid.SetColumn(actionsPanel, 2);
        pillBar.Children.Add(actionsPanel);
        _fullPillContent.Children.Add(pillBar);

        // 3. Expandable QR Drawer (for login)
        _drawer = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 6, 0, 10),
            HorizontalAlignment = HAlign.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        var drawerStack = new StackPanel
        {
            HorizontalAlignment = HAlign.Center,
            Width = 264
        };

        var qrFrame = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            HorizontalAlignment = HAlign.Center
        };

        _qrImage = new System.Windows.Controls.Image
        {
            Width = 240,
            Height = 240,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(_qrImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_qrImage, EdgeMode.Aliased);
        qrFrame.Child = _qrImage;
        drawerStack.Children.Add(qrFrame);

        _drawerHint = new TextBlock
        {
            Text = "📱 请使用手机微信 [扫一扫] 扫描二维码",
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HAlign.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        drawerStack.Children.Add(_drawerHint);

        _verifyRow = new Grid
        {
            Width = 240,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _verifyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _verifyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _verifyBox = new System.Windows.Controls.TextBox
        {
            Height = 32,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HAlign.Center,
            Padding = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            IsEnabled = false
        };
        Grid.SetColumn(_verifyBox, 0);
        _verifyRow.Children.Add(_verifyBox);

        _verifySubmitBtn = new Button
        {
            Content = "确认",
            Width = 56,
            Height = 32,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(6, 0, 0, 0),
            Background = GreenBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            IsEnabled = false
        };
        void DoSubmitVerify()
        {
            if (_pendingCode?.TrySetResult(_verifyBox.Text.Trim()) == true)
            {
                _verifyBox.Clear();
                _verifyBox.IsEnabled = _verifySubmitBtn.IsEnabled = false;
                _verifyRow.Visibility = Visibility.Collapsed;
            }
        }
        _verifySubmitBtn.Click += (_, _) => DoSubmitVerify();
        _verifyBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoSubmitVerify(); };
        Grid.SetColumn(_verifySubmitBtn, 1);
        _verifyRow.Children.Add(_verifySubmitBtn);
        drawerStack.Children.Add(_verifyRow);

        _drawer.Child = drawerStack;


        rootGrid.Children.Add(_fullPillContent);
        _cardBorder.Child = rootGrid;
        Content = _cardBorder;

        // Mouse hover pause on full pill
        MouseMove += (_, e) =>
        {
            if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _dragged) return;
            var position = e.GetPosition(this);
            if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragged = true;
            try { DragMove(); ClampPosition(); SaveSettings(); } catch (InvalidOperationException) { }
        };
        MouseEnter += (_, _) => _collapseTimer.Stop();
        MouseLeave += (_, _) =>
        {
            if (!_isMiniOrb && _drawer.Visibility != Visibility.Visible)
                _collapseTimer.Start();
        };

        // Wire tray events
        _tray.ToggleVisibilityRequested += () => Dispatcher.Invoke(() =>
        {
            if (IsVisible)
            {
                if (_isMiniOrb) ExpandToPill();
                else Hide();
            }
            else
            {
                Show();
                ExpandToPill();
            }
        });

        _tray.HistoryRequested += () => Dispatcher.Invoke(() => ShowDesktop(false));
        _tray.ScreenshotRequested += () => Dispatcher.Invoke(() => _ = TriggerScreenshotAsync());
        _tray.SettingsRequested += () => Dispatcher.Invoke(() => ShowDesktop(true));
        _tray.ExitRequested += () => Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current?.Shutdown();
            Close();
        });

        // Load persisted settings
        LoadSettings();

        // Hotkey & Lifecycle
        Loaded += (_, _) =>
        {
            RegisterGlobalHotkey();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook((nint hwnd, int msg, nint w, nint l, ref bool handled) => { if (msg == 0x21) { handled = true; return 3; } return 0; });
            _ = ConnectAsync(forceLogin: false);
        };
        Closing += (_, _) =>
        {
            UnregisterGlobalHotkey();
            _session.Cancel();
            _pendingCode?.TrySetCanceled();
            _tray.Dispose();
            _desktopWindow?.Close();
            _collapseTimer.Stop();
            _http.Dispose();
            System.Windows.Application.Current?.Shutdown();
        };
    }

    private void ClampPosition()
    {
        // WPF 使用 DIP；虚拟桌面边界用于避免窗口在显示器配置变化后完全离屏。
        var left = SystemParameters.VirtualScreenLeft; var top = SystemParameters.VirtualScreenTop;
        Left = Math.Clamp(Left, left, Math.Max(left, left + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, top + SystemParameters.VirtualScreenHeight - 64));
    }

    private void ExpandToPill()
    {
        _isMiniOrb = false;
        _miniOrbContent.Visibility = Visibility.Collapsed;
        _fullPillContent.Visibility = Visibility.Visible;
        Width = 320;
        _cardBorder.CornerRadius = new CornerRadius(14); ClampPosition();
    }

    private void CollapseToMiniOrb()
    {
        _isMiniOrb = true;
        _fullPillContent.Visibility = Visibility.Collapsed;
        _miniOrbContent.Visibility = Visibility.Visible;
        Width = 48;
        _cardBorder.CornerRadius = new CornerRadius(19); ClampPosition();
    }

    public void OpenDesktop() => ShowDesktop(false);

    private void ShowDesktop(bool settings)
    {
        if (_desktopWindow is null)
        {
            if (_drawer.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(_drawer);
            _desktopWindow = new DesktopWindow(
                () => _state is null ? [] : InputHistory.Read(_live?.Coordinator.Snapshot ?? _state.ToRuntime(_autoInput, _autoEnter), new(_state.Account.BotId, _state.Account.OwnerId)),
                () => (_autoInput, _autoEnter, Topmost, _statusText.Text),
                ApplyAutoInput,
                ApplyAutoEnter,
                value => { Topmost = value; _tray.TopmostEnabled = value; SaveSettings(); },
                () => _ = ConnectAsync(forceLogin: true),
                _drawer,
                () => _ = TriggerScreenshotAsync());
            _desktopWindow.Closed += (_, _) => _desktopWindow = null;
        }
        _desktopWindow.SelectPage(settings);
        _desktopWindow.Show();
        if (_desktopWindow.WindowState == WindowState.Minimized) _desktopWindow.WindowState = WindowState.Normal;
        _desktopWindow.Activate();
    }

    private void ShowMoreMenu(FrameworkElement target)
    {
        _collapseTimer.Stop();
        var menu = new ContextMenu();
        void Add(string title, SymbolRegular symbol, Action action)
        {
            var item = new MenuItem
            {
                Header = title,
                Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 }
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("消息历史", SymbolRegular.History24, () => ShowDesktop(false));
        Add("截取屏幕发微信", SymbolRegular.Camera24, () => _ = TriggerScreenshotAsync());
        Add("连接与设置", SymbolRegular.Settings24, () => ShowDesktop(true));
        Add("隐藏悬浮窗", SymbolRegular.EyeOff24, Hide);
        menu.Items.Add(new Separator());
        Add("退出连接器", SymbolRegular.SignOut24, Close);
        menu.Closed += (_, _) => _collapseTimer.Start();
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private string VaultPath => System.IO.Path.Combine(_directory, "probe-state.dpapi");

    private static Button CreateIconButton(string text, string tooltip)
    {
        return new Button
        {
            Content = text,
            Width = 26,
            Height = 26,
            FontSize = 11.5,
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Focusable = false
        };
    }

    private void SetStatus(string text, SolidColorBrush dotColor)
    {
        Dispatcher.Invoke(() =>
        {
            _statusDot.Fill = dotColor;
            _miniOrbDot.Fill = dotColor;
            _statusText.Text = text;
        });
    }

    private sealed record PillSettings(bool AutoInput = true, bool AutoEnter = false, bool Topmost = true, double? Left = null, double? Top = null)
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }
    private PillSettings? _savedSettings;

    private void LoadSettings()
    {
        try
        {
            var path = System.IO.Path.Combine(_directory, "settings.json");
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<PillSettings>(File.ReadAllText(path));
                if (s is not null)
                {
                    _savedSettings = s;
                    _autoInput = s.AutoInput;
                    _autoEnter = s.AutoEnter;
                    Topmost = s.Topmost;
                    if (s.Left is { } left && s.Top is { } top) { Left = left; Top = top; ClampPosition(); }
                    _tray.AutoInputEnabled = _autoInput;
                    _tray.AutoEnterEnabled = _autoEnter;
                    _tray.TopmostEnabled = Topmost;
                    return;
                }
            }
        }
        catch { }
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            var path = System.IO.Path.Combine(_directory, "settings.json");
            var updated = new PillSettings(_autoInput, _autoEnter, Topmost, Left, Top) { Extra = _savedSettings?.Extra };
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(updated));
            File.Move(path + ".tmp", path, true); _savedSettings = updated;
        }
        catch (Exception) { SetStatus("设置未能保存，重启后可能恢复原值。", GreenBrush); }
    }

    private void UpdateRuntime(Action action)
    {
        try { action(); }
        catch (LegacyClipboardRecoveryException)
        {
            SetStatus("存在旧版剪贴板恢复记录，需先处理；已保持暂停。", GreenBrush);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _session.Cancel();
            SetStatus("本地加密状态无法保存，连接和输入已停止。", GrayBrush);
        }
    }

    private void ApplyAutoInput(bool enabled) => UpdateRuntime(() =>
    {
        _live?.Coordinator.SetAutoInput(enabled);
        _autoInput = enabled; _tray.AutoInputEnabled = enabled; SaveSettings();
        _live?.PublishDiagnostic();
        SetStatus(enabled ? (_live?.Coordinator.Snapshot.InputPaused == true ? "自动输入开关已开启，当前仍有输入阻塞。" : "已开启自动输入。") : "自动输入已暂停。", GreenBrush);
    });

    private void ApplyAutoEnter(bool enabled) => UpdateRuntime(() =>
    {
        _live?.Coordinator.SetAutoEnter(enabled);
        _autoEnter = enabled; _tray.AutoEnterEnabled = enabled; SaveSettings();
        _live?.PublishDiagnostic();
        SetStatus(enabled ? "已开启自动回车。" : "已关闭自动回车。", GreenBrush);
    });

    private async Task ConnectAsync(bool forceLogin)
    {
        await _connectTransition.WaitAsync();
        try
        {
            _session.Cancel(); _pendingCode?.TrySetCanceled();
            await _connectionTask;
            _session.Dispose(); _session = new(); _live = null;
            _connectionTask = RunConnectionAsync(forceLogin, _session.Token);
        }
        finally { _connectTransition.Release(); }
    }

    private async Task RunConnectionAsync(bool forceLogin, CancellationToken token)
    {
        try
        {
            if (File.Exists(VaultPath)) _state = ProtectedFile.Load<ProbeState>(VaultPath);
            if (forceLogin || _state is null)
            {
                ShowDesktop(true); SetStatus("请扫码连接微信…", GreenBrush);
                _drawer.Visibility = Visibility.Visible;
                var login = new QrLogin(new(_http, _metadata), RequestVerifyCodeAsync);
                var result = await login.RunAsync(new Progress<LoginProgress>(OnLoginProgress), token);
                _state = new(result, null, []);
                ProtectedFile.Save(VaultPath, _state);
            }
            _drawer.Visibility = Visibility.Collapsed;
            _live = new ReceiptSession(_directory, _state, _autoInput, _autoEnter, _http, _metadata, ReportSessionStatus);
            SetStatus("微信已连接；发送 /状态 可查询。", GreenBrush);
            _collapseTimer.Start();
            await _live.RunAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (SessionExpiredException)
        {
            SetStatus("微信会话失效，收发已停止，请重新扫码。", GrayBrush);
            _drawer.Visibility = Visibility.Visible; ShowDesktop(true);
        }
        catch (Exception)
        {
            SetStatus("连接或本地状态处理失败，收发和输入已停止。", GrayBrush);
        }
        finally { _live = null; }
    }

    private async Task TriggerScreenshotAsync()
    {
        if (_live is null)
        {
            SetStatus("未连接微信", GrayBrush);
            if (_isMiniOrb) ExpandToPill();
            _collapseTimer.Stop();
            _collapseTimer.Start();
            return;
        }
        SetStatus("📸 正在截屏并上传微信…", GreenBrush);
        if (_isMiniOrb) ExpandToPill();
        var success = await _live.SendScreenshotToWeChatAsync(CancellationToken.None);
        if (success)
        {
            SetStatus("📸 已截屏并回传微信", GreenBrush);
        }
        else
        {
            SetStatus("微信凭证已过期，请在手机发消息激活", GrayBrush);
        }
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void ReportSessionStatus(string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            SetStatus(text, GreenBrush);
            if (_isMiniOrb) ExpandToPill();
            _collapseTimer.Stop();
            _collapseTimer.Start();
        });
    }

    private void OnLoginProgress(LoginProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (progress.QrContent is not null)
            {
                var png = PngByteQRCodeHelper.GetQRCode(progress.QrContent, QRCodeGenerator.ECCLevel.M, 8);
                var imageSource = new BitmapImage();
                using var ms = new MemoryStream(png);
                imageSource.BeginInit();
                imageSource.StreamSource = ms;
                imageSource.CacheOption = BitmapCacheOption.OnLoad;
                imageSource.EndInit();
                imageSource.Freeze();
                _qrImage.Source = imageSource;
            }

            _drawerHint.Text = progress.Status switch
            {
                "qrcode" or "wait" => "📱 请用手机微信 [扫一扫] 扫描上方二维码",
                "scaned" => "⏳ 已扫码，请在手机微信上点击确认",
                "need_verifycode" => "🔢 请在下方输入手机微信显示的验证码",
                "scaned_but_redirect" => "🔄 正在切换登录节点…",
                "confirmed" => "🎉 登录成功！",
                "expired" => "⚠️ 二维码已过期，正在刷新…",
                _ => "正在连接微信…"
            };

            if (progress.Status == "need_verifycode")
            {
                _verifyRow.Visibility = Visibility.Visible;
            }
            else if (progress.Status is "confirmed" or "expired")
            {
                _verifyRow.Visibility = Visibility.Collapsed;
            }
        });
    }

    private Task<string> RequestVerifyCodeAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() =>
        {
            _pendingCode = completion;
            _verifyRow.Visibility = Visibility.Visible;
            _verifyBox.IsEnabled = _verifySubmitBtn.IsEnabled = true;
            _verifyBox.Focus();
        });
        return completion.Task.WaitAsync(ct);
    }

    private async Task TriggerManualReplayAsync()
    {
        try
        {
            await _attemptGate.TryRunAsync(async token =>
            {
                for (var i = 0; i < 50 && new[] { 0x10, 0x11, 0x12 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0); i++)
                    await Task.Delay(20, token);
                if (_live is null) { SetStatus("微信连接尚未就绪。", GreenBrush); return; }
                await _live.InsertNextAsync(token);
            }, _session.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _session.Cancel(); SetStatus("输入结果无法保存，收发和输入已停止。", GrayBrush); }
    }

    // --- Global Hotkey (Ctrl + Alt + V) ---
    private const int HotkeyId = 9001;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);

    private void RegisterGlobalHotkey()
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(HwndHook);
        RegisterHotKey(helper.Handle, HotkeyId, 0x4003, 0x56);
    }

    private void UnregisterGlobalHotkey()
    {
        var helper = new WindowInteropHelper(this);
        UnregisterHotKey(helper.Handle, HotkeyId);
    }

    private nint HwndHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int wmHotkey = 0x0312;
        if (msg == wmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _ = TriggerManualReplayAsync();
            handled = true;
        }
        return 0;
    }
}
