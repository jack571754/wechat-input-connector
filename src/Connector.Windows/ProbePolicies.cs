namespace Connector.Windows;

public sealed record TargetObservation(int ProcessId, int AllowedProcessId, string ProcessName,
    bool IsPassword, bool IsEnabled, bool IsReadOnly, bool HasSelection,
    bool ModifiersDown, bool ImeComposing, bool OnInteractiveDesktop, bool HasTextPattern);

public static class ProbePolicies
{
    public static bool IsSafeText(string text) => text.Length is > 0 and <= 10000
        && !text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t');
    public static bool CanInsert(TargetObservation t) => t.ProcessId > 0 && t.ProcessId == t.AllowedProcessId
        && t.ProcessName.Equals("Notepad", StringComparison.OrdinalIgnoreCase) && !t.IsPassword && t.IsEnabled
        && !t.IsReadOnly && !t.HasSelection && !t.ModifiersDown && !t.ImeComposing && t.OnInteractiveDesktop && t.HasTextPattern;

    public static bool CanSnapshot(IReadOnlyCollection<uint> formats) => formats.All(x => x is 1 or 7 or 13 or 16);
    public static bool CanRestore(uint ownedSequence, uint currentSequence, bool contentVerified)
        => contentVerified && ownedSequence != 0 && ownedSequence == currentSequence;
}
