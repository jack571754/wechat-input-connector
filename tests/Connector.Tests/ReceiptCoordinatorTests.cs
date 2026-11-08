using Connector.Core;
using Xunit;

namespace Connector.Tests;

public sealed class ReceiptCoordinatorTests
{
    private readonly Clock _clock = new();
    private ReceiptSnapshot _saved = new(null, [], []);
    private ReceiptCoordinator Create(ReceiptSnapshot? state = null, Action<ReceiptSnapshot>? save = null)
        => new(state ?? _saved, new("bot", "owner"), save ?? (s => _saved = s), _clock);
    private InboxItem Item(string id = "m1", string text = "秘密正文", string? context = "ctx")
        => new(id, new("bot", id, "owner", text, _clock.GetUtcNow(), _clock.GetUtcNow()), EventState.Queued, "ready",
            context is null ? null : new("bot", "owner", context, id, _clock.GetUtcNow()));
    private static Task<InputOutcome> Verified(TextEvent _, CancellationToken ct) => Task.FromResult(new InputOutcome(EventState.Verified));

    [Fact]
    public async Task InputIsSavedBeforeWriterAndResultWithReceiptIsSavedTogether()
    {
        var c = Create(); c.CommitBatch("cursor", [Item()]);
        await c.ProcessAsync("m1", (_, _) =>
        {
            Assert.Equal(EventState.Inserting, _saved.Events.Single().State);
            return Task.FromResult(new InputOutcome(EventState.Verified));
        }, false, default);
        Assert.Equal(EventState.Verified, _saved.Events.Single().State);
        Assert.Single(_saved.Pending);
        Assert.Contains("已确认写入", _saved.Pending[0].Text);
        Assert.DoesNotContain("秘密正文", _saved.Pending[0].Text);
        Assert.DoesNotContain("ctx", _saved.Pending[0].Text);
    }

    [Fact]
    public async Task DispatchedDoesNotPretendVerifiedOrPauseInput()
    {
        var c = Create(); c.CommitBatch(null, [Item()]);
        await c.ProcessAsync("m1", (_, _) => Task.FromResult(new InputOutcome(EventState.Dispatched)), false, default);
        Assert.Contains("尚未确认", c.Snapshot.Pending.Single().Text);
        Assert.DoesNotContain("已确认写入", c.Snapshot.Pending.Single().Text);
        Assert.False(c.Snapshot.InputPaused);
    }

    [Fact]
    public async Task UncertainDoesNotPauseFollowingInputAndNeverReplays()
    {
        var c = Create(); c.CommitBatch(null, [Item(), Item("m2")]); var calls = 0;
        Task<InputOutcome> Write(TextEvent _, CancellationToken ct) { calls++; return Task.FromResult(new InputOutcome(EventState.Uncertain)); }
        await c.ProcessAsync("m1", Write, false, default);
        await c.ProcessAsync("m2", Write, false, default);
        await c.ProcessAsync("m1", Write, true, default);
        Assert.Equal(2, calls); Assert.False(c.Snapshot.InputPaused);
        Assert.All(c.Snapshot.Events, x => Assert.Equal(1, x.InputAttempts));
        Assert.DoesNotContain("已暂停", c.Snapshot.Pending.First().Text);
    }

