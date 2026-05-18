using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Idempotent seeding of the platform-default <see cref="OrganizationFile"/>
/// rows (defined in <see cref="PlatformOrganizationFiles"/>) for a single
/// organisation. Called from <see cref="AccountService"/> when a new org is
/// created via signup, from <c>StartupTasks</c> when the Default org is
/// created on a fresh database, and indirectly from migrations via the
/// equivalent SQL backfill — different entry points, one definition of
/// "what should be there".
/// </summary>
public static class PlatformOrganizationFileSeeder
{
    /// <summary>
    /// Inserts any platform-default file that doesn't already exist for the
    /// organisation. Caller is responsible for calling <c>SaveChangesAsync</c>.
    /// </summary>
    public static async Task EnsureForOrganizationAsync(
        AppDbContext db,
        int organizationId,
        DateTime now,
        CancellationToken ct = default)
    {
        var existingPaths = await db.OrganizationFiles
            .IgnoreQueryFilters()
            .Where(f => f.OrganizationId == organizationId)
            .Select(f => f.Path)
            .ToListAsync(ct);
        var existing = existingPaths.ToHashSet(StringComparer.Ordinal);

        foreach (var def in PlatformOrganizationFiles.All)
        {
            if (existing.Contains(def.Path)) continue;
            db.OrganizationFiles.Add(new OrganizationFile
            {
                OrganizationId = organizationId,
                Path = def.Path,
                Content = def.Content,
                MustacheEnabled = def.MustacheEnabled,
                Scope = def.Scope,
                Ordering = def.Ordering,
                UpdatedAt = now,
            });
        }
    }
}
