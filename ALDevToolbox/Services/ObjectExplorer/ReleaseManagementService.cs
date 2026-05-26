using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Write-path operations on an existing <see cref="Domain.Entities.ObjectExplorer.Release"/>.
/// Soft-delete is the default — reversible, sets <c>deleted_at</c>, hides the
/// Release from the public picker. Hard-delete drops the Release row and
/// cascades through every dependent <c>oe_modules</c> / <c>oe_module_*</c>
/// row via the FK relationships set up in PR 1; it's the path admins use
/// when storage actually needs reclaiming.
///
/// All operations require an Admin user in scope (gated at the endpoint
/// layer) and are scoped to <see cref="IOrganizationContext.CurrentOrganizationId"/>
/// via the query filter on <see cref="AppDbContext"/>.
/// </summary>
public class ReleaseManagementService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<ReleaseManagementService> _logger;

    public ReleaseManagementService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<ReleaseManagementService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; ReleaseManagementService called outside an authenticated request.");

    /// <summary>
    /// Sets <c>deleted_at</c> to now. Reversible via <see cref="RestoreAsync"/>.
    /// Soft-deleted Releases stay queryable for admins but disappear from the
    /// regular browse list.
    /// </summary>
    public async Task SoftDeleteAsync(int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        if (release.DeletedAt is not null)
        {
            // Idempotent: already soft-deleted is a no-op.
            return;
        }
        release.DeletedAt = DateTime.UtcNow;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Soft-deleted Release {ReleaseId} ({Label})", release.Id, release.Label);
    }

    /// <summary>
    /// Clears <c>deleted_at</c>, making the Release visible again. Inverse of
    /// <see cref="SoftDeleteAsync"/>.
    /// </summary>
    public async Task RestoreAsync(int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        if (release.DeletedAt is null) return;
        release.DeletedAt = null;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Restored Release {ReleaseId} ({Label})", release.Id, release.Label);
    }

    /// <summary>
    /// Updates the editable free-text metadata on a Release — the publisher and
    /// (for customer Releases) the customer name. Both collapse to null when blank.
    /// Deliberately does <em>not</em> touch <c>parent_release_id</c>: re-parenting
    /// after ingest would invalidate the already-resolved cross-module references
    /// (the chain walk runs at import time), so the parent stays set on import only.
    /// </summary>
    public async Task UpdateMetadataAsync(
        int releaseId, string? publisher, string? customerName, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        release.Publisher = NullIfBlank(publisher);
        release.CustomerName = NullIfBlank(customerName);
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Updated Release {ReleaseId} ({Label}) metadata: Publisher={Publisher} CustomerName={CustomerName}",
            release.Id, release.Label, release.Publisher, release.CustomerName);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Removes the Release row plus every dependent <c>oe_*</c> row via FK
    /// cascade. Refuses when another Release still points at this one as its
    /// <c>parent_release_id</c> — pre-checked here so the failure message is
    /// precise instead of a generic Postgres FK-violation.
    ///
    /// The supplied <paramref name="confirmLabel"/> must match the Release's
    /// actual label exactly. The UI requires admins to type the label before
    /// the POST, making accidental clicks harmless.
    /// </summary>
    public async Task HardDeleteAsync(int releaseId, string confirmLabel, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw NotFound(releaseId);

        if (!string.Equals(confirmLabel?.Trim(), release.Label, StringComparison.Ordinal))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ConfirmLabel"] = "Type the exact Release label to confirm permanent deletion.",
            });
        }

        var hasChildren = await _db.OeReleases.AsNoTracking()
            .AnyAsync(r => r.ParentReleaseId == releaseId, ct).ConfigureAwait(false);
        if (hasChildren)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Release"] = "Another Release uses this one as its parent. Delete the dependent Release first.",
            });
        }

        _db.OeReleases.Remove(release);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Hard-deleted Release {ReleaseId} ({Label}). All dependent oe_* rows were cascade-removed; ObjectExplorerVacuumScheduler reclaims storage on its next nightly tick.",
            release.Id, release.Label);
    }

    private static PlanValidationException NotFound(int releaseId) =>
        new(new Dictionary<string, string> { ["Release"] = $"Release {releaseId} not found in this organisation." });
}
