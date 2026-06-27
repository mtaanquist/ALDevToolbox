using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The single source of truth for project-management authorization: any signed-in
/// user may create and browse projects, but adding/removing repositories, editing
/// settings, triggering builds, and deleting are restricted to the project
/// <em>owner</em> (<see cref="Project.CreatedByUserId"/>) or an org Admin /
/// SiteAdmin. Shared by <see cref="ProjectService"/> (mutate/delete) and
/// <see cref="ProjectBuildImporter"/> (build) so the rule lives in one place. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public sealed class ProjectAccess
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    public ProjectAccess(AppDbContext db, IOrganizationContext orgContext)
    {
        _db = db;
        _orgContext = orgContext;
    }

    /// <summary>
    /// True when the current user may manage <paramref name="createdByUserId"/>'s
    /// project: they own it, or they're an org Admin, or a SiteAdmin. A SiteAdmin
    /// manages every org's projects; an org Admin manages their own org's. Legacy
    /// ownerless projects (null owner) are manageable only by Admin/SiteAdmin.
    /// </summary>
    public async Task<bool> CanManageAsync(int? createdByUserId, CancellationToken ct = default)
    {
        if (_orgContext.IsSiteAdmin) return true;

        var userId = _orgContext.CurrentUserId;
        if (userId is null) return false;
        if (createdByUserId is not null && createdByUserId == userId) return true;

        // Org Admins manage every project in their org regardless of owner. Read
        // the role from the user row (org-scoped by the query filter).
        return await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) == UserRole.Admin;
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not manage the project owned by <paramref name="createdByUserId"/>.
    /// </summary>
    public async Task EnsureCanManageAsync(int? createdByUserId, CancellationToken ct = default)
    {
        if (!await CanManageAsync(createdByUserId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException();
        }
    }
}
