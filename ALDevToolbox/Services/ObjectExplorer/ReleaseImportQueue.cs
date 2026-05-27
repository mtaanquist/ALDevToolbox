using System.Threading.Channels;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// In-process hand-off from the import endpoint to <see cref="ReleaseImportWorker"/>
/// for the DVD-scale paths (folder-ZIP upload, URL download). A bounded
/// <see cref="Channel{T}"/> — not an external queue — keeps the "no external
/// services" fence intact while giving the worker a clean back-pressure point.
///
/// <para>
/// The queue is deliberately not durable: a process restart drops pending
/// jobs, and the startup reconciler (see <c>StartupTasks</c>) flips any
/// release left in <c>ingesting</c> to <c>failed</c> so nothing is stranded.
/// </para>
/// </summary>
public sealed class ReleaseImportQueue
{
    // Small bound: each job can hold a multi-GB temp file and the worker
    // processes one at a time, so we never want a deep backlog. Writers wait
    // briefly rather than the queue growing unbounded.
    private readonly Channel<ReleaseImportJob> _channel =
        Channel.CreateBounded<ReleaseImportJob>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ChannelReader<ReleaseImportJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(ReleaseImportJob job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);
}

/// <summary>
/// A queued release import. The release row already exists in <c>ingesting</c>
/// state (created synchronously in the request so it shows in the list);
/// the worker materialises the uploads from <see cref="Source"/> and runs the
/// ingest under the captured <see cref="Identity"/>.
/// </summary>
public sealed record ReleaseImportJob(
    int ReleaseId,
    AmbientOrganizationScope.OrganizationIdentity Identity,
    ReleaseImportSource Source);

/// <summary>Where the worker gets the bytes from.</summary>
public abstract record ReleaseImportSource
{
    /// <summary>Download the full DVD ZIP from a (already allow-list-validated) URL, then keep only the DVD subset.</summary>
    public sealed record Url(string DownloadUrl) : ReleaseImportSource;

    /// <summary>Open a ZIP already staged to a temp file. <paramref name="IsDvd"/> selects the DVD-subset walk vs the whole-archive walk.</summary>
    public sealed record StagedZip(string TempPath, bool IsDvd) : ReleaseImportSource;
}
