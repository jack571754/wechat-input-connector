using Connector.Core;
using Xunit;

namespace Connector.Tests;

public sealed class HardeningTests
{
    [Fact]
    public void MixedLargeChineseRecordsCannotPoisonNextCommit()
    {
        var text = new string('字', 10000);
        var records = Enumerable.Range(0, 524).Select(i => Item("done" + i, text: text))
            .Concat(Enumerable.Range(0, 600).Select(i => Item("pending" + i, EventState.Queued, text))).ToArray();
        var c = new ReceiptCoordinator(new(null, records, []), Identity, state =>
            Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state,
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Length < 32 * 1024 * 1024));
        Assert.Equal(600, c.Snapshot.Events.Count(x => x.State == EventState.Queued));
        c.CommitBatch("next", [Item("overflow", EventState.Queued)]);
        Assert.Equal(EventState.Rejected, c.Snapshot.Events.Single(x => x.StorageKey == "overflow").State);
    }

    [Fact]
    public async Task SuccessfulDispatchNeverDisplaysAnUnavailableTargetReason()
    {
        var c = new ReceiptCoordinator(new(null, [Item("a", EventState.Queued)], []), Identity, _ => { });
        await c.ProcessAsync("a", (_, _) => Task.FromResult(new InputOutcome(EventState.Dispatched)), false, default);
        Assert.Null(c.Snapshot.Events.Single().Failure);
        Assert.Equal("", InputHistory.Read(c.Snapshot, Identity).Single().Detail);
    }
    private static readonly BoundIdentity Identity = new("bot", "owner");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static InboxItem Item(string id, EventState state = EventState.Dispatched, string text = "完整正文")
        => new(id, new("bot", id, "owner", text, Now, Now), state, "ready", new("bot", "owner", "context", id, Now));

    [Fact]
    public void HistoryIsReadOnlyAndNeverReturnsForeignMessagesOrCommands()
    {
        var old = Item("a", text: new string('字', 12000));
        var foreign = Item("b") with { Event = Item("b").Event! with { SenderId = "other" } };
        var query = Item("c") with { ReasonCode = "status_query" };
        var state = new ReceiptSnapshot(null, [old, foreign, query], []);
        var rows = InputHistory.Read(state, Identity);
        Assert.Single(rows); Assert.Equal(12000, rows[0].Text.Length);
        Assert.Equal(EventState.Dispatched, state.Events[0].State);
    }

    [Fact]
    public void RetentionPrunesCompletedTextButStillRejectsItsDuplicate()
    {
        var events = Enumerable.Range(0, 1002).Select(i => Item(i.ToString())).ToArray();
        var c = new ReceiptCoordinator(new(null, events, []), Identity, _ => { });
        Assert.InRange(c.Snapshot.Events.Count, 1, 1000);
        Assert.Empty(c.CommitBatch("same", [Item("0", EventState.Queued)]));
        Assert.DoesNotContain(c.Snapshot.Events, x => x.StorageKey == "0");
    }

    [Fact]
    public void RetentionNeverSilentlyDropsUnfinishedMessages()
    {
        var c = new ReceiptCoordinator(new(null, Enumerable.Range(0, 1100).Select(i => Item(i.ToString(), EventState.Queued)).ToArray(), []), Identity, _ => { });
        Assert.Equal(1100, c.Snapshot.Events.Count);
        c.CommitBatch(null, [Item("overflow", EventState.Queued)]);
        Assert.Equal(EventState.Rejected, c.Snapshot.Events.Single(x => x.StorageKey == "overflow").State);
    }

    [Fact]
    public void OutboxCapacityRecoveryDoesNotEnableDisabledAutoInput()
    {
        var pending = new PendingReceipt("r", "client", new("bot", "owner", "context", "m", Now), "回执", Now, Now);
        var c = new ReceiptCoordinator(new(null, [], [pending], AutoInput: false, InputPaused: true, PauseCause: InputPauseCause.OutboxFull), Identity, _ => { });
        Assert.False(c.Snapshot.InputPaused); Assert.False(c.Snapshot.AutoInput);
    }

    [Fact]
    public async Task NativeFailureIsRetainedInHistoryAndReceipt()
    {
        var c = new ReceiptCoordinator(new(null, [Item("a", EventState.Queued)], []), Identity, _ => { });
        await c.ProcessAsync("a", (_, _) => Task.FromResult(new InputOutcome(EventState.Queued, InputFailure.FocusChanged)), false, default);
        Assert.Equal(InputFailure.FocusChanged, c.Snapshot.Events.Single().Failure);
        Assert.Contains("焦点", c.TakeDue()!.Text);
    }
}
