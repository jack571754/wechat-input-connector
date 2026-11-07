using Connector.Core;
using Xunit;

namespace Connector.Tests;

public sealed class ProbeInputFlowTests
{
    private static InboxItem Item(string key, EventState state = EventState.Queued) => new(key,
        new("bot", key, "owner", key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), state, "ready");

    [Fact]
    public async Task SameFreshBatchIsDispatchedInOrderWithoutMistakingLaterItemsForBacklog()
    {
        List<string> attempts = [];
        await ProbeInputFlow.RunFreshBatchAsync([Item("1"), Item("2"), Item("3")], false,
            (item, _) => { attempts.Add(item.StorageKey); return Task.FromResult(EventState.Verified); }, default);
        Assert.Equal(["1", "2", "3"], attempts);
    }

    [Fact]
    public async Task FailedFirstItemKeepsFollowingItemsQueued()
    {
        List<string> attempts = [];
        await ProbeInputFlow.RunFreshBatchAsync([Item("1"), Item("2"), Item("3")], false,
            (item, _) => { attempts.Add(item.StorageKey); return Task.FromResult(EventState.Queued); }, default);
        Assert.Equal(["1"], attempts);
    }

    [Fact]
    public async Task PausedInputDoesNotDispatchFreshBatch()
    {
        var calls = 0;
        await ProbeInputFlow.RunFreshBatchAsync([Item("1"), Item("2")], true,
            (_, _) => { calls++; return Task.FromResult(EventState.Verified); }, default);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(EventState.Verified)]
    [InlineData(EventState.Dispatched)]
    [InlineData(EventState.Uncertain)]
    [InlineData(EventState.Inserting)]
    public void CompletedOrUncertainEventCannotBeStartedFromStaleReference(EventState state)
        => Assert.Null(ProbeInputFlow.TryBegin([Item("1", state)], "1"));

    [Fact]
    public void OnlyQueuedEventTransitionsToInserting()
    {
        var result = ProbeInputFlow.TryBegin([Item("1"), Item("2")], "1");
        Assert.Equal(EventState.Inserting, result![0].State);
        Assert.Equal(EventState.Queued, result[1].State);
    }

    [Fact]
    public async Task RepeatedHotkeyCannotQueueSecondWaiterDuringModifierRelease()
    {
        var gate = new InputAttemptGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var first = gate.TryRunAsync(async _ => { entered.SetResult(); await release.Task; attempts++; }, default);
        await entered.Task;
        Assert.False(await gate.TryRunAsync(_ => { attempts++; return Task.CompletedTask; }, default));
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, attempts);
    }
}
