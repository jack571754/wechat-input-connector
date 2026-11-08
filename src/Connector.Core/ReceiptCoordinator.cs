using System.Security.Cryptography;
using System.Text;

namespace Connector.Core;

public sealed class ReceiptCoordinator
{
    public async Task<string?> ProcessNextAsync(Func<TextEvent, CancellationToken, Task<InputOutcome>> writer, CancellationToken ct)
    {
        string? last = null;
        while (true)
        {
            var next = Snapshot.Events.FirstOrDefault(x => x.State == EventState.Queued && x.Event is not null);
            if (next is null) return last;
            await ProcessAsync(next.StorageKey, writer, true, ct);
            last = next.StorageKey;
            if (Snapshot.Events.FirstOrDefault(x => x.StorageKey == last)?.State != EventState.Rejected) return last;
        }
    }
    public void MarkSessionExpired()
    {
        lock (_sync)
        {
            if (!_state.Connected) return;
            Commit(_state with { Pending = [], LastContext = null, Connected = false, InputPaused = true, PauseCause = InputPauseCause.SessionExpired,
                Events = _state.Events.Select(x => x with { Reply = null }).ToArray(), Skipped = _state.Skipped + _state.Pending.Count });
        }
    }
    private readonly object _sync = new();
    private readonly SemaphoreSlim _input = new(1, 1);
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly BoundIdentity _identity;
    private readonly Action<ReceiptSnapshot> _save;
    private readonly TimeProvider _clock;
    private ReceiptSnapshot _state;
    private DateTimeOffset Now => _clock.GetUtcNow();

    public ReceiptCoordinator(ReceiptSnapshot snapshot, BoundIdentity identity, Action<ReceiptSnapshot> save, TimeProvider? clock = null)
    {
        _identity = identity; _save = save; _clock = clock ?? TimeProvider.System; _state = snapshot;
        var pauseCause = snapshot.PauseCause;
        if (snapshot.InputPaused && pauseCause == InputPauseCause.None)
        {
            // 旧版叠加原因不可还原：按当前容量迁移；有空位且存在未知记录时继续新输入。
            // 新版已明确标记的其他阻塞不会进入这条迁移分支。
            pauseCause = !snapshot.Connected ? InputPauseCause.SessionExpired
                : snapshot.Pending.Count(x => !x.Query) >= 99 ? InputPauseCause.OutboxFull
                : snapshot.Events.Any(x => x.State is EventState.Inserting or EventState.Uncertain) ? InputPauseCause.None
                : InputPauseCause.UnknownLegacy;
        }
        var recovered = snapshot with
        {
            Events = snapshot.Events.Select(x => x.State == EventState.Inserting ? x with { State = EventState.Uncertain, ReasonCode = "restart_uncertain" } : x).ToArray(),
            Pending = snapshot.Pending.Where(x => x.Attempts < 4).Select(x => x with
            {
                Text = x.Text.Replace("输入结果不明，自动输入已暂停；请检查电脑后再处理本条。", ReceiptFormatter.UncertainText, StringComparison.Ordinal)
                    .Replace("，尚未确认写入结果。", "。", StringComparison.Ordinal),
                Sending = false,
                NextAttemptAt = x.Sending && x.Attempts > 0 ? Now.AddSeconds(new[] { 2, 5, 15 }[x.Attempts - 1]) : x.NextAttemptAt
            }).ToArray(),
            Skipped = snapshot.Skipped + snapshot.Pending.Count(x => x.Attempts >= 4),
            InputPaused = pauseCause != InputPauseCause.None,
            PauseCause = pauseCause
        };
        Commit(Prune(recovered));
    }

