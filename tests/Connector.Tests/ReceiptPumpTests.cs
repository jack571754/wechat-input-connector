using Connector.Core;
using Xunit;

namespace Connector.Tests;

public sealed class ReceiptPumpTests
{
    private static ReceiptCoordinator Coordinator()
    {
        var now = DateTimeOffset.UtcNow;
        var reply = new ReplyContext("bot", "owner", "context", "query", now);
        var item = new InboxItem("query", new("bot", "query", "owner", "/状态", now, now), EventState.Queued, "ready", reply);
        var c = new ReceiptCoordinator(new(null, [], []), new("bot", "owner"), _ => { }); c.CommitBatch(null, [item]); return c;
    }

    [Fact]
    public async Task ActualPumpDeliversStatusQueryWithoutAnyInputWriter()
    {
        var c = Coordinator(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiptPump.RunAsync(c, (receipt, ct) =>
        {
            calls++; Assert.Contains("连接状态", receipt.Text); return Task.FromResult(ReceiptDelivery.Sent);
        }, stop.Token, () => stop.Cancel()));
        Assert.Equal(1, calls); Assert.Empty(c.Snapshot.Pending);
    }

    [Fact]
    public async Task SessionExpiryStopsPumpAndPausesCoordinator()
    {
        var c = Coordinator(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ReceiptPump.RunAsync(c, (_, _) => Task.FromResult(ReceiptDelivery.SessionExpired), stop.Token);
        Assert.False(c.Snapshot.Connected); Assert.True(c.Snapshot.InputPaused); Assert.Empty(c.Snapshot.Pending);
    }

    [Fact]
    public async Task CancellationDuringSendKeepsPersistedReceiptForRecovery()
    {
        var c = Coordinator(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiptPump.RunAsync(c, async (_, ct) =>
        {
            stop.Cancel(); await Task.Delay(1, ct); return ReceiptDelivery.Sent;
        }, stop.Token));
        var pending = Assert.Single(c.Snapshot.Pending); Assert.True(pending.Sending);
        var recovered = new ReceiptCoordinator(c.Snapshot, new("bot", "owner"), _ => { });
        Assert.False(Assert.Single(recovered.Snapshot.Pending).Sending);
    }
}
