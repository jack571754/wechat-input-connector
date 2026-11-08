namespace Connector.Core;

internal static class InboxRetention
{
    public static bool IsActive(InboxItem item) => item.State is EventState.Queued or EventState.Inserting;
    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static int TextBytes(InboxItem item) => System.Text.Encoding.UTF8.GetByteCount(item.Event?.Text ?? "");
    public static bool CanAccept(ReceiptSnapshot state, InboxItem incoming) => state.Events.Count(IsActive) < 1000
        && state.Events.Where(IsActive).Sum(x => (long)TextBytes(x)) + TextBytes(incoming) <= 4 * 1024 * 1024;
    public static ReceiptSnapshot Apply(ReceiptSnapshot state, DateTimeOffset now)
    {
        var kept = new List<InboxItem>();
        var seen = (state.Seen ?? []).Where(x => now - x.At < TimeSpan.FromDays(30)).ToList();
        var textBytes = 0L;
        var completed = 0;
        foreach (var item in state.Events.Reverse())
        {
            var bytes = TextBytes(item);
            var time = item.Event?.ReceivedAt ?? item.ObservedAt ?? now;
            if (IsActive(item) || (completed < 1000 && textBytes + bytes <= 10 * 1024 * 1024 && now - time < TimeSpan.FromDays(7)))
            {
                kept.Add(item);
                if (!IsActive(item)) { completed++; textBytes += bytes; }
            }
            else seen.Add(new(item.StorageKey, now));
        }
        kept.Reverse();
        var result = state with { Events = kept.ToArray(), Seen = seen.DistinctBy(x => x.Key).TakeLast(10000).ToArray() };
        // 给账号封套及其他字段留空间；旧格式积压超出新预算时只回收终态，绝不删除活动输入。
        var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, Options).Length;
        if (size > 24 * 1024 * 1024)
        {
            foreach (var item in kept.Where(x => !IsActive(x)).ToArray())
            {
                kept.Remove(item); seen.Add(new(item.StorageKey, now));
                size -= System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item, Options).Length;
                if (size < 23 * 1024 * 1024) break;
            }
            result = result with { Events = kept.ToArray(), Seen = seen.DistinctBy(x => x.Key).TakeLast(10000).ToArray() };
        }
        return result;
    }
}