    public ReceiptSnapshot Snapshot { get { lock (_sync) return _state; } }
    private bool IdentityMatches(ReplyContext c) => c.AccountId == _identity.BotId && c.OwnerId == _identity.OwnerId;
    private bool Fresh(ReplyContext? c) => c is not null && IdentityMatches(c) && !string.IsNullOrWhiteSpace(c.ContextToken)
        && Now - c.ReceivedAt >= TimeSpan.Zero && Now - c.ReceivedAt < TimeSpan.FromMinutes(5);
    private void Commit(ReceiptSnapshot next)
    {
        next = InboxRetention.Apply(next, Now);
        if (next.PauseCause == InputPauseCause.OutboxFull && next.Pending.Count(x => !x.Query) < 99)
            next = next with { InputPaused = false, PauseCause = InputPauseCause.None };
        // 保存成功后才发布新快照；收件、输入完成和发件完成均经过这个短临界区。
        _save(next); _state = next;
        if (_changed.CurrentCount == 0) _changed.Release();
    }
    private static ReceiptSnapshot Replace(ReceiptSnapshot state, InboxItem item)
        => state with { Events = state.Events.Select(x => x.StorageKey == item.StorageKey ? item : x).ToArray() };

    public IReadOnlyList<InboxItem> CommitBatch(string? cursor, IReadOnlyList<InboxItem> items)
    {
        lock (_sync)
        {
            var next = Prune(_state); var keys = next.Events.Select(x => x.StorageKey)
                .Concat((next.Seen ?? []).Select(x => x.Key)).ToHashSet(StringComparer.Ordinal);
            var fresh = new List<InboxItem>();
            foreach (var incoming in items)
            {
                var item = incoming;
                if (item.ReasonCode == "bot_echo" || item.Reply is not null && (!IdentityMatches(item.Reply) || item.Reply.SourceMessageId != item.StorageKey)
                    || item.Event is not null && (item.Event.AccountId != _identity.BotId || item.Event.SenderId != _identity.OwnerId)) continue;
                if (!keys.Add(item.StorageKey)) continue;
                if (item.Reply?.ContextToken.Length > 16384) item = item with { Reply = null };
                item = item with { ObservedAt = item.ObservedAt ?? item.Event?.ReceivedAt ?? item.Reply?.ReceivedAt ?? Now,
                    Identity = item.Event is not null || item.Reply is not null ? _identity : item.Identity };
                var inboxFull = item.Event is not null && item.Event.Text.Trim() != "/状态"
                    && !InboxRetention.CanAccept(next, item);
                if (inboxFull) item = item with { State = EventState.Rejected, ReasonCode = "inbox_full", Failure = InputFailure.InboxFull };
                next = next with { Events = next.Events.Append(item).ToArray(), Connected = true };
                if (Fresh(item.Reply)) next = next with { LastContext = item.Reply };
                if (item.Event?.Text.Trim() == "/状态")
                {
                    var query = item with { State = EventState.Discarded, ReasonCode = "status_query" };
                    next = Replace(next, query);
                    next = Enqueue(next, query.Reply, "query:" + item.StorageKey, ReceiptFormatter.Status(next), true);
                }
                else if (inboxFull)
                    next = ReceiptFor(next, item, new(EventState.Queued, InputFailure.InboxFull));
                else if (item.State == EventState.NeedsTranscript)
                    next = ReceiptFor(next, item, new(EventState.NeedsTranscript));
                else fresh.Add(item);
            }
            next = next with { Cursor = string.IsNullOrEmpty(cursor) ? next.Cursor : cursor };
            Commit(next); return fresh;
        }
    }

