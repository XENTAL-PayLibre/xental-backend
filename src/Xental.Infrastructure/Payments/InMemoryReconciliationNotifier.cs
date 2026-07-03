using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Payments;

/// <summary>
/// Process-local <see cref="IReconciliationNotifier"/>. Each SSE subscriber gets a small bounded
/// channel; a publish fans out to every channel registered for the account and drops the oldest
/// event for a slow consumer rather than blocking — so the reconciliation caller is never held up
/// and a stuck stream can't back-pressure the money path. Single-instance; swap for Redis pub/sub
/// to scale horizontally.
/// </summary>
public sealed class InMemoryReconciliationNotifier : IReconciliationNotifier
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<CheckoutStatusEvent>>> _subs = new();

    public void Publish(CheckoutStatusEvent evt)
    {
        if (!_subs.TryGetValue(evt.VirtualAccountId, out var channels))
            return;
        foreach (var ch in channels.Values)
            ch.Writer.TryWrite(evt); // bounded + DropOldest => never blocks
    }

    public async IAsyncEnumerable<CheckoutStatusEvent> SubscribeAsync(
        Guid virtualAccountId, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<CheckoutStatusEvent>(
            new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        var channels = _subs.GetOrAdd(virtualAccountId, _ => new ConcurrentDictionary<Guid, Channel<CheckoutStatusEvent>>());
        channels[id] = channel;
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            channels.TryRemove(id, out _);
            if (channels.IsEmpty)
                _subs.TryRemove(virtualAccountId, out _);
        }
    }
}