    [Fact]
    public async Task RestartConvertsInsertingToUncertainWithoutReplay()
    {
        var c = Create(new(null, [Item() with { State = EventState.Inserting }], [])); var calls = 0;
        await c.ProcessAsync("m1", (_, _) => { calls++; return Verified(null!, default); }, false, default);
        Assert.False(c.Snapshot.InputPaused); Assert.Equal(0, calls);
        Assert.Equal(EventState.Uncertain, c.Snapshot.Events.Single().State);
        c.CommitBatch(null, [Item("new")]);
        await c.ProcessAsync("new", (_, _) => { calls++; return Verified(null!, default); }, false, default);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LegacyUncertainPauseIsClearedWithoutReplayingOldMessage()
    {
        var c = Create(new(null, [Item() with { State = EventState.Uncertain }], [], InputPaused: true));
        Assert.False(c.Snapshot.InputPaused);
        var calls = 0;
        await c.ProcessAsync("m1", (_, _) => { calls++; return Verified(null!, default); }, true, default);
        Assert.Equal(0, calls);
        Assert.Equal(EventState.Uncertain, c.Snapshot.Events.Single().State);
    }

    [Theory]
    [InlineData(InputPauseCause.OutboxFull)]
    [InlineData(InputPauseCause.SessionExpired)]
    [InlineData(InputPauseCause.LegacyRecovery)]
    [InlineData(InputPauseCause.TargetNotReady)]
    public async Task UncertainHistoryDoesNotClearAnExplicitDifferentBlock(InputPauseCause cause)
    {
        var c = Create(new(null, [Item() with { State = EventState.Uncertain }], [], InputPaused: true, PauseCause: cause));
        c.CommitBatch(null, [Item("new")]); var calls = 0;
        await c.ProcessAsync("new", (_, _) => { calls++; return Verified(null!, default); }, false, default);
        Assert.True(c.Snapshot.InputPaused); Assert.Equal(cause, c.Snapshot.PauseCause); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LegacyUncertainMigrationDoesNotEnableUserDisabledAutoInput()
    {
        var c = Create(new(null, [Item() with { State = EventState.Uncertain }], [], AutoInput: false, InputPaused: true));
        c.CommitBatch(null, [Item("new")]); var calls = 0;
        await c.ProcessAsync("new", (_, _) => { calls++; return Verified(null!, default); }, false, default);
        Assert.False(c.Snapshot.InputPaused); Assert.False(c.Snapshot.AutoInput); Assert.Equal(0, calls);
    }

    [Fact]
    public void LegacyUncertainPendingReceiptIsUpdatedWithoutChangingItsDeliveryIdentity()
    {
        var item = Item() with { State = EventState.Uncertain };
        var pending = new PendingReceipt("id", "client", item.Reply!, "输入结果不明，自动输入已暂停；请检查电脑后再处理本条。\n编号：12345678",
            _clock.GetUtcNow(), _clock.GetUtcNow(), Attempts: 1);
        var c = Create(new(null, [item], [pending], InputPaused: true));
        var migrated = c.Snapshot.Pending.Single();
        Assert.DoesNotContain("已暂停", migrated.Text); Assert.Contains("后续新消息继续", migrated.Text);
        Assert.Equal("id", migrated.Id); Assert.Equal("client", migrated.ClientId); Assert.Equal(1, migrated.Attempts);
    }

    [Fact]
    public async Task LegacyOverlappingPauseUsesCurrentCapacityAndStillEnforcesTheLimit()
    {
        var item = Item() with { State = EventState.Uncertain };
        var pending = Enumerable.Range(0, 98).Select(i => new PendingReceipt($"r{i}", $"c{i}", item.Reply!, "状态",
            _clock.GetUtcNow(), _clock.GetUtcNow())).ToArray();
        var c = Create(new(null, [item], pending, InputPaused: true));
        Assert.False(c.Snapshot.InputPaused);
        c.CommitBatch(null, [Item("next"), Item("blocked")]); var calls = 0;
        Task<InputOutcome> Write(TextEvent e, CancellationToken ct) { calls++; return Verified(e, ct); }
        await c.ProcessAsync("next", Write, false, default);
        await c.ProcessAsync("blocked", Write, false, default);
        Assert.Equal(1, calls); Assert.Equal(99, c.Snapshot.Pending.Count);
        Assert.True(c.Snapshot.InputPaused); Assert.Equal(InputPauseCause.OutboxFull, c.Snapshot.PauseCause);
    }

    [Fact]
    public async Task StatusQueryIsNeverInsertedAndHasPriority()
    {
        var c = Create(); c.CommitBatch(null, [Item()]); await c.ProcessAsync("m1", Verified, false, default);
        c.CommitBatch("next", [Item("query", "/状态")]);
        await c.ProcessAsync("query", (_, _) => throw new Exception("查询不得进入 writer"), false, default);
        var receipt = c.TakeDue(); Assert.True(receipt!.Query);
        Assert.Contains("待处理", receipt.Text); Assert.Equal("next", c.Snapshot.Cursor);
    }

    [Fact]
    public async Task MissingContextDoesNotPreventInputButCannotSendReceipt()
    {
        var c = Create(); c.CommitBatch(null, [Item(context: null)]);
        await c.ProcessAsync("m1", Verified, false, default);
        Assert.Empty(c.Snapshot.Pending); Assert.Equal(1, c.Snapshot.Skipped);
        Assert.Equal(EventState.Verified, c.Snapshot.Events.Single().State);
    }

    [Fact]
    public void MissingTranscriptStillRepliesToItsOwnContext()
    {
        var c = Create(); c.CommitBatch(null, [Item() with { Event = null, State = EventState.NeedsTranscript }]);
        Assert.Contains("未收到转写", c.Snapshot.Pending.Single().Text);
        Assert.Equal("m1", c.Snapshot.Pending.Single().Reply.SourceMessageId);
    }

    [Fact]
    public async Task DuplicateBatchAndCompletedMessageNeverDispatchTwice()
    {
        var c = Create(); var calls = 0;
        Task<InputOutcome> Write(TextEvent _, CancellationToken ct) { calls++; return Verified(null!, default); }
        c.CommitBatch("1", [Item()]); await c.ProcessAsync("m1", Write, false, default);
        c.CommitBatch("2", [Item()]); await c.ProcessAsync("m1", Write, false, default);
        Assert.Equal(1, calls); Assert.Single(c.Snapshot.Events); Assert.Single(c.Snapshot.Pending);
    }

    [Fact]
    public async Task ReplyRetryReusesClientIdAndNeverRepeatsInput()
    {
        var c = Create(); c.CommitBatch(null, [Item()]); await c.ProcessAsync("m1", Verified, false, default);
        var first = c.TakeDue()!; c.CompleteSend(first.Id, ReceiptDelivery.Retryable);
        Assert.Null(c.TakeDue()); _clock.Advance(2);
        var second = c.TakeDue()!; Assert.Equal(first.ClientId, second.ClientId); Assert.Equal(2, second.Attempts);
        c.CompleteSend(second.Id, ReceiptDelivery.Retryable); _clock.Advance(5);
        var third = c.TakeDue()!; c.CompleteSend(third.Id, ReceiptDelivery.Retryable); _clock.Advance(15);
        var fourth = c.TakeDue()!; c.CompleteSend(fourth.Id, ReceiptDelivery.Retryable);
        Assert.Empty(c.Snapshot.Pending); Assert.Equal(1, c.Snapshot.Skipped);
        Assert.Equal(EventState.Verified, c.Snapshot.Events.Single().State);
    }

    [Fact]
    public async Task ExpiredReceiptsAndContextAreRemoved()
    {
        var c = Create(); c.CommitBatch(null, [Item()]); await c.ProcessAsync("m1", Verified, false, default);
        _clock.Advance(301); Assert.Null(c.TakeDue()); Assert.Empty(c.Snapshot.Pending); Assert.Null(c.Snapshot.LastContext);
    }

    [Fact]
    public async Task FullOutboxPausesInputButPreservesQuerySlot()
    {
        var c = Create();
        for (var i = 0; i < 99; i++) { var item = Item($"m{i}"); c.CommitBatch(null, [item]); await c.ProcessAsync(item.StorageKey, Verified, false, default); }
        c.CommitBatch(null, [Item("overflow")]); await c.ProcessAsync("overflow", (_, _) => throw new Exception("队列满不可输入"), false, default);
        c.CommitBatch(null, [Item("query1", "/状态"), Item("query2", "/状态")]);
        Assert.Equal(100, c.Snapshot.Pending.Count); Assert.True(c.Snapshot.InputPaused);
        Assert.Equal("query2", c.TakeDue()!.Reply.SourceMessageId);
        c.CommitBatch(null, [Item("query3", "/状态")]);
        Assert.Equal(100, c.Snapshot.Pending.Count); Assert.Single(c.Snapshot.Pending, x => x.Query);
    }

    [Fact]
    public async Task SendRateIsLimitedAcrossSuccessfulReplies()
    {
        var c = Create(); c.CommitBatch(null, [Item(), Item("m2")]);
        await c.ProcessAsync("m1", Verified, false, default); await c.ProcessAsync("m2", Verified, false, default);
        var receipt = c.TakeDue()!; c.CompleteSend(receipt.Id, ReceiptDelivery.Sent);
        Assert.Null(c.TakeDue()); _clock.Advance(1); Assert.NotNull(c.TakeDue());
    }

    [Fact]
    public async Task SaveFailurePreventsInputAndDoesNotAdvanceMemory()
    {
        var reject = false; var c = Create(save: s => { if (reject) throw new IOException(); _saved = s; });
        c.CommitBatch(null, [Item()]); reject = true;
        await Assert.ThrowsAsync<IOException>(() => c.ProcessAsync("m1", (_, _) => throw new Exception("不得执行"), false, default));
        Assert.Equal(EventState.Queued, c.Snapshot.Events.Single().State);
    }

    [Fact]
    public async Task IncomingBatchDuringWriterDoesNotGetOverwrittenByCompletion()
    {
        var c = Create(); c.CommitBatch(null, [Item()]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<InputOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = c.ProcessAsync("m1", (_, _) => { started.SetResult(); return finish.Task; }, false, default);
        await started.Task; c.CommitBatch("new-cursor", [Item("m2"), Item("q", "/状态")]);
        finish.SetResult(new(EventState.Verified)); await task;
        Assert.Equal(3, c.Snapshot.Events.Count); Assert.Equal("new-cursor", c.Snapshot.Cursor);
        Assert.Equal(2, c.Snapshot.Pending.Count);
    }

    [Fact]
    public void PauseNotificationOnlyOnActualChangeAndFreshContext()
    {
        var c = Create(); c.CommitBatch(null, [Item()]);
        c.SetAutoInput(false); c.SetAutoInput(false); c.SetAutoInput(true);
        Assert.Equal(2, c.Snapshot.Pending.Count);
        _clock.Advance(301); c.SetAutoInput(false);
        Assert.Equal(1, c.Snapshot.Skipped);
    }

    [Fact]
    public void SwitchReceiptDoesNotPretendInputIsReadyWhenOnlyWindowActivated()
    {
        var item = Item(); var c = Create(); c.ReportSwitch(item.Reply!, "Antigravity", true, false);
        Assert.Contains("尚未找到输入框", c.Snapshot.Pending.Single().Text);
        Assert.DoesNotContain("已就绪", c.Snapshot.Pending.Single().Text);
    }

    [Fact]
    public void CompletedHistoryDoesNotPermanentlyDisableNewMessagesOrStatusQuery()
    {
        var history = Enumerable.Range(0, 500).Select(i => Item($"old{i}") with { State = EventState.Verified }).ToArray();
        var c = Create(new(null, history, []));
        c.CommitBatch("new", [Item("normal"), Item("query", "/状态")]);
        Assert.Contains(c.Snapshot.Events, x => x.StorageKey == "normal"); Assert.True(c.TakeDue()!.Query);
    }

    [Fact]
    public async Task InvalidHeadDoesNotPermanentlyBlockManualReplayOfNextMessage()
    {
        var c = Create(); c.CommitBatch(null, [Item("bad", new string('字', 10001)), Item("good", "正常文字")]);
        var ids = new List<string>();
        var key = await c.ProcessNextAsync((item, _) => { ids.Add(item.MessageId); return Verified(item, default); }, default);
        Assert.Equal("good", key); Assert.Equal(new[] { "good" }, ids);
        Assert.Equal(EventState.Rejected, c.Snapshot.Events.First(x => x.StorageKey == "bad").State);
        Assert.Equal("正常文字", c.Snapshot.Events.First(x => x.StorageKey == "good").Event!.Text);
    }

    [Fact]
    public async Task SessionExpiryIsNotLostWhenInflightReceiptWasPruned()
    {
        var c = Create(); c.CommitBatch(null, [Item()]); await c.ProcessAsync("m1", Verified, false, default);
        var receipt = c.TakeDue()!; _clock.Advance(301); c.CommitBatch("new", [Item("m2")]);
        Assert.Empty(c.Snapshot.Pending); c.CompleteSend(receipt.Id, ReceiptDelivery.SessionExpired);
        Assert.False(c.Snapshot.Connected); Assert.True(c.Snapshot.InputPaused); Assert.Null(c.Snapshot.LastContext);
    }

    [Fact]
    public void ReceiveSideSessionExpiryAlsoBlocksInputAndClearsReplyContext()
    {
        var c = Create(); c.CommitBatch(null, [Item()]); c.MarkSessionExpired();
        Assert.False(c.Snapshot.Connected); Assert.True(c.Snapshot.InputPaused); Assert.Null(c.Snapshot.LastContext);
    }

    [Fact]
    public void RecoveryBarrierMustBeClearedBeforeResumeIsCommitted()
    {
        var c = Create(new(null, [Item()], [], InputPaused: true));
        Assert.Throws<IOException>(() => c.AcknowledgeUncertain(() => throw new IOException()));
        Assert.True(c.Snapshot.InputPaused);
        var cleared = false; c.AcknowledgeUncertain(() => cleared = true);
        Assert.True(cleared); Assert.False(c.Snapshot.InputPaused);
        Assert.Equal(EventState.Queued, c.Snapshot.Events.Single().State);
    }

    [Fact]
    public void RestartDoesNotExceedRetryLimitForFinalInFlightAttempt()
    {
        var reply = Item().Reply!;
        var pending = new PendingReceipt("r", "client", reply, "状态", _clock.GetUtcNow(), _clock.GetUtcNow(), 4, Sending: true);
        var c = Create(new(null, [], [pending]));
        Assert.Null(c.TakeDue()); Assert.Empty(c.Snapshot.Pending); Assert.Equal(1, c.Snapshot.Skipped);
    }

    [Fact]
    public void IdleReceiptChecksDoNotContinuouslySaveEncryptedState()
    {
        var saves = 0; var c = Create(save: _ => saves++); var baseline = saves;
        for (var i = 0; i < 100; i++) Assert.Null(c.TakeDue());
        Assert.Equal(baseline, saves);
    }

    [Fact]
    public void ForgedIdentityNeverEntersInputOrReceiptQueues()
    {
        var c = Create(); c.CommitBatch(null, [Item() with { Reply = Item().Reply! with { OwnerId = "stranger" } }]);
        Assert.Empty(c.Snapshot.Events); Assert.Empty(c.Snapshot.Pending);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-07T06:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
