using Connector.Core;
using Connector.WeChat;
using Connector.Windows;
using QRCoder;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Forms = System.Windows.Forms;

namespace Connector.Probe;

internal static class Program
{
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool SetThreadDesktop(nint desktop);

    [STAThread]
    private static void Main(string[] args)
    {
        string? directory = null;
        try
        {
            var desk = OpenInputDesktop(0, false, 0x01FF);
            if (desk != 0) SetThreadDesktop(desk);
            if (args.Contains("--input-helper")) { FocusedInputProbe.RunHelper(); return; }
            directory = ResolveDataDirectory(args);
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "trace.log"), $"[Trace] Main start\n");
            if (args.Contains("--insert-probe"))
            {
                var result = FocusedInputProbe.InsertAsync("【P0 本地输入验证】中文与 emoji 🙂\r\n第二行，不自动发送。",
                    Path.Combine(directory, "input-recovery.dpapi"), default).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(directory, "native-input-result.json"), JsonSerializer.Serialize(result));
                return;
            }
            using var single = new Mutex(true, "Local\\WeChatInputConnector.P0", out var first);
            File.AppendAllText(Path.Combine(directory, "trace.log"), $"[Trace] Main first={first}\n");
            if (!first) { Forms.MessageBox.Show("微信输入连接器已在运行，请查看桌面顶部或右下角系统托盘。", "微信连接器"); return; }
            using var tray = new TrayManager();
            var app = new System.Windows.Application();
            app.DispatcherUnhandledException += (s, e) =>
            {
                File.AppendAllText(Path.Combine(directory, "crash.log"), $"[Dispatcher Exception] {e.Exception}\n");
                e.Handled = false;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                File.AppendAllText(Path.Combine(directory, "crash.log"), $"[AppDomain Exception] {e.ExceptionObject}\n");
            };
            app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            var window = new PillWindow(directory, tray);
            window.Show();
            app.Run(window);
        }
        catch (Exception ex)
        {
            var targetDir = directory ?? AppContext.BaseDirectory;
            File.WriteAllText(Path.Combine(targetDir, "crash.log"), ex.ToString());
        }
    }

    private static string ResolveDataDirectory(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length)
        {
            return Path.GetFullPath(args[index + 1]);
        }

        var localSubdir = Path.Combine(AppContext.BaseDirectory, ".local");
        if (File.Exists(Path.Combine(localSubdir, "probe-state.dpapi")))
        {
            return localSubdir;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, ".local");
            if (File.Exists(Path.Combine(candidate, "probe-state.dpapi")))
            {
                return candidate;
            }
            current = current.Parent;
        }

        return localSubdir;
    }
}

public sealed record ProbeState(LoginResult Account, string? Cursor, List<InboxItem> Events,
    int VoiceTotal = 0, int VoiceWithTranscript = 0);