    public async Task ProcessAsync(string key, Func<TextEvent, CancellationToken, Task<InputOutcome>> writer, bool manual, CancellationToken ct)
    {
        await _input.WaitAsync(ct);
        try
        {
            InboxItem item;
            lock (_sync)
            {
                var found = _state.Events.FirstOrDefault(x => x.StorageKey == key);
                if (found?.State != EventState.Queued || found.Event is null) return;
                item = found;
                if (!TextPayload.IsSafe(item.Event!.Text))
                {
                    item = item with { State = EventState.Rejected, ReasonCode = "invalid_text" };
                    Commit(ReceiptFor(Replace(_state, item), item, new(EventState.Rejected, InputFailure.InvalidText)));
                    return;
                }
                var failure = _state.InputPaused || !manual && !_state.AutoInput ? InputFailure.Paused
                    : _state.Pending.Count(x => !x.Query) >= 99 ? InputFailure.QueueFull
                    : !manual && (item.ReasonCode != "ready" || item.Event.CreatedAt is null
                        || Now - item.Event.CreatedAt > TimeSpan.FromSeconds(15) || item.Event.CreatedAt - Now > TimeSpan.FromSeconds(5)) ? InputFailure.Expired
                    : (InputFailure?)null;
                if (failure is not null) { item = item with { Failure = failure }; Commit(ReceiptFor(Replace(_state, item), item, new(EventState.Queued, failure.Value))); return; }
                ct.ThrowIfCancellationRequested();
                item = item with { State = EventState.Inserting, InputAttempts = item.InputAttempts + 1, ReasonCode = "inserting" };
                Commit(Replace(_state, item));
            }
            InputOutcome outcome;
            try { outcome = await writer(item.Event!, ct); }
            catch (Exception) { outcome = new(EventState.Uncertain, InputFailure.Uncertain); }
            if (outcome.State is not (EventState.Queued or EventState.Verified or EventState.Dispatched or EventState.Uncertain))
                outcome = new(EventState.Uncertain, InputFailure.Uncertain);
            lock (_sync)
            {
                item = item with { State = outcome.State, ReasonCode = outcome.State == EventState.Queued ? "ready" : outcome.State.ToString(),
                    Failure = outcome.State is EventState.Dispatched or EventState.Verified ? null : outcome.Failure };
                var next = Replace(_state, item);
                Commit(ReceiptFor(next, item, outcome));
            }
        }
        finally { _input.Release(); }
    }

    private ReceiptSnapshot ReceiptFor(ReceiptSnapshot state, InboxItem item, InputOutcome outcome)
    {
        var id = $"input:{item.StorageKey}:{item.InputAttempts}:{outcome.State}:{outcome.Failure}";
        if (item.LastReceiptId == id) return state;
        state = Replace(state, item with { LastReceiptId = id });
        return Enqueue(state, item.Reply, id, ReceiptFormatter.Input(item, outcome));
    }

