using System.Threading.Channels;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// In-process hand-off from the import endpoint to <see cref="ReleaseImportWorker"/>
/// for the DVD-scale paths (folder-ZIP upload, URL download). A bounded
/// <see cref="Channel{T}"/> — not an external queue — keeps the "no external
/// services" fence intact while giving the worker a clean back-pressure point.
///
/// <para>
/// The channel itself is in-memory, but every enqueue is mirrored to the
/// <c>oe_import_jobs</c> table via <see cref="PersistedImportJobs"/>. On
/// startup the reconciler re-enqueues every <c>queued</c> / <c>running</c>
/// URL-source row so an interrupted download resumes; staged-zip rows can't
/// be resumed (their temp file lives in container-local <c>/tmp</c> and is
/// gone after a restart), so the reconciler marks those <c>failed</c> with a
/// "restart lost the upload" message instead of stranding them.
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
///
/// <para><see cref="JobRowId"/> points at the durable <c>oe_import_jobs</c>
/// row managed by <see cref="PersistedImportJobs"/>. The worker updates that
/// row's status (running → completed / failed) as it progresses so the admin
/// page reflects current state and the startup reconciler can re-enqueue
/// URL-source rows that survived a restart.</para>
/// </summary>
public sealed record ReleaseImportJob(
    int ReleaseId,
    AmbientOrganizationScope.OrganizationIdentity Identity,
    ReleaseImportSource Source,
    bool StoreSymbolReference = false,
    long JobRowId = 0);

/// <summary>Where the worker gets the bytes from.</summary>
public abstract record ReleaseImportSource
{
    /// <summary>Download the full DVD ZIP from a (already allow-list-validated) URL, then keep only the DVD subset.</summary>
    public sealed record Url(string DownloadUrl) : ReleaseImportSource;

    /// <summary>Open a ZIP already staged to a temp file. <paramref name="IsDvd"/> selects the DVD-subset walk vs the whole-archive walk.</summary>
    public sealed record StagedZip(string TempPath, bool IsDvd) : ReleaseImportSource;

    /// <summary>
    /// A legacy C/AL TXT export staged to a temp file, decoded with the named
    /// codepage ("850" or "1252"). Like <see cref="StagedZip"/> it lives in
    /// container-local <c>/tmp</c> and so is never resumed after a restart.
    /// </summary>
    public sealed record CalTxt(string TempPath, string EncodingName) : ReleaseImportSource;

    /// <summary>
    /// Maintenance re-extraction over already-stored source: repopulate
    /// <c>oe_module_system_references</c> for an existing release without
    /// re-uploading the package (see #291). Carries no payload — the release id
    /// on the job is enough — so unlike the upload sources it survives a restart
    /// only as a failed row to re-trigger, never a lost temp file. The worker
    /// routes it to the AL or C/AL backfill path by inspecting the release.
    /// </summary>
    public sealed record Backfill() : ReleaseImportSource;
}
