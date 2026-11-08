using System.IO;
using System.Net.Http;
using System.Text.Json;
using Connector.Core;
using Connector.WeChat;
using Connector.Windows;

namespace Connector.Probe;

internal sealed class ReceiptSession
{
    private readonly string _directory;
    private readonly ILinkClient _client;
    private ConnectionState _persisted;
    public ReceiptCoordinator Coordinator { get; }
    private readonly Action<string> _report;
    private long _sent;
    private long _received;
    private long _polls;
    private string _phase = "starting";
    private string? _failureType;

    public ReceiptSession(string directory, ConnectionState state, bool autoInput, bool autoEnter,
        HttpClient http, ProtocolMetadata metadata, Action<string> report)
    {
        _directory = directory; _persisted = state; _report = report;
        _client = new(http, metadata, state.Account);
        var initial = state.ToRuntime(autoInput, autoEnter);
        if (File.Exists(Path.Combine(directory, "clipboard-recovery.dpapi")))
            initial = initial with { InputPaused = true, PauseCause = InputPauseCause.LegacyRecovery };
        Coordinator = new(initial, new(state.Account.BotId, state.Account.OwnerId), snapshot =>
        {
            var updated = _persisted.WithRuntime(snapshot);
            ProtectedFile.Save(Path.Combine(_directory, "probe-state.dpapi"), updated);
            _persisted = updated;
        });
        InputRecovery.ArchiveUncertain(Path.Combine(directory, "input-recovery.dpapi"));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (!Coordinator.Snapshot.Connected) throw new SessionExpiredException();
            try { await _client.NotifyLifecycleAsync(true, linked.Token); }
            catch (SessionExpiredException) { throw; }
            catch (ILinkException) { }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
            PublishDiagnostic();
            _phase = "receiving";
            var receive = ReceiveAsync(linked.Token);
            var send = ReceiptPump.RunAsync(Coordinator, SendAsync, linked.Token, PublishDiagnostic);
            await Task.WhenAny(receive, send);
            linked.Cancel();
            try { await Task.WhenAll(receive, send); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !Coordinator.Snapshot.Connected) { }
            if (!Coordinator.Snapshot.Connected) throw new SessionExpiredException();
        }
        catch (SessionExpiredException)
        {
            Coordinator.MarkSessionExpired(); PublishDiagnostic(); throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _failureType = error.GetType().Name; PublishDiagnostic(); throw;
        }
        finally { linked.Cancel(); }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var parser = new MessageParser(); var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            PollBatch batch;
            try
            {
                _phase = "polling";
                batch = await _client.GetUpdatesAsync(Coordinator.Snapshot.Cursor, ct);
                failures = 0; _polls++; _failureType = null;
            }
            catch (SessionExpiredException) { throw; }
            catch (ILinkException error)
            {
                _failureType = error.GetType().Name; _phase = "retrying"; PublishDiagnostic();
                _report("微信连接暂时中断，正在退避重连。");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failures++, 4))), ct);
                continue;
            }
            if (batch.Messages.Count == 0 && (string.IsNullOrEmpty(batch.NextCursor) || batch.NextCursor == Coordinator.Snapshot.Cursor)) { PublishDiagnostic(); continue; }
            _phase = "parsing";
            var items = batch.Messages.Select(x => parser.Parse(x, new(_persisted.Account.BotId, _persisted.Account.OwnerId)))
                .Where(x => !x.Ignore).Select(x => x.Item).ToArray();
            _phase = "committing";
            var fresh = Coordinator.CommitBatch(batch.NextCursor, items);
            _received += fresh.Count;
            foreach (var item in fresh)
            {
                if (item.Event is null) continue;
                await Coordinator.ProcessAsync(item.StorageKey, WriteAsync, false, ct);
                ReportInput(item.StorageKey);
            }
            if (items.Any(x => x.Event?.Text.Trim() == "/状态")) _report("已生成微信状态查询回执。");
            PublishDiagnostic();
        }
    }

    public async Task InsertNextAsync(CancellationToken ct)
    {
        var key = await Coordinator.ProcessNextAsync(WriteAsync, ct);
        if (key is null) { _report("暂无待输入文字。"); return; }
        ReportInput(key); PublishDiagnostic();
    }

    public void ReviewAndResume()
    {
        Coordinator.AcknowledgeUncertain(() => InputRecovery.ClearReviewed(_directory));
        PublishDiagnostic();
    }

    private async Task<InputOutcome> WriteAsync(TextEvent item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await FocusedInputProbe.InsertAsync(item.Text, Path.Combine(_directory, "input-recovery.dpapi"), ct, Coordinator.Snapshot.AutoEnter);
        return new(Enum.TryParse<EventState>(result.State, out var state) ? state : EventState.Uncertain);
    }
    private void ReportInput(string key)
    {
        var item = Coordinator.Snapshot.Events.First(x => x.StorageKey == key);
        _report(ReceiptFormatter.Input(item, new(item.State,
            Coordinator.Snapshot.InputPaused || !Coordinator.Snapshot.AutoInput ? InputFailure.Paused : InputFailure.NoInput)));
    }
    private async Task<ReceiptDelivery> SendAsync(PendingReceipt receipt, CancellationToken ct)
    {
        try { await _client.SendTextAsync(receipt.Reply, receipt.ClientId, receipt.Text, ct); _sent++; return ReceiptDelivery.Sent; }
        catch (SessionExpiredException) { return ReceiptDelivery.SessionExpired; }
        catch (ILinkException) { return ReceiptDelivery.Retryable; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ReceiptDelivery.Retryable; }
    }

    public void PublishDiagnostic()
    {
        var state = Coordinator.Snapshot;
        var json = JsonSerializer.Serialize(new { Status = state.Connected ? "connected" : "session_expired", UpdatedAt = DateTimeOffset.UtcNow,
            Bound = true, FocusMode = "CurrentInput", UniqueMessages = state.Events.Count, ReceivedThisRun = _received,
            Queued = state.Events.Count(x => x.State == EventState.Queued), Verified = state.Events.Count(x => x.State == EventState.Verified),
            Dispatched = state.Events.Count(x => x.State == EventState.Dispatched),
            Uncertain = state.Events.Count(x => x.State == EventState.Uncertain), state.InputPaused,
            PauseCause = state.PauseCause.ToString(), state.AutoInput, state.AutoEnter,
            PendingReceipts = state.Pending.Count, SentReceiptsThisRun = _sent, SkippedReceipts = state.Skipped,
            Phase = _phase, CompletedPolls = _polls, FailureType = _failureType });
        try
        {
            var path = Path.Combine(_directory, "probe-status.json");
            File.WriteAllText(path + ".tmp", json); File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
