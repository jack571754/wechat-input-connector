namespace Connector.Windows;

public sealed record FocusIdentity(long Window, int ProcessId, long EditWindow, IReadOnlyList<int>? RuntimeId);

public sealed record TargetObservation(int ForegroundProcessId, int FocusedProcessId, bool IsBoundToForeground,
    bool IsPassword, bool IsEnabled, bool IsReadOnly, bool HasSelection,
    bool ModifiersDown, bool ImeComposing, bool OnInteractiveDesktop, bool HasTarget);

public static class ProbePolicies
{
    public static bool IsSameFocus(FocusIdentity before, FocusIdentity after) => before.Window != 0 && before.Window == after.Window
        && before.ProcessId == after.ProcessId && before.EditWindow == after.EditWindow
        && (before.RuntimeId is null ? after.RuntimeId is null : after.RuntimeId is not null && before.RuntimeId.SequenceEqual(after.RuntimeId));
    public static bool IsEditableTarget(bool nativeEdit, bool editableText, bool writableValue)
        => nativeEdit || editableText || writableValue;
    public static bool CanUseUnicode(string text) => IsSafeText(text) && !text.Any(c => c is '\r' or '\n' or '\t');
    public static bool IsDirectEditTarget(string className, int processId, int allowedProcessId)
        => processId > 0 && processId == allowedProcessId && (className.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase));
    public static bool IsSafeText(string text) => Connector.Core.TextPayload.IsSafe(text);
    public static bool CanInsert(TargetObservation t) => t.ForegroundProcessId > 0 && t.FocusedProcessId > 0
        && t.IsBoundToForeground && !t.IsPassword && t.IsEnabled && !t.IsReadOnly && !t.HasSelection
        && !t.ModifiersDown && !t.ImeComposing && t.OnInteractiveDesktop && t.HasTarget;

    public static bool CanSnapshot(IReadOnlyCollection<uint> formats) => formats.All(x => x is 1 or 7 or 13 or 16);
    public static bool CanRestore(uint ownedSequence, uint currentSequence, bool contentVerified)
        => contentVerified && ownedSequence != 0 && ownedSequence == currentSequence;
}
