using System.Threading.Channels;

namespace ALDevToolbox.Services;

/// <summary>
/// Tiny in-process wakeup channel for <see cref="SymbolReindexer"/>. The
/// reindexer normally polls every five minutes; admins who hit the
/// "Reindex now" button publish to this queue and the worker wakes up
/// immediately, ticks once, then resumes polling.
///
/// We deliberately don't store work items here — the BaseAppVersion row's
/// <c>SymbolsIndexedAt</c> column is the source of truth for "what needs
/// indexing". The channel value is just a kick. CLAUDE.md keeps the
/// architectural fence on persistent queues; this pattern stays on the
/// right side of it (singleton, in-memory, no durability promised).
///
/// Singleton lifetime so the writer (mutation-time service method) and
/// reader (hosted service) share one instance.
/// </summary>
public sealed class SymbolReindexQueue
{
    private readonly Channel<int> _channel = Channel.CreateBounded<int>(
        new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Publishes a wakeup. Multiple concurrent publishes collapse to one
    /// (the channel is bounded at capacity 1 with <c>DropWrite</c>) so a
    /// burst of "Reindex now" clicks doesn't queue up redundant ticks —
    /// the worker already loops through all pending versions on every wake.
    /// </summary>
    public void Signal()
    {
        // TryWrite returns false when the channel is full; that's fine — the
        // existing pending wakeup already covers our intent.
        _channel.Writer.TryWrite(0);
    }

    /// <summary>
    /// Awaits a wakeup, returning once one is published or
    /// <paramref name="ct"/> fires. Returns <c>true</c> when a real wakeup
    /// arrived, <c>false</c> on cancellation.
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        try
        {
            await _channel.Reader.ReadAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
