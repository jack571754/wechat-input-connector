namespace Connector.Windows;

public sealed record TargetObservation(int ForegroundProcessId, int FocusedProcessId, bool IsBoundToForeground,
    bool IsPassword, bool IsEnabled, bool IsReadOnly, bool HasSelection,
    bool ModifiersDown, bool ImeComposing, bool OnInteractiveDesktop, bool HasTarget);

public static class ProbePolicies
{
    public static bool IsDirectEditTarget(string className, int processId, int allowedProcessId)
        => processId > 0 && processId == allowedProcessId && (className.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase));
    public static bool IsSafeText(string text) => text.Length is > 0 and <= 10000
        && !text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t');
    public static bool CanInsert(TargetObservation t) => t.ForegroundProcessId > 0 && t.FocusedProcessId > 0
        && t.IsBoundToForeground && !t.IsPassword && t.IsEnabled && !t.IsReadOnly && !t.HasSelection
        && !t.ModifiersDown && !t.ImeComposing && t.OnInteractiveDesktop && t.HasTarget;

    public static bool CanSnapshot(IReadOnlyCollection<uint> formats) => formats.All(x => x is 1 or 7 or 13 or 16);
    public static bool CanRestore(uint ownedSequence, uint currentSequence, bool contentVerified)
        => contentVerified && ownedSequence != 0 && ownedSequence == currentSequence;
}
