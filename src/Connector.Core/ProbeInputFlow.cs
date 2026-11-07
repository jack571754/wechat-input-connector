namespace Connector.Core;

public sealed class InputAttemptGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<bool> TryRunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return false;
        try { await action(ct); return true; }
        finally { _gate.Release(); }
    }
}

public static class ProbeInputFlow
{
    public static async Task RunFreshBatchAsync(IReadOnlyList<InboxItem> items, bool inputPaused,
        Func<InboxItem, CancellationToken, Task<EventState>> attempt, CancellationToken ct)
    {
        if (inputPaused) return;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var state = item.State == EventState.Queued && item.Event is not null ? await attempt(item, ct) : item.State;
            if (state is EventState.Queued or EventState.Inserting or EventState.Uncertain or EventState.NeedsTranscript) break;
        }
    }

    public static IReadOnlyList<InboxItem>? TryBegin(IReadOnlyList<InboxItem> items, string key)
    {
        var current = items.FirstOrDefault(x => x.StorageKey == key);
        if (current?.State != EventState.Queued || current.Event is null) return null;
        return items.Select(x => x.StorageKey == key ? x with { State = EventState.Inserting, ReasonCode = "inserting" } : x).ToArray();
    }
}
