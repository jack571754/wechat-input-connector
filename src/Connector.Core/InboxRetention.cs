namespace Connector.Core;

internal static class InboxRetention
{
    public static bool IsActive(InboxItem item) => item.State is EventState.Queued or EventState.Inserting;
    public static ReceiptSnapshot Apply(ReceiptSnapshot state, DateTimeOffset now)
    {
        var kept = new List<InboxItem>();
        var seen = (state.Seen ?? []).Where(x => now - x.At < TimeSpan.FromDays(30)).ToList();
        var textBytes = 0L;
        var completed = 0;
        foreach (var item in state.Events.Reverse())
        {
            var bytes = (long)(item.Event?.Text.Length ?? 0) * 2;
            var time = item.Event?.ReceivedAt ?? item.ObservedAt ?? now;
            if (IsActive(item) || (completed < 1000 && textBytes + bytes <= 10 * 1024 * 1024 && now - time < TimeSpan.FromDays(7)))
            {
                kept.Add(item);
                if (!IsActive(item)) { completed++; textBytes += bytes; }
            }
            else seen.Add(new(item.StorageKey, now));
        }
        kept.Reverse();
        return state with { Events = kept.ToArray(), Seen = seen.DistinctBy(x => x.Key).TakeLast(10000).ToArray() };
    }
}