    private ReceiptSnapshot Enqueue(ReceiptSnapshot state, ReplyContext? context, string key, string text, bool query = false)
    {
        if (!Fresh(context)) return state with { Skipped = state.Skipped + 1 };
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_identity.BotId + ":" + key)));
        if (state.Pending.Any(x => x.Id == id)) return state;
        var pending = state.Pending.ToList();
        if (query)
        {
            var previous = pending.FirstOrDefault(x => x.Query);
            if (previous?.Sending == true) return state with { Skipped = state.Skipped + 1 };
            if (previous is not null) { pending.Remove(previous); state = state with { Skipped = state.Skipped + 1 }; }
        }
        else if (pending.Count(x => !x.Query) >= 99)
            return state with { InputPaused = true, PauseCause = state.PauseCause == InputPauseCause.None ? InputPauseCause.OutboxFull : state.PauseCause, Skipped = state.Skipped + 1 };
        pending.Add(new(id, "wic-" + id, context!, text + "\n编号：" + id[..8], Now, Now, Query: query));
        var full = pending.Count(x => !x.Query) >= 99;
        return state with { Pending = pending.ToArray(), InputPaused = state.InputPaused || full,
            PauseCause = full && state.PauseCause == InputPauseCause.None ? InputPauseCause.OutboxFull : state.PauseCause };
    }

    public void SetAutoInput(bool enabled)
    {
        lock (_sync)
        {
            if (_state.AutoInput == enabled) return;
            var next = _state with { AutoInput = enabled };
            var text = !enabled ? "自动输入已暂停。" : next.InputPaused ? "自动输入开关已开启，但异常暂停尚未解除。"
                : "自动输入已恢复；历史积压不会自动补写。";
            Commit(Enqueue(next, next.LastContext, "control:" + Guid.NewGuid(), text));
        }
    }
    public void SetAutoEnter(bool enabled) { lock (_sync) { if (_state.AutoEnter != enabled) Commit(_state with { AutoEnter = enabled }); } }
    public void AcknowledgeUncertain(Action? clearRecovery = null)
    {
        lock (_sync)
        {
            if (!_state.InputPaused || _state.Pending.Count(x => !x.Query) >= 99) return;
            clearRecovery?.Invoke();
            var next = _state with { InputPaused = false, PauseCause = InputPauseCause.None,
                Events = _state.Events.Select(x => x.State == EventState.Uncertain ? x with { State = EventState.Discarded, ReasonCode = "user_checked" } : x).ToArray() };
            Commit(Enqueue(next, next.LastContext, "resume:" + Guid.NewGuid(), next.AutoInput
                ? "自动输入已恢复；历史积压不会自动补写。" : "异常暂停已解除，自动输入开关仍关闭。"));
        }
    }
    public void ReportSwitch(ReplyContext context, string application, bool activated, bool inputReady)
    {
        var text = ReceiptFormatter.Switch(application, activated, inputReady);
        lock (_sync) Commit(Enqueue(_state with { InputPaused = _state.InputPaused || !activated || !inputReady,
            PauseCause = (!activated || !inputReady) && _state.PauseCause == InputPauseCause.None ? InputPauseCause.TargetNotReady : _state.PauseCause }, context,
            "switch:" + context.SourceMessageId, text));
    }

    private ReceiptSnapshot Prune(ReceiptSnapshot state)
    {
        if (state.Pending.All(x => Now - x.CreatedAt < TimeSpan.FromMinutes(5) && Fresh(x.Reply))
            && (state.LastContext is null || Fresh(state.LastContext))
            && state.Events.All(x => x.Reply is null || Fresh(x.Reply))) return state;
        var pending = state.Pending.Where(x => Now - x.CreatedAt < TimeSpan.FromMinutes(5) && Fresh(x.Reply)).ToArray();
        return state with { Pending = pending, Skipped = state.Skipped + state.Pending.Count - pending.Length,
            LastContext = Fresh(state.LastContext) ? state.LastContext : null,
            Events = state.Events.Select(x => x.Reply is not null && !Fresh(x.Reply) ? x with { Reply = null } : x).ToArray() };
    }

    public PendingReceipt? TakeDue()
    {
        lock (_sync)
        {
            var next = Prune(_state);
            if (!next.Connected || next.Pending.Any(x => x.Sending) || next.LastSendAt is not null && Now - next.LastSendAt < TimeSpan.FromSeconds(1))
            { if (!ReferenceEquals(next, _state)) Commit(next); return null; }
            var receipt = next.Pending.Where(x => x.NextAttemptAt <= Now).OrderByDescending(x => x.Query).FirstOrDefault();
            if (receipt is null) { if (!ReferenceEquals(next, _state)) Commit(next); return null; }
            receipt = receipt with { Sending = true, Attempts = receipt.Attempts + 1 };
            Commit(next with { Pending = next.Pending.Select(x => x.Id == receipt.Id ? receipt : x).ToArray(), LastSendAt = Now });
            return receipt;
        }
    }

    public void CompleteSend(string id, ReceiptDelivery delivery)
    {
        // 失效属于整个会话，即使该回执刚被过期清理也必须停止收发。
        if (delivery == ReceiptDelivery.SessionExpired) { MarkSessionExpired(); return; }
        lock (_sync)
        {
            var current = _state.Pending.FirstOrDefault(x => x.Id == id && x.Sending);
            if (current is null) return;
            var pending = _state.Pending.Where(x => x.Id != id).ToList(); var skipped = _state.Skipped;
            if (delivery == ReceiptDelivery.Retryable && current.Attempts < 4 && Fresh(current.Reply) && Now - current.CreatedAt < TimeSpan.FromMinutes(5))
                pending.Add(current with { Sending = false, NextAttemptAt = Now.AddSeconds(new[] { 2, 5, 15 }[current.Attempts - 1]) });
            else if (delivery != ReceiptDelivery.Sent) skipped++;
            Commit(_state with { Pending = pending.ToArray(), Skipped = skipped });
        }
    }

    public async Task WaitForChangeAsync(CancellationToken ct) => await _changed.WaitAsync(TimeSpan.FromSeconds(1), ct);
}
