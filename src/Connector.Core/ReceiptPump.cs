namespace Connector.Core;

public static class ReceiptPump
{
    public static async Task RunAsync(ReceiptCoordinator coordinator,
        Func<PendingReceipt, CancellationToken, Task<ReceiptDelivery>> send, CancellationToken ct, Action? onDelivery = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var receipt = coordinator.TakeDue();
            if (receipt is null) { await coordinator.WaitForChangeAsync(ct); continue; }
            ReceiptDelivery delivery;
            try { delivery = await send(receipt, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { delivery = ReceiptDelivery.Retryable; }
            coordinator.CompleteSend(receipt.Id, delivery);
            onDelivery?.Invoke();
            if (delivery == ReceiptDelivery.SessionExpired) return;
        }
    }
}
