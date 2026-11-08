using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Connector.Windows;

public sealed record NativeProbeResult(string State, string Reason, string Clipboard, string? Backend = null, string? FailureCode = null)
{
    public Connector.Core.InputOutcome ToOutcome()
    {
        var state = Enum.TryParse<Connector.Core.EventState>(State, out var parsed) ? parsed : Connector.Core.EventState.Uncertain;
        var failure = state is Connector.Core.EventState.Dispatched or Connector.Core.EventState.Verified ? Connector.Core.InputFailure.NoInput
            : state == Connector.Core.EventState.Uncertain ? Connector.Core.InputFailure.Uncertain : FailureCode switch
            {
                "focus_changed" => Connector.Core.InputFailure.FocusChanged,
                "multiline_unsupported" => Connector.Core.InputFailure.MultilineUnsupported,
                "timeout" => Connector.Core.InputFailure.Timeout,
                "storage" => Connector.Core.InputFailure.StorageFailure,
                _ => Connector.Core.InputFailure.TargetUnavailable
            };
        return new(state, failure);
    }
}
public sealed record NativeProbeRequest(string Text, int OriginProcessId, bool AutoEnter = false, long TargetWindow = 0, int TargetProcessId = 0)
{
    public override string ToString() => "输入请求（正文已隐藏）";
}

public static class FocusedInputProbe
{
    private sealed record Wire(string Stage, NativeProbeResult? Result = null, int FocusPid = 0, string? Backend = null);
    private sealed record Recovery(string Stage, int FocusPid, string? Backend, DateTimeOffset CreatedAt);
    private sealed record Focus(nint Window, nint EditWindow, int ProcessId, AutomationElement? Element,
        string? Original, int Caret, string Backend);

    public static async Task<NativeProbeResult> InsertAsync(string text, string recoveryPath, CancellationToken ct, bool autoEnter = false)
    {
        if (!ProbePolicies.IsSafeText(text)) return new("Queued", "文字不符合自动输入条件。", "Untouched");
        try { InputRecovery.ArchiveUncertain(recoveryPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new("Queued", "旧输入阶段记录无法归档，本条尚未执行输入。", "Untouched"); }

        ct.ThrowIfCancellationRequested();
        return await InsertViaHelperAsync(text, recoveryPath, autoEnter, ct);
    }

    private static async Task<NativeProbeResult> InsertViaHelperAsync(string text, string recoveryPath, bool autoEnter, CancellationToken ct)
    {
        var prepared = false;
        var targetWindow = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(targetWindow, out var targetPid);
        try
        {
            using var job = new OwnedHelperJob();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--input-helper");
            using var child = Process.Start(start) ?? throw new IOException();
            try
            {
                job.Attach(child);
                if ((await ReadWire(child, TimeSpan.FromSeconds(8), ct)).Stage != "ready") throw new InvalidDataException();
                ct.ThrowIfCancellationRequested();
                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new NativeProbeRequest(text, Environment.ProcessId, autoEnter, (long)targetWindow, (int)targetPid)));
                var first = await ReadWire(child, TimeSpan.FromMilliseconds(800), ct);
                if (first.Result is not null) return first.Result;
                if (first.Stage != "prepared") throw new InvalidDataException();
                ProtectedFile.Save(recoveryPath, new Recovery("prepared", first.FocusPid, first.Backend, DateTimeOffset.UtcNow));
                prepared = true;
                ct.ThrowIfCancellationRequested();
                await child.StandardInput.WriteLineAsync("saved");
                var result = (await ReadWire(child, TimeSpan.FromSeconds(3), ct)).Result ?? throw new InvalidDataException();
                if (result.State is "Verified" or "Dispatched" or "Queued") File.Delete(recoveryPath);
                return result;
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or JsonException or OperationCanceledException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return prepared ? new("Uncertain", "输入结果不明，已保留恢复记录，本条不再重发。", "Untouched")
                : new("Queued", "焦点探测未完成，未执行输入。", "Untouched", FailureCode:
                    error is TimeoutException ? "timeout" : error is System.Security.Cryptography.CryptographicException ? "storage" : "target_unavailable");
        }
    }

    private static async Task<Wire> ReadWire(Process child, TimeSpan timeout, CancellationToken ct)
    {
        var line = await child.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(timeout, ct);
        return line is not null ? JsonSerializer.Deserialize<Wire>(line) ?? throw new InvalidDataException() : throw new EndOfStreamException();
    }
    private static void Send(Wire wire) { Console.Out.WriteLine(JsonSerializer.Serialize(wire)); Console.Out.Flush(); }

