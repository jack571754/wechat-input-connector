using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Connector.Windows;

public sealed record NativeProbeResult(string State, string Reason, string Clipboard);
public sealed record NativeProbeRequest(string Text, int ProcessId);
public static class NotepadProbe
{
    [ThreadStatic] private static nint _clipboardOwner;
    private sealed record Wire(string Stage, NativeProbeResult? Result = null, Dictionary<uint, byte[]>? Snapshot = null, uint Sequence = 0);
    private sealed record Recovery(Dictionary<uint, byte[]> Snapshot, uint Before, uint? Owned, string Stage);
    private sealed record Focus(nint Window, AutomationElement Element, TextPattern Pattern, string Original, int Caret);

    public static async Task<NativeProbeResult> InsertAsync(string text, int processId, string recoveryPath, CancellationToken ct)
    {
        if (processId <= 0 || !ProbePolicies.IsSafeText(text)) return new("Queued", "没有指定记事本或文字不符合输入条件。", "Untouched");
        if (File.Exists(recoveryPath)) return new("Uncertain", "存在未解决的剪贴板恢复记录，自动输入已暂停。", "PendingRecovery");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--input-helper");
        using var child = Process.Start(start) ?? throw new IOException("无法启动输入辅助进程。");
        var prepared = false;
        try
        {
            // 冷启动预算独立于焦点探测预算；ready 后才发送请求和开始计时。
            await ReadWire(child, TimeSpan.FromSeconds(8), ct);
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new NativeProbeRequest(text, processId)));
            var first = await ReadWire(child, TimeSpan.FromMilliseconds(500), ct);
            if (first.Result is not null) return first.Result;
            if (first.Stage != "prepared" || first.Snapshot is null) throw new InvalidDataException();
            var recovery = new Recovery(first.Snapshot, first.Sequence, null, "prepared");
            ProtectedFile.Save(recoveryPath, recovery);
            prepared = true;
            await child.StandardInput.WriteLineAsync("saved");
            var written = await ReadWire(child, TimeSpan.FromSeconds(2), ct);
            if (written.Result is not null)
            {
                if (written.Result.Clipboard == "Untouched") File.Delete(recoveryPath);
                return written.Result;
            }
            if (written.Stage != "written") throw new InvalidDataException();
            ProtectedFile.Save(recoveryPath, recovery with { Owned = written.Sequence, Stage = "written" });
            await child.StandardInput.WriteLineAsync("saved");
            var complete = await ReadWire(child, TimeSpan.FromSeconds(3), ct);
            var result = complete.Result ?? throw new InvalidDataException();
            if (result.Clipboard is "Restored" or "UserChanged" or "Untouched") File.Delete(recoveryPath);
            return result;
        }
        catch (Exception error) when (error is IOException or TimeoutException or JsonException or OperationCanceledException
            or System.ComponentModel.Win32Exception)
        {
            return prepared ? new("Uncertain", "输入辅助进程结果不明，已暂停并保留剪贴板恢复记录。", "PendingRecovery")
                : new("Queued", "焦点探测未在限定时间内完成，未执行粘贴。", "Untouched");
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    private static async Task<Wire> ReadWire(Process child, TimeSpan timeout, CancellationToken ct)
    {
        var line = await child.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(timeout, ct);
        return line is not null ? JsonSerializer.Deserialize<Wire>(line) ?? throw new InvalidDataException() : throw new EndOfStreamException();
    }

    private static void Send(Wire wire) { Console.Out.WriteLine(JsonSerializer.Serialize(wire)); Console.Out.Flush(); }

    public static void RunHelper()
    {
        using var owner = new ClipboardOwner();
        _clipboardOwner = owner.Handle;
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var mutated = false;
        try
        {
            Send(new("ready"));
            var request = JsonSerializer.Deserialize<NativeProbeRequest>(Console.ReadLine() ?? "") ?? throw new InvalidDataException();
            if (!ProbePolicies.IsSafeText(request.Text)) throw new ProbeRefusal("文字不符合自动输入条件。");
            var focus = CaptureFocus(request.ProcessId);
            var (snapshot, sequence) = CaptureClipboard();
            Send(new("prepared", Snapshot: snapshot, Sequence: sequence));
            if (Console.ReadLine() != "saved") return;
            VerifyFocus(focus, request.ProcessId);
            OpenClipboardChecked();
            uint owned;
            try
            {
                if (Native.GetClipboardSequenceNumber() != sequence) throw new ProbeRefusal("用户正在修改剪贴板，本次输入已暂停。");
                var bytes = Encoding.Unicode.GetBytes(request.Text + '\0');
                var memory = Allocate(bytes);
                if (!Native.EmptyClipboard()) { Native.GlobalFree(memory); throw new IOException(); }
                mutated = true;
                if (Native.SetClipboardData(13, memory) == 0) { Native.GlobalFree(memory); throw new IOException(); }
                owned = Native.GetClipboardSequenceNumber();
            }
            finally { Native.CloseClipboard(); }
            Send(new("written", Sequence: owned));
            if (Console.ReadLine() != "saved") return;
            VerifyFocus(focus, request.ProcessId);
            Native.Input[] keys = [Native.Key(0x11), Native.Key(0x56), Native.Key(0x56, 2), Native.Key(0x11, 2)];
            if (Native.SendInput((uint)keys.Length, keys, Marshal.SizeOf<Native.Input>()) != keys.Length) throw new IOException();
            var expected = focus.Original[..focus.Caret] + Normalize(request.Text) + focus.Original[focus.Caret..];
            var clock = Stopwatch.StartNew();
            var verified = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (Native.GetForegroundWindow() != focus.Window) break;
                if (Normalize(focus.Pattern.DocumentRange.GetText(100001)) == expected) { verified = true; break; }
                Thread.Sleep(15);
            }
            if (!verified) { Send(new("done", new("Uncertain", "已派发粘贴，但目标内容尚未可靠确认。", "PendingRecovery"))); return; }
            OpenClipboardChecked();
            var restored = false;
            try
            {
                if (ProbePolicies.CanRestore(owned, Native.GetClipboardSequenceNumber(), verified))
                {
                    // 在清空前准备所有句柄；成功交给系统后不再释放其所有权。
                    var blocks = snapshot.Select(x => (Format: x.Key, Handle: Allocate(x.Value))).ToList();
                    try
                    {
                        if (!Native.EmptyClipboard()) throw new IOException();
                        for (var i = 0; i < blocks.Count; i++)
                        {
                            if (Native.SetClipboardData(blocks[i].Format, blocks[i].Handle) == 0) throw new IOException();
                            blocks[i] = (blocks[i].Format, 0);
                        }
                        restored = true;
                    }
                    finally { foreach (var block in blocks) if (block.Handle != 0) Native.GlobalFree(block.Handle); }
                }
            }
            finally { Native.CloseClipboard(); }
            Send(new("done", new("Verified", "已在指定记事本确认文字。", restored ? "Restored" : "UserChanged")));
        }
        catch (ProbeRefusal error) { Send(new("done", new(mutated ? "Uncertain" : "Queued", error.Message, mutated ? "PendingRecovery" : "Untouched"))); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ElementNotAvailableException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception or COMException or JsonException)
        { Send(new("done", new(mutated ? "Uncertain" : "Queued", "Windows 探测或输入未完成，已停止本次操作。", mutated ? "PendingRecovery" : "Untouched"))); }
    }

    private static Focus CaptureFocus(int expectedPid)
    {
        var window = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(window, out var windowPid);
        if (window == 0 || windowPid != expectedPid) throw new ProbeRefusal("当前前台不是指定的记事本，请点入测试文本区后发送新消息。");
        var element = AutomationElement.FocusedElement ?? throw new ProbeRefusal("未检测到可输入焦点。");
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)) throw new ProbeRefusal("当前控件不支持可靠文本验证。");
        var pattern = (TextPattern)patternObject;
        var selections = pattern.GetSelection();
        var hasSelection = selections.Length != 1 || selections[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selections[0], TextPatternRangeEndpoint.End) != 0;
        var readOnly = pattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
        var password = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
        var process = Process.GetProcessById(expectedPid);
        var observation = new TargetObservation(element.Current.ProcessId, expectedPid, process.ProcessName,
            password is not false, element.Current.IsEnabled, readOnly is not false, hasSelection,
            ModifiersDown(), ImeComposing(element.Current.NativeWindowHandle), OnDefaultDesktop() && Native.AllowsIntegrity(expectedPid), true);
        if (!ProbePolicies.CanInsert(observation)) throw new ProbeRefusal("当前控件、选区、权限或输入状态不符合安全输入条件。");
        var original = Normalize(pattern.DocumentRange.GetText(100001));
        if (original.Length > 100000) throw new ProbeRefusal("P0 只验证小型记事本文档。");
        var prefix = pattern.DocumentRange.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selections[0], TextPatternRangeEndpoint.Start);
        return new(window, element, pattern, original, Normalize(prefix.GetText(100001)).Length);
    }

    private static void VerifyFocus(Focus before, int processId)
    {
        var now = CaptureFocus(processId);
        if (now.Window != before.Window || !Automation.Compare(now.Element, before.Element)
            || now.Caret != before.Caret || now.Original != before.Original) throw new ProbeRefusal("焦点或文档已变化，未继续输入。");
    }

    private static (Dictionary<uint, byte[]>, uint) CaptureClipboard()
    {
        OpenClipboardChecked();
        try
        {
            List<uint> formats = [];
            uint format = 0;
            while ((format = Native.EnumClipboardFormats(format)) != 0) formats.Add(format);
            if (!ProbePolicies.CanSnapshot(formats)) throw new ProbeRefusal("剪贴板包含图片、文件或富文本，已保留原内容并暂停写入。");
            Dictionary<uint, byte[]> result = [];
            foreach (var f in formats)
            {
                var handle = Native.GetClipboardData(f);
                var size = Native.GlobalSize(handle);
                if (handle == 0 || size is 0 or > 1048576) throw new ProbeRefusal("剪贴板内容无法完整保护。");
                var pointer = Native.GlobalLock(handle);
                if (pointer == 0) throw new IOException();
                try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); result[f] = bytes; }
                finally { Native.GlobalUnlock(handle); }
            }
            return (result, Native.GetClipboardSequenceNumber());
        }
        finally { Native.CloseClipboard(); }
    }

    private static nint Allocate(byte[] bytes)
    {
        var memory = Native.GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == 0) throw new IOException();
        var pointer = Native.GlobalLock(memory);
        if (pointer == 0) { Native.GlobalFree(memory); throw new IOException(); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { Native.GlobalUnlock(memory); }
        return memory;
    }

    private static void OpenClipboardChecked()
    {
        for (var i = 0; i < 3; i++) { if (_clipboardOwner != 0 && Native.OpenClipboard(_clipboardOwner)) return; Thread.Sleep(5); }
        throw new ProbeRefusal("剪贴板正被其他程序占用。");
    }
    private static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0);
    private static bool ImeComposing(nint window)
    {
        var context = Native.ImmGetContext(window);
        if (context == 0) return false;
        try { return Native.ImmGetCompositionStringW(context, 8, 0, 0) > 0; }
        finally { Native.ImmReleaseContext(window, context); }
    }
    private static bool OnDefaultDesktop()
    {
        var desktop = Native.OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try { var name = new StringBuilder(128); return Native.GetUserObjectInformationW(desktop, 2, name, 256, out _) && name.ToString() == "Default"; }
        finally { Native.CloseDesktop(desktop); }
    }
    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    private sealed class ProbeRefusal(string message) : Exception(message);
    private sealed class ClipboardOwner : System.Windows.Forms.NativeWindow, IDisposable
    {
        internal ClipboardOwner() => CreateHandle(new System.Windows.Forms.CreateParams { Caption = "WeChatInputConnector.P0.Helper", Parent = -3 });
        public void Dispose() => DestroyHandle();
    }
}

internal static class Native
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32.dll")] internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] internal static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] internal static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] internal static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
    [DllImport("imm32.dll")] internal static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] internal static extern int ImmGetCompositionStringW(nint context, uint index, nint buffer, uint length);
    [DllImport("imm32.dll")] internal static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("user32.dll")] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetUserObjectInformationW(nint obj, int index, StringBuilder info, uint length, out uint needed);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] internal struct Union { [FieldOffset(0)] internal Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal Union Data; }
    internal static Input Key(ushort key, uint flags = 0) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = flags } } };
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int info, nint buffer, uint length, out uint needed);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);

    internal static bool AllowsIntegrity(int targetPid)
    {
        var target = Integrity(targetPid); var own = Integrity(Environment.ProcessId);
        return target.HasValue && own.HasValue && target <= own;
    }
    private static uint? Integrity(int processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (process == 0) return null;
        nint token = 0;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) return null;
            GetTokenInformation(token, 25, 0, 0, out var size);
            if (size == 0) return null;
            var memory = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetTokenInformation(token, 25, memory, size, out _)) return null;
                var sid = Marshal.ReadIntPtr(memory);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count == 0 ? null : unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1)));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { if (token != 0) CloseHandle(token); CloseHandle(process); }
    }
}
