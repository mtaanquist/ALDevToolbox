using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Coordinates importing a Microsoft OnPrem artifact into a Release: resolve the
/// version → dedup against the catalogue → create the <c>ingesting</c> release
/// row → enqueue a <see cref="ReleaseImportSource.BcArtifact"/> job. Shared by
/// the per-org auto-import scheduler (<see cref="ReleaseAutoImportScheduler"/>)
/// and the Artifacts tab on the Import Release page, so both name and dedup
/// releases identically.
///
/// <para>
/// The release label is the dedup key: "Business Central {Major}.{Minor} ({CC})".
/// A version whose label already exists (non-deleted) is skipped, which is what
/// makes the daily sweep idempotent and stops the Artifacts tab re-downloading a
/// version already in the catalogue. Always <c>first_party</c> — these are
/// Microsoft releases.
/// </para>
/// </summary>
public sealed class ArtifactReleaseImporter
{
    private readonly BcArtifactService _artifacts;
    private readonly ReleaseImportService _importer;
    private readonly ReleaseImportQueue _queue;
    private readonly PersistedImportJobs _persistedJobs;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<ArtifactReleaseImporter> _logger;

    public ArtifactReleaseImporter(
        BcArtifactService artifacts,
        ReleaseImportService importer,
        ReleaseImportQueue queue,
        PersistedImportJobs persistedJobs,
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<ArtifactReleaseImporter> logger)
    {
        _artifacts = artifacts;
        _importer = importer;
        _queue = queue;
        _persistedJobs = persistedJobs;
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>
    /// Resolves <paramref name="version"/> (newest when null, else the exact /
    /// Major.Minor match) for <paramref name="country"/> and queues its import,
    /// unless the catalogue already has a release with the computed label.
    /// </summary>
    public async Task<ArtifactImportOutcome> ImportAsync(string country, string? version, CancellationToken ct = default)
    {
        var resolved = await _artifacts.ResolveOnPremAsync(country, version, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return new ArtifactImportOutcome(ArtifactImportStatus.NotFound, null, null);
        }

        // Dedup by the computed label — query-filtered to the current org.
        var existingId = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Label == resolved.Label && r.DeletedAt == null)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existingId is not null)
        {
            return new ArtifactImportOutcome(ArtifactImportStatus.AlreadyImported, existingId, resolved.Label);
        }

        var metadata = new ReleaseImportMetadata(
            Label: resolved.Label,
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null);
        var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);

        var identity = CaptureIdentity();
        var source = new ReleaseImportSource.BcArtifact(resolved.ApplicationUrl);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, StoreSymbolReference: false, jobRowId), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued BC artifact import {Label} (release {ReleaseId}, version {Version}, {Url}).",
            resolved.Label, releaseId, resolved.Version, resolved.ApplicationUrl);
        return new ArtifactImportOutcome(ArtifactImportStatus.Queued, releaseId, resolved.Label);
    }

    private AmbientOrganizationScope.OrganizationIdentity CaptureIdentity() => new(
        OrganizationId: _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing an artifact import."),
        UserId: _orgContext.CurrentUserId,
        IsSiteAdmin: _orgContext.IsSiteAdmin,
        IsSystemOrganization: _orgContext.IsSystemOrganization);
}

/// <summary>Outcome of <see cref="ArtifactReleaseImporter.ImportAsync"/>.</summary>
public enum ArtifactImportStatus
{
    /// <summary>A new ingesting release was created and the import was enqueued.</summary>
    Queued,
    /// <summary>A non-deleted release with the computed label already exists; nothing was queued.</summary>
    AlreadyImported,
    /// <summary>No artifact matched the requested version/country.</summary>
    NotFound,
}

/// <summary>Result of an artifact import attempt: the status plus the affected release id / label when known.</summary>
public sealed record ArtifactImportOutcome(ArtifactImportStatus Status, int? ReleaseId, string? Label);