internal sealed class ProbeForm : Forms.Form
{
    private readonly string _directory;
    private readonly HttpClient _http = ILinkClient.CreateHttpClient();
    private readonly ProtocolMetadata _metadata = ILinkClient.LoadPinnedMetadata();
    private readonly Forms.Label _status = new() { AutoSize = true, Text = "准备连接…" };
    private readonly Forms.PictureBox _qr = new() { SizeMode = Forms.PictureBoxSizeMode.Zoom, Width = 270, Height = 270 };
    private readonly Forms.TextBox _verify = new() { Width = 130, PlaceholderText = "手机显示的验证码", UseSystemPasswordChar = true, Enabled = false };
    private readonly Forms.Button _submit = new() { Text = "提交验证码", AutoSize = true, Enabled = false };
    private readonly Forms.Button _connect = new() { Text = "重新扫码", AutoSize = true };
    private readonly Forms.Button _pause = new() { Text = "暂停接收", AutoSize = true };
    private readonly Forms.Label _metrics = new() { AutoSize = true, Text = "尚未收到消息" };
    private readonly Forms.TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = Forms.ScrollBars.Vertical, Dock = Forms.DockStyle.Fill };
    private readonly Forms.DataGridView _messages = new() { Dock = Forms.DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = Forms.DataGridViewAutoSizeColumnsMode.Fill };
    private CancellationTokenSource _session = new();
    private TaskCompletionSource<string>? _pendingCode;
    private ProbeState? _state;
    private readonly Forms.CheckBox _automatic = new() { Text = "自动输入当前输入框", Checked = true, AutoSize = true };
    private readonly InputAttemptGate _attemptGate = new();
    private bool _inputPaused;
    private string? _lastInputState;
    private string? _lastInputReason;
    private string? _lastBackend;

    internal ProbeForm(string directory)
    {
        _directory = directory;
        Text = "微信连接器 P0 · 接入验证";
        ClientSize = new(920, 720);
        MinimumSize = new(650, 570);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        Font = new("Microsoft YaHei UI", 10);
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new(16), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new(Forms.SizeType.Absolute, 290));
        layout.ColumnStyles.Add(new(Forms.SizeType.Percent, 100));
        layout.RowStyles.Add(new(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new(Forms.SizeType.Absolute, 285));
        layout.RowStyles.Add(new(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new(Forms.SizeType.AutoSize));
        layout.RowStyles.Add(new(Forms.SizeType.Percent, 60));
        layout.RowStyles.Add(new(Forms.SizeType.Percent, 40));
        layout.Controls.Add(_status, 0, 0); layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_qr, 0, 1);
        var instructions = new Forms.Label { Dock = Forms.DockStyle.Fill, Text = "手机微信扫码连接后，点击任意应用中要写入的输入框。\r\n\r\n发送新的微信文字或语音，直接写入当前光标处。\r\n\r\n如果输入框未聚焦或未写入，可点击目标输入框后按 Ctrl+Alt+V 补写，或双击列表项重新写入。\r\n\r\n不改写剪贴板，不自动点击发送。密码框或只读控件自动跳过。" };
        layout.Controls.Add(instructions, 1, 1);
        var controls = new Forms.FlowLayoutPanel { AutoSize = true, Dock = Forms.DockStyle.Fill, WrapContents = true };
        controls.Controls.AddRange([_verify, _submit, _connect, _pause, _automatic]);
        layout.Controls.Add(controls, 0, 2); layout.SetColumnSpan(controls, 2);
        layout.Controls.Add(_metrics, 0, 3); layout.SetColumnSpan(_metrics, 2);
        _messages.Columns.Add("time", "接收时间"); _messages.Columns.Add("state", "处理状态"); _messages.Columns.Add("length", "文字长度");
        layout.Controls.Add(_messages, 0, 4); layout.SetColumnSpan(_messages, 2);
        layout.Controls.Add(_preview, 0, 5); layout.SetColumnSpan(_preview, 2);
        Controls.Add(layout);
        _messages.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _messages.Rows.Count)
            {
                var item = _state?.Events.ElementAtOrDefault(e.RowIndex);
                if (item?.Event is not null)
                {
                    await _attemptGate.TryRunAsync(ct => InsertCoreAsync(item, manual: true, ct), _session.Token);
                }
            }
        };
        _submit.Click += (_, _) => { if (_pendingCode?.TrySetResult(_verify.Text.Trim()) == true) { _verify.Clear(); _verify.Enabled = _submit.Enabled = false; } };
        _pause.Click += (_, _) => { _session.Cancel(); _status.Text = "接收已暂停，点击重新扫码可重新开始。"; WriteStatus("paused"); };
        _connect.Click += async (_, _) => await ConnectAsync(forceLogin: true);
        Shown += async (_, _) => await ConnectAsync(forceLogin: false);
        FormClosing += (_, _) => { _session.Cancel(); _pendingCode?.TrySetCanceled(); };
        FormClosed += (_, _) => { _qr.Image?.Dispose(); _http.Dispose(); _session.Dispose(); };
    }

    private string VaultPath => Path.Combine(_directory, "probe-state.dpapi");

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!RegisterHotKey(Handle, 1, 0x4003, 0x56)) _lastInputReason = "Ctrl+Alt+V 被其他软件占用，手动插入暂不可用。";
    }
    protected override void OnHandleDestroyed(EventArgs e) { UnregisterHotKey(Handle, 1); base.OnHandleDestroyed(e); }
    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == 0x0312 && m.WParam == 1) _ = InsertNextAsync();
        base.WndProc(ref m);
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private async Task InsertNextAsync()
    {
        try
        {
            await _attemptGate.TryRunAsync(async ct =>
            {
                for (var i = 0; i < 50 && new[] { 0x10, 0x11, 0x12 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0); i++) await Task.Delay(20, ct);
                var next = _state?.Events.FirstOrDefault(x => x.State == EventState.Queued && x.Event is not null);
                if (next is not null) await InsertCoreAsync(next, manual: true, ct);
            }, _session.Token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task InsertCoreAsync(InboxItem requested, bool manual, CancellationToken ct)
    {
        var item = _state?.Events.FirstOrDefault(x => x.StorageKey == requested.StorageKey);
        if (_state is null || item?.Event is null || _inputPaused) return;
        if (!manual && (!_automatic.Checked || item.ReasonCode != "ready"
            || (item.Event.CreatedAt is not null && DateTimeOffset.UtcNow - item.Event.CreatedAt > TimeSpan.FromMinutes(2))
            || DateTimeOffset.UtcNow - item.Event.ReceivedAt > TimeSpan.FromSeconds(30))) return;
        try
        {
            var events = ProbeInputFlow.TryBegin(_state.Events, item.StorageKey);
            if (events is null) return;
            var updated = _state with { Events = events.ToList() };
            ProtectedFile.Save(VaultPath, updated);
            _state = updated;
            var result = await FocusedInputProbe.InsertAsync(item.Event.Text, Path.Combine(_directory, "input-recovery.dpapi"), ct);
            var state = Enum.TryParse<EventState>(result.State, out var parsed) ? parsed : EventState.Uncertain;
            UpdateEvent(item.StorageKey, state, result.Reason);
            _lastInputState = result.State; _lastInputReason = result.Reason;
            _lastBackend = result.Backend;
            _inputPaused = state == EventState.Uncertain;
            _status.Text = result.Reason;
            WriteStatus("connected");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { _inputPaused = true; _status.Text = "写入状态无法保存，已暂停自动输入。"; }
    }

    private void UpdateEvent(string key, EventState state, string reason)
    {
        var events = _state!.Events.Select(x => x.StorageKey == key ? x with { State = state, ReasonCode = reason } : x).ToList();
        var updated = _state with { Events = events };
        ProtectedFile.Save(VaultPath, updated);
        _state = updated;
        for (var i = 0; i < _messages.Rows.Count; i++)
        {
            if (_state.Events.Count > i && _state.Events[i].StorageKey == key)
            {
                _messages.Rows[i].Cells[1].Value = Describe(_state.Events[i]);
                break;
            }
        }
    }

    private async Task ConnectAsync(bool forceLogin)
    {
        if (!_connect.Enabled) return;
        _connect.Enabled = false;
        _session.Cancel();
        _session.Dispose();
        _session = new();
        var token = _session.Token;
        try
        {
            if (!forceLogin && File.Exists(VaultPath)) _state = ProtectedFile.Load<ProbeState>(VaultPath);
            if (_state is not null)
            {
                _state = _state with { Events = _state.Events.Select(x => x.State == EventState.Inserting ? x with { State = EventState.Uncertain } : x).ToList() };
                _inputPaused = File.Exists(Path.Combine(_directory, "clipboard-recovery.dpapi"))
                    || File.Exists(Path.Combine(_directory, "input-recovery.dpapi")) || _state.Events.Any(x => x.State == EventState.Uncertain);
            }
            if (forceLogin || _state is null)
            {
                _status.Text = "正在向微信申请二维码…"; WriteStatus("requesting_qrcode");
                var progress = new Progress<LoginProgress>(OnLoginProgress);
                var login = new QrLogin(new(_http, _metadata), RequestVerifyCodeAsync);
                var result = await login.RunAsync(progress, token);
                _state = new(result, null, []);
                ProtectedFile.Save(VaultPath, _state);
            }
            _qr.Image?.Dispose(); _qr.Image = null;
            _status.Text = "已连接；点击要写入的输入框后，从手机发送新消息。";
            WriteStatus("connected");
            var client = new ILinkClient(_http, _metadata, _state.Account);
            try { await client.NotifyLifecycleAsync(true, token); }
            catch (ILinkException) { _status.Text = "已连接；启动通知未确认，继续验证消息接收。"; }
            await ReceiveAsync(client, token);
        }
        catch (OperationCanceledException) { if (!IsDisposed) { _status.Text = "连接已暂停或登录等待超时。"; WriteStatus("paused"); } }
        catch (SessionExpiredException) { _status.Text = "微信会话失效，已停止请求；请稍后重新扫码。"; WriteStatus("session_expired"); }
        catch (ILinkException error) { _status.Text = error.Message; WriteStatus("protocol_error", error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException)
        { _status.Text = "本地加密状态无法读写，已停止接收；请检查目录权限或在客户端重新绑定。"; WriteStatus("storage_error"); }
        finally { if (!IsDisposed) { _connect.Enabled = true; _verify.Enabled = _submit.Enabled = false; } }
    }

    private void OnLoginProgress(LoginProgress progress)
    {
        if (IsDisposed) return;
        if (progress.QrContent is not null)
        {
            var png = PngByteQRCodeHelper.GetQRCode(progress.QrContent, QRCodeGenerator.ECCLevel.M, 10);
            File.WriteAllBytes(Path.Combine(_directory, "login-qr.png"), png);
            using var stream = new MemoryStream(png);
            using var image = Image.FromStream(stream);
            var previous = _qr.Image;
            _qr.Image = new Bitmap(image);
            previous?.Dispose();
        }
        _status.Text = progress.Status switch
        {
            "qrcode" or "wait" => "请用手机微信扫描二维码。",
            "scaned" => "已扫码，正在等待手机确认。",
            "need_verifycode" => "请在下方输入手机微信显示的验证码。",
            "scaned_but_redirect" => "微信正在切换登录节点。",
            "confirmed" => "登录成功，正在安全保存凭据。",
            "expired" => "二维码过期，正在刷新。",
            _ => "微信连接状态已更新。"
        };
        WriteStatus(progress.Status);
    }

    private Task<string> RequestVerifyCodeAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() => { _pendingCode = completion; _verify.Enabled = _submit.Enabled = true; _verify.Focus(); });
        return completion.Task.WaitAsync(ct);
    }

    private async Task ReceiveAsync(ILinkClient client, CancellationToken ct)
    {
        var parser = new MessageParser();
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = await client.GetUpdatesAsync(_state!.Cursor, ct);
                failures = 0;
                var items = new List<InboxItem>(_state.Events);
                var keys = items.Select(x => x.StorageKey).ToHashSet(StringComparer.Ordinal);
                var voices = _state.VoiceTotal;
                var transcripts = _state.VoiceWithTranscript;
                var fresh = new List<InboxItem>();
                foreach (var message in batch.Messages)
                {
                    var parsed = parser.Parse(message, new(_state.Account.BotId, _state.Account.OwnerId)).Item;
                    if (!keys.Add(parsed.StorageKey)) continue;
                    if (items.Count >= 500) throw new ILinkException("P0 队列已满 500 条，接收已停止；请先处理测试数据。");
                    items.Add(parsed); fresh.Add(parsed);
                    if (parsed.State != EventState.Rejected && message.Raw.TryGetProperty("item_list", out var rawItems))
                        foreach (var item in rawItems.EnumerateArray())
                            if (item.TryGetProperty("type", out var type) && type.TryGetInt32(out var n) && n == 3)
                            { voices++; if (item.TryGetProperty("voice_item", out var voice) && voice.TryGetProperty("text", out var text)
                                    && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())) transcripts++; }
                }
                var updated = _state with { Cursor = string.IsNullOrEmpty(batch.NextCursor) ? _state.Cursor : batch.NextCursor,
                    Events = items, VoiceTotal = voices, VoiceWithTranscript = transcripts };
                ProtectedFile.Save(VaultPath, updated);
                _state = updated;
                foreach (var item in fresh)
                {
                    _messages.Rows.Insert(0, DateTime.Now.ToString("HH:mm:ss"), Describe(item), item.Event?.Text.Length ?? 0);
                    if (item.Event is not null) _preview.Text = item.Event.Text;
                }
                await ProbeInputFlow.RunFreshBatchAsync(fresh, _inputPaused || !_automatic.Checked, async (item, cancellation) =>
                {
                    await _attemptGate.TryRunAsync(token => InsertCoreAsync(item, manual: false, token), cancellation);
                    return _state.Events.First(x => x.StorageKey == item.StorageKey).State;
                }, ct);
                while (_messages.Rows.Count > 100) _messages.Rows.RemoveAt(_messages.Rows.Count - 1);
                var accepted = items.Count(x => x.State != EventState.Rejected);
                _metrics.Text = $"收到 {items.Count} 条唯一消息 · 身份通过 {accepted} 条 · 语音 {voices} 条，其中带转写 {transcripts} 条";
                WriteStatus("connected");
            }
            catch (SessionExpiredException) { throw; }
            catch (ILinkException error) when (!ct.IsCancellationRequested && !error.Message.Contains("队列已满"))
            {
                _status.Text = error.Message + " 正在退避重连。";
                WriteStatus("reconnecting", error.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failures++, 4))), ct);
            }
        }
    }

    private static string Describe(InboxItem item) => item.State switch
    {
        EventState.Queued => "待输入",
        EventState.Verified => "已确认写入",
        EventState.Dispatched => "已写入",
        EventState.NeedsTranscript => "语音缺少转写",
        _ => "已拒绝：" + item.ReasonCode
    };

    private void WriteStatus(string status, string? reason = null)
    {
        if (IsDisposed) return;
        var json = JsonSerializer.Serialize(new { Status = status, Reason = reason, UpdatedAt = DateTimeOffset.UtcNow,
            Bound = _state is not null, UniqueMessages = _state?.Events.Count ?? 0,
            AcceptedMessages = _state?.Events.Count(x => x.State != EventState.Rejected) ?? 0,
            VoiceTotal = _state?.VoiceTotal ?? 0, VoiceWithTranscript = _state?.VoiceWithTranscript ?? 0,
            FocusMode = "CurrentInput", LastInputState = _lastInputState, LastInputReason = _lastInputReason, Backend = _lastBackend,
            Queued = _state?.Events.Count(x => x.State == EventState.Queued) ?? 0, InputPaused = _inputPaused,
            Verified = _state?.Events.Count(x => x.State is EventState.Verified or EventState.Dispatched) ?? 0 });
        var path = Path.Combine(_directory, "probe-status.json");
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