    public static void RunHelper()
    {
        var worker = new Thread(RunHelperCore);
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
    }

    private static void RunHelperCore()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var dispatched = false;
        try
        {
            Send(new("ready"));
            var request = JsonSerializer.Deserialize<NativeProbeRequest>(Console.ReadLine() ?? "") ?? throw new InvalidDataException();
            if (!ProbePolicies.IsSafeText(request.Text)) throw new ProbeRefusal("文字不符合输入条件。");
            var focus = CaptureFocus(request.OriginProcessId);
            if (request.TargetWindow != 0 && ((long)focus.Window != request.TargetWindow
                || (int)GetWindowProcess(focus.Window) != request.TargetProcessId))
                throw new ProbeRefusal("焦点在输入准备期间发生变化。", "focus_changed");
            if (focus.Backend == "Unicode" && !ProbePolicies.CanUseUnicode(request.Text))
                throw new ProbeRefusal("当前输入框不支持安全插入换行或制表符，内容已保留。", "multiline_unsupported");
            if (request.Text.Length > 10000)
                throw new ProbeRefusal("文本超出长度限制，已保留在队列。");
            Send(new("prepared", FocusPid: focus.ProcessId, Backend: focus.Backend));
            if (Console.ReadLine() != "saved") return;
            VerifyFocus(focus, request.OriginProcessId);
            // 选择后端发生在派发前，失败后不自动换一种方式重复写入。
            if (focus.Backend == "EditMessage")
            {
                dispatched = true;
                if (Native.SendMessageTimeoutW(focus.EditWindow, 0x00C2, 1, request.Text, 0x23, 500, out _) == 0)
                    throw new IOException();
            }
            else
            {
                var inputList = new List<Native.Input>();
                foreach (var c in request.Text)
                {
                    inputList.Add(Native.Unicode(c, false));
                    inputList.Add(Native.Unicode(c, true));
                }
                dispatched = true;
                var keys = inputList.ToArray();
                if (Native.SendInput((uint)keys.Length, keys, Marshal.SizeOf<Native.Input>()) != keys.Length) throw new IOException();
            }

            if (request.AutoEnter)
            {
                VerifyFocus(focus, request.OriginProcessId);
                var enter = new[] { Native.Key(0x0D), Native.Key(0x0D, 2) };
                if (Native.SendInput(2, enter, Marshal.SizeOf<Native.Input>()) != 2) throw new IOException();
            }
            if (Native.GetForegroundWindow() == focus.Window)
            {
                Send(new("done", new("Dispatched", "已派发到当前输入框。", "Untouched", focus.Backend)));
                return;
            }

            Send(new("done", new("Uncertain", "已派发，但目标窗口发生切换；本条不再重发。", "Untouched", focus.Backend)));
        }
        catch (ProbeRefusal error) { Send(new("done", new(dispatched ? "Uncertain" : "Queued", error.Message, "Untouched", FailureCode: error.Code))); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ElementNotAvailableException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception or COMException or JsonException or ArgumentException)
        { Send(new("done", new(dispatched ? "Uncertain" : "Queued", "Windows 焦点检查或输入未完成，已停止本次操作。", "Untouched"))); }
    }

    private static Focus CaptureFocus(int originPid)
    {
        var window = Native.GetForegroundWindow();
        var thread = Native.GetWindowThreadProcessId(window, out var foregroundPid);
        if (window == 0 || foregroundPid == 0 || foregroundPid == originPid || foregroundPid == Environment.ProcessId)
            throw new ProbeRefusal("请先点入外部应用中要写入的输入框。", "no_target");

        var element = AutomationElement.FocusedElement;
        var focusPid = element?.Current.ProcessId ?? (int)foregroundPid;

        var isPassword = element is not null && element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is true;

        var isEnabled = element?.Current.IsEnabled ?? true;
        if (!isEnabled) throw new ProbeRefusal("当前控件不可用，未执行输入。");

        TextPattern? pattern = null;
        if (element is not null && element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject))
            pattern = patternObject as TextPattern;

        ValuePattern? valPattern = null;
        if (element is not null && element.TryGetCurrentPattern(ValuePattern.Pattern, out var valObject))
            valPattern = valObject as ValuePattern;

        var readOnly = false;
        if (pattern is not null)
            readOnly = pattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is true;
        else if (valPattern is not null)
            readOnly = valPattern.Current.IsReadOnly;

        if (readOnly) throw new ProbeRefusal("当前控件为只读，未执行输入。");

        var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        var nativeEdit = element is not null ? (nint)element.Current.NativeWindowHandle : 0;
        if (nativeEdit == 0 && Native.GetGUIThreadInfo(thread, ref info)) nativeEdit = info.Focus;

        var className = new StringBuilder(256);
        if (nativeEdit != 0) Native.GetClassNameW(nativeEdit, className, 256);
        Native.GetWindowThreadProcessId(nativeEdit, out var editPid);
        var direct = nativeEdit != 0 && ProbePolicies.IsDirectEditTarget(className.ToString(), (int)editPid, focusPid);
        if (direct)
        {
            var style = Native.GetWindowLongW(nativeEdit, -16);
            if ((style & 0x0020) != 0) isPassword = true;
            if ((style & 0x0800) != 0) throw new ProbeRefusal("当前控件为只读，未执行输入。");
        }
        if (isPassword) throw new ProbeRefusal("密码框已跳过自动输入。");

        var bound = nativeEdit != 0 && Native.GetAncestor(nativeEdit, 2) == window;
        if (!bound && element is not null)
        {
            var root = AutomationElement.FromHandle(window);
            var candidate = element;
            for (var i = 0; i < 16 && candidate is not null; i++)
            {
                if (Automation.Compare(candidate, root)) { bound = true; break; }
                candidate = TreeWalker.ControlViewWalker.GetParent(candidate);
            }
        }

        var hasSelection = false;
        if (pattern is not null)
        {
            try
            {
                var selections = pattern.GetSelection();
                hasSelection = selections.Length != 1 || selections[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selections[0], TextPatternRangeEndpoint.End) != 0;
            }
            catch (Exception) { throw new ProbeRefusal("无法核对选区，未执行输入。", "selection_unavailable"); }
        }

        var hasTarget = ProbePolicies.IsEditableTarget(direct,
            pattern is not null && !readOnly && element?.Current.IsKeyboardFocusable == true,
            valPattern is not null && !readOnly && element?.Current.IsKeyboardFocusable == true);

        var observation = new TargetObservation((int)foregroundPid, focusPid, bound, isPassword,
            isEnabled, readOnly, hasSelection, ModifiersDown(), ImeComposing(nativeEdit),
            OnDefaultDesktop() && Native.AllowsIntegrity(focusPid) && Native.AllowsIntegrity((int)foregroundPid), hasTarget);

        if (!ProbePolicies.CanInsert(observation)) throw new ProbeRefusal("当前控件、选区、权限或输入状态不符合输入条件。");

        return new(window, nativeEdit, focusPid, element, null, 0,
            direct ? "EditMessage" : "Unicode");
    }

    private static void VerifyFocus(Focus before, int originPid)
    {
        var now = CaptureFocus(originPid);
        if (now.Window != before.Window || now.ProcessId != before.ProcessId)
            throw new ProbeRefusal("焦点窗口已经变化，未继续输入。");
        if (before.EditWindow != 0 && now.EditWindow != 0 && now.EditWindow != before.EditWindow)
            throw new ProbeRefusal("焦点控件已经变化，未继续输入。");
        if (before.Element is not null && now.Element is not null)
        {
            try
            {
                if (!Automation.Compare(now.Element, before.Element))
                    throw new ProbeRefusal("焦点控件已经变化，未继续输入。");
            }
            catch (ProbeRefusal) { throw; }
            catch (Exception) { throw new ProbeRefusal("无法确认焦点控件未变化。", "focus_changed"); }
        }
        if (before.Original is not null && now.Original is not null)
        {
            if (now.Caret != before.Caret || now.Original != before.Original)
                throw new ProbeRefusal("文档已经变化，未继续输入。");
        }
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
    private static uint GetWindowProcess(nint window)
    {
        Native.GetWindowThreadProcessId(window, out var pid);
        return pid;
    }
    private sealed class ProbeRefusal(string message, string code = "target_unavailable") : Exception(message)
    {
        public string Code { get; } = code;
    }
}

internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint window, StringBuilder value, int length);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo { internal uint Size, Flags; internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeoutW(nint window, uint message, nuint parameter, string text, uint flags, uint timeout, out nuint result);
    internal static Input Unicode(char value, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Scan = value, Flags = up ? 6u : 4u } } };

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("imm32.dll")] internal static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] internal static extern int ImmGetCompositionStringW(nint context, uint index, nint buffer, uint length);
    [DllImport("imm32.dll")] internal static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("user32.dll")] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetUserObjectInformationW(nint obj, int index, StringBuilder info, uint length, out uint needed);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern int GetWindowLongW(nint window, int index);
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
