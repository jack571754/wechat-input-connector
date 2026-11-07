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
using Connector.WeChat;
using Connector.Windows;
using QRCoder;
using Button = System.Windows.Controls.Button;
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
    private bool _autoInput = true;
    private bool _autoEnter = true;
    private bool _inputPaused = false;
    private string? _latestText;
    private bool _isMiniOrb = false;
    private SettingsWindow? _settingsWindow;
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
    private readonly System.Windows.Controls.TextBox _verifyBox;
    private readonly Button _verifySubmitBtn;
    private readonly TextBlock _drawerHint;

    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(7, 193, 96));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(255, 184, 0));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(142, 142, 147));
    private static readonly SolidColorBrush DarkBg = new(Color.FromArgb(242, 26, 26, 28));
    private static readonly SolidColorBrush BorderColor = new(Color.FromArgb(120, 75, 75, 80));

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
        Width = 390;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        // Position: Top center of primary screen
        var workArea = SystemParameters.WorkArea;
        Left = (workArea.Width - 390) / 2 + workArea.Left;
        Top = workArea.Top + 18;

        // Outer Card Border
        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = DarkBg,
            BorderBrush = BorderColor,
            BorderThickness = new Thickness(1.2),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.45,
                Color = Colors.Black
            },
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
            ToolTip = "微信输入连接器 · 单击展开 / 右键设置 (可拖拽)"
        };
        _miniOrbDot = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = GreenBrush,
            Effect = new DropShadowEffect { Color = Color.FromRgb(7, 193, 96), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.8 },
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _miniOrbContent.Children.Add(_miniOrbDot);

        _miniOrbContent.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        _miniOrbContent.MouseLeftButtonUp += (_, _) =>
        {
            ExpandToPill();
            _collapseTimer.Start();
        };
        _miniOrbContent.MouseRightButtonUp += (_, _) => ShowSettingsDialog();
        rootGrid.Children.Add(_miniOrbContent);

        // 2. Full Pill Content (Visible when expanded)
        _fullPillContent = new StackPanel { Width = 376 };

        var pillBar = new Grid { Height = 44, Cursor = Cursors.Hand };
        pillBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        pillBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pillBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        pillBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        pillBar.MouseRightButtonUp += (_, _) => ShowSettingsDialog();

        // Status Indicator Dot
        _statusDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = YellowBrush,
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_statusDot, 0);
        pillBar.Children.Add(_statusDot);

        // Status / Message Text
        _statusText = new TextBlock
        {
            Text = "正在连接微信…",
            Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 245)),
            FontSize = 12.5,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "点击可补写最新文字到当前光标处",
            Margin = new Thickness(0, 0, 8, 0)
        };
        _statusText.MouseLeftButtonUp += async (_, _) => await TriggerManualReplayAsync();
        Grid.SetColumn(_statusText, 1);
        pillBar.Children.Add(_statusText);

        // Right Actions Stack
        var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };

        _replayBtn = CreateIconButton("↵", "补写到当前光标 (Ctrl+Alt+V)");
        _replayBtn.Click += async (_, _) => await TriggerManualReplayAsync();
        actionsPanel.Children.Add(_replayBtn);

        var settingsBtn = CreateIconButton("⚙", "偏好设置");
        settingsBtn.Click += (_, _) => ShowSettingsDialog();
        actionsPanel.Children.Add(settingsBtn);

        _minimizeBtn = CreateIconButton("—", "缩小为微型悬浮珠");
        _minimizeBtn.Click += (_, _) => CollapseToMiniOrb();
        actionsPanel.Children.Add(_minimizeBtn);

        Grid.SetColumn(actionsPanel, 2);
        pillBar.Children.Add(actionsPanel);
        _fullPillContent.Children.Add(pillBar);

        // 3. Expandable QR Drawer (for login)
        _drawer = new Border
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(16, 8, 16, 16),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255))
        };

        var drawerStack = new StackPanel { HorizontalAlignment = HAlign.Center };

        _qrImage = new System.Windows.Controls.Image
        {
            Width = 190,
            Height = 190,
            Margin = new Thickness(0, 8, 0, 8)
        };
        RenderOptions.SetBitmapScalingMode(_qrImage, BitmapScalingMode.HighQuality);
        drawerStack.Children.Add(_qrImage);

        _drawerHint = new TextBlock
        {
            Text = "请使用手机微信扫描二维码绑定",
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            FontSize = 11.5,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        drawerStack.Children.Add(_drawerHint);

        var verifyRow = new Grid { Width = 230, Margin = new Thickness(0, 4, 0, 4) };
        verifyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        verifyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _verifyBox = new System.Windows.Controls.TextBox
        {
            Height = 28,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 44)),
            Foreground = Brushes.White,
            BorderBrush = BorderColor,
            BorderThickness = new Thickness(1),
            IsEnabled = false
        };
        Grid.SetColumn(_verifyBox, 0);
        verifyRow.Children.Add(_verifyBox);

        _verifySubmitBtn = new Button
        {
            Content = "确认",
            Width = 52,
            Height = 28,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Background = GreenBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            IsEnabled = false
        };
        _verifySubmitBtn.Click += (_, _) =>
        {
            if (_pendingCode?.TrySetResult(_verifyBox.Text.Trim()) == true)
            {
                _verifyBox.Clear();
                _verifyBox.IsEnabled = _verifySubmitBtn.IsEnabled = false;
            }
        };
        Grid.SetColumn(_verifySubmitBtn, 1);
        verifyRow.Children.Add(_verifySubmitBtn);
        drawerStack.Children.Add(verifyRow);

        _drawer.Child = drawerStack;
        _fullPillContent.Children.Add(_drawer);

        rootGrid.Children.Add(_fullPillContent);
        _cardBorder.Child = rootGrid;
        Content = _cardBorder;

        // Mouse hover pause on full pill
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
                Activate();
                ExpandToPill();
            }
        });

        _tray.AutoInputChanged += enabled => Dispatcher.Invoke(() =>
        {
            _autoInput = enabled;
            SaveSettings();
            SetStatus(enabled ? "已开启自动打字" : "已暂停自动打字", _state is not null ? GreenBrush : YellowBrush);
        });

        _tray.AutoEnterChanged += enabled => Dispatcher.Invoke(() =>
        {
            _autoEnter = enabled;
            SaveSettings();
            SetStatus(enabled ? "已开启自动回车" : "已关闭自动回车", GreenBrush);
        });

        _tray.TopmostChanged += top => Dispatcher.Invoke(() =>
        {
            Topmost = top;
            SaveSettings();
        });

        _tray.ReconnectRequested += () => Dispatcher.Invoke(async () => await ConnectAsync(forceLogin: true));
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
            _ = ConnectAsync(forceLogin: false);
        };
        Closing += (_, _) =>
        {
            UnregisterGlobalHotkey();
            _session.Cancel();
            _pendingCode?.TrySetCanceled();
            _tray.Dispose();
            _settingsWindow?.Close();
            System.Windows.Application.Current?.Shutdown();
        };
    }

    private void ExpandToPill()
    {
        _isMiniOrb = false;
        _miniOrbContent.Visibility = Visibility.Collapsed;
        _fullPillContent.Visibility = Visibility.Visible;
        Width = 390;
        _cardBorder.CornerRadius = new CornerRadius(22);
    }

    private void CollapseToMiniOrb()
    {
        _isMiniOrb = true;
        _fullPillContent.Visibility = Visibility.Collapsed;
        _miniOrbContent.Visibility = Visibility.Visible;
        Width = 48;
        _cardBorder.CornerRadius = new CornerRadius(19);
    }

    private void ShowSettingsDialog()
    {
        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _directory,
            _tray,
            _autoInput,
            _autoEnter,
            Topmost,
            _state?.Account?.OwnerId ?? "",
            onAutoInputChanged: val =>
            {
                _autoInput = val;
                _tray.AutoInputEnabled = val;
                SaveSettings();
                SetStatus(val ? "已开启自动打字" : "已暂停自动打字", GreenBrush);
            },
            onAutoEnterChanged: val =>
            {
                _autoEnter = val;
                _tray.AutoEnterEnabled = val;
                SaveSettings();
                SetStatus(val ? "已开启自动回车" : "已关闭自动回车", GreenBrush);
            },
            onTopmostChanged: val =>
            {
                Topmost = val;
                _tray.TopmostEnabled = val;
                SaveSettings();
            },
            onReconnect: () => _ = ConnectAsync(forceLogin: true),
            onExit: () => Close()
        );

        // Place near the pill / orb
        _settingsWindow.Left = Left + (Width - 330) / 2;
        _settingsWindow.Top = Top + 50;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
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

    private sealed record PillSettings(bool AutoInput = true, bool AutoEnter = true, bool Topmost = true);

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
                    _autoInput = s.AutoInput;
                    _autoEnter = s.AutoEnter;
                    Topmost = s.Topmost;
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
            File.WriteAllText(path, JsonSerializer.Serialize(new PillSettings(_autoInput, _autoEnter, Topmost)));
        }
        catch { }
    }

    private async Task ConnectAsync(bool forceLogin)
    {
        _session.Cancel();
        _session.Dispose();
        _session = new CancellationTokenSource();
        var token = _session.Token;

        try
        {
            if (!forceLogin && File.Exists(VaultPath)) _state = ProtectedFile.Load<ProbeState>(VaultPath);
            if (_state is not null)
            {
                _state = _state with { Events = _state.Events.Select(x => x.State == EventState.Inserting ? x with { State = EventState.Uncertain } : x).ToList() };
                _inputPaused = _state.Events.Any(x => x.State == EventState.Uncertain);
            }

            if (forceLogin || _state is null)
            {
                ExpandToPill();
                SetStatus("请扫码绑定微信…", YellowBrush);
                _drawer.Visibility = Visibility.Visible;
                _cardBorder.CornerRadius = new CornerRadius(16);

                var progress = new Progress<LoginProgress>(OnLoginProgress);
                var login = new QrLogin(new(_http, _metadata), RequestVerifyCodeAsync);
                var result = await login.RunAsync(progress, token);
                _state = new(result, null, []);
                ProtectedFile.Save(VaultPath, _state);
            }

            // Successfully connected: hide drawer & collapse to mini orb after 2 seconds
            _drawer.Visibility = Visibility.Collapsed;
            ExpandToPill();
            SetStatus("微信已就绪 · 发语音直接打字", GreenBrush);

            _ = Task.Run(async () =>
            {
                await Task.Delay(2000, token);
                Dispatcher.Invoke(() =>
                {
                    if (_drawer.Visibility != Visibility.Visible)
                        CollapseToMiniOrb();
                });
            }, token);

            var client = new ILinkClient(_http, _metadata, _state.Account);
            try { await client.NotifyLifecycleAsync(true, token); } catch { }
            await ReceiveAsync(client, token);
        }
        catch (OperationCanceledException) { }
        catch (SessionExpiredException)
        {
            ExpandToPill();
            SetStatus("微信会话失效，请重新扫码", GrayBrush);
            _drawer.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SetStatus($"连接异常: {ex.Message}", GrayBrush);
        }
    }

    private void OnLoginProgress(LoginProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (progress.QrContent is not null)
            {
                var png = PngByteQRCodeHelper.GetQRCode(progress.QrContent, QRCodeGenerator.ECCLevel.M, 10);
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
                "qrcode" or "wait" => "请用手机微信扫描二维码",
                "scaned" => "已扫码，请在手机上点击确认",
                "need_verifycode" => "请输入手机上显示的验证码",
                "scaned_but_redirect" => "正在切换登录节点…",
                "confirmed" => "登录成功！",
                "expired" => "二维码已过期，正在刷新…",
                _ => "正在连接微信…"
            };
        });
    }

    private Task<string> RequestVerifyCodeAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() =>
        {
            _pendingCode = completion;
            _verifyBox.IsEnabled = _verifySubmitBtn.IsEnabled = true;
            _verifyBox.Focus();
        });
        return completion.Task.WaitAsync(ct);
    }

    private async Task ReceiveAsync(ILinkClient client, CancellationToken ct)
    {
        var parser = new MessageParser();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = await client.GetUpdatesAsync(_state!.Cursor, ct);
                var items = new List<InboxItem>(_state.Events);
                var keys = items.Select(x => x.StorageKey).ToHashSet(StringComparer.Ordinal);
                var fresh = new List<InboxItem>();

                foreach (var message in batch.Messages)
                {
                    var parsed = parser.Parse(message, new(_state.Account.BotId, _state.Account.OwnerId)).Item;
                    if (!keys.Add(parsed.StorageKey)) continue;
                    items.Add(parsed);
                    fresh.Add(parsed);
                }

                var updated = _state with
                {
                    Cursor = string.IsNullOrEmpty(batch.NextCursor) ? _state.Cursor : batch.NextCursor,
                    Events = items
                };
                ProtectedFile.Save(VaultPath, updated);
                _state = updated;

                foreach (var item in fresh)
                {
                    if (item.Event is not null)
                    {
                        _latestText = item.Event.Text;

                        // Wake up & Expand Pill on input!
                        Dispatcher.Invoke(() =>
                        {
                            ExpandToPill();
                            _collapseTimer.Stop();
                            SetStatus($"输入中：{_latestText}", GreenBrush);
                        });

                        if (_autoInput && !_inputPaused)
                        {
                            await _attemptGate.TryRunAsync(async token =>
                            {
                                var res = await FocusedInputProbe.InsertAsync(item.Event.Text, System.IO.Path.Combine(_directory, "input-recovery.dpapi"), token, _autoEnter);
                                Dispatcher.Invoke(() =>
                                {
                                    if (res.State == "Dispatched" || res.State == "Verified")
                                    {
                                        SetStatus($"已打入：{_latestText}", GreenBrush);
                                    }
                                    else
                                    {
                                        SetStatus($"未对准输入框：{_latestText}", YellowBrush);
                                    }
                                    // Start 3 seconds countdown to collapse back to Mini Orb!
                                    _collapseTimer.Start();
                                });
                            }, ct);
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                SetStatus($"收到：{_latestText} (Ctrl+Alt+V补打)", YellowBrush);
                                _collapseTimer.Start();
                            });
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => SetStatus($"重试中: {ex.Message}", GrayBrush));
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task TriggerManualReplayAsync()
    {
        if (string.IsNullOrWhiteSpace(_latestText))
        {
            ExpandToPill();
            SetStatus("暂无最新文字", YellowBrush);
            _collapseTimer.Start();
            return;
        }

        ExpandToPill();
        _collapseTimer.Stop();

        await _attemptGate.TryRunAsync(async token =>
        {
            var res = await FocusedInputProbe.InsertAsync(_latestText, System.IO.Path.Combine(_directory, "input-recovery.dpapi"), token, _autoEnter);
            Dispatcher.Invoke(() =>
            {
                if (res.State == "Dispatched" || res.State == "Verified")
                    SetStatus($"已补写：{_latestText}", GreenBrush);
                else
                    SetStatus($"请先点入输入框: {_latestText}", YellowBrush);

                _collapseTimer.Start();
            });
        }, _session.Token);
    }

    // --- Global Hotkey (Ctrl + Alt + V) ---
    private const int HotkeyId = 9001;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);

    private void RegisterGlobalHotkey()
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(HwndHook);
        RegisterHotKey(helper.Handle, HotkeyId, 0x0002 | 0x0001, 0x56);
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
