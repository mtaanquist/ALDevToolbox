using System.Linq.Expressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The single source of truth for project authorization, on two axes. See
/// <c>.design/teams-and-visibility.md</c> and <c>.design/artifacts.md</c>.
///
/// <list type="bullet">
///   <item><b>View</b> (<see cref="CanViewAsync"/>) — everyone in the org, unless
///   the project is <see cref="ProjectVisibility.Private"/>, in which case only its
///   owner, a member of an assigned team, an org Admin, or a SiteAdmin.</item>
///   <item><b>Manage</b> (<see cref="CanManageAsync"/>) — adding and removing
///   repositories, editing settings, triggering builds and deliveries: the owner,
///   an org Admin, a SiteAdmin, or a member of a team assigned to the project.</item>
///   <item><b>Delete</b> (<see cref="EnsureCanDeleteAsync"/>) — deliberately
///   stricter than manage: owner, org Admin, SiteAdmin only. A team grant is about
///   doing the work, not about ending it.</item>
/// </list>
///
/// <para>Shared by every project-scoped service (<see cref="ProjectService"/>,
/// <see cref="PipelineService"/>, <see cref="ArtifactService"/>,
/// <see cref="DeliveryService"/>, <see cref="ProjectBuildImporter"/> and friends)
/// so the rules live in one place. List queries use
/// <see cref="VisibleProjectPredicate"/> rather than a per-row check.</para>
/// </summary>
public sealed class ProjectAccess
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    /// <summary>
    /// The caller's identity and team memberships, resolved once per DI scope. In a
    /// Blazor circuit that scope is the circuit, so a membership change made while a
    /// page is open is not picked up until the page reloads. That staleness is
    /// accepted deliberately: it matches the consistency model of the circuit-scoped
    /// <see cref="AppDbContext"/> the same page reads through, and the alternative
    /// (a database round-trip per authorization check) buys freshness nobody asked
    /// for. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    private AccessSnapshot? _snapshot;

    public ProjectAccess(AppDbContext db, IOrganizationContext orgContext)
    {
        _db = db;
        _orgContext = orgContext;
    }

    /// <summary>
    /// Who the caller is, for the purposes of project access: their user id (null
    /// for a background worker running under an ambient org scope), whether they
    /// bypass visibility, and the teams they are on.
    /// </summary>
    /// <param name="UserId">Null when nobody is signed in — grants nothing, never throws.</param>
    /// <param name="TeamIds">The teams this user is on, in the acting org.</param>
    public sealed record AccessSnapshot(int? UserId, bool IsSiteAdmin, bool IsOrgAdmin, IReadOnlySet<int> TeamIds)
    {
        /// <summary>True when the caller sees every project regardless of its visibility.</summary>
        public bool BypassesVisibility => IsSiteAdmin || IsOrgAdmin;
    }

    /// <summary>
    /// The caller's snapshot, loaded on first use and cached for the DI scope.
    /// Public so list-query callers can pass it straight to
    /// <see cref="VisibleProjectPredicate"/> without a second lookup.
    /// </summary>
    public async Task<AccessSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (_snapshot is not null) return _snapshot;

        var userId = _orgContext.CurrentUserId;
        if (userId is null)
        {
            // No signed-in user: a background worker under an ambient org scope, or
            // a pre-auth render. No grants, and nothing to look up.
            return _snapshot = new AccessSnapshot(null, _orgContext.IsSiteAdmin, false, new HashSet<int>());
        }

        var isOrgAdmin = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) == UserRole.Admin;

        var teamIds = await _db.TeamMembers.AsNoTracking()
            .Where(m => m.UserId == userId.Value)
            .Select(m => m.TeamId)
            .ToListAsync(ct).ConfigureAwait(false);

        return _snapshot = new AccessSnapshot(userId, _orgContext.IsSiteAdmin, isOrgAdmin, teamIds.ToHashSet());
    }

    // ── Manage axis ─────────────────────────────────────────────────────

    /// <summary>
    /// True when the current user may manage project <paramref name="projectId"/>,
    /// owned by <paramref name="createdByUserId"/>: they're a SiteAdmin, they own it,
    /// they're an org Admin, or they're on a team assigned to it. Legacy ownerless
    /// projects (null owner) are manageable by Admin/SiteAdmin and assigned teams.
    /// </summary>
    public async Task<bool> CanManageAsync(int projectId, int? createdByUserId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.IsSiteAdmin) return true;
        if (snapshot.UserId is null) return false;
        if (createdByUserId is not null && createdByUserId == snapshot.UserId) return true;
        if (snapshot.IsOrgAdmin) return true;
        return await IsOnAnAssignedTeamAsync(projectId, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not manage project <paramref name="projectId"/>.
    /// </summary>
    public async Task EnsureCanManageAsync(int projectId, int? createdByUserId, CancellationToken ct = default)
    {
        if (!await CanManageAsync(projectId, createdByUserId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException();
        }
    }

    // ── Delete axis (stricter than manage — no team grant) ──────────────

    /// <summary>
    /// True when the current user may <em>delete</em> the project owned by
    /// <paramref name="createdByUserId"/> — the owner, an org Admin, or a SiteAdmin.
    /// Being on an assigned team is deliberately not enough. Takes no project id
    /// because team assignment doesn't enter into it.
    /// </summary>
    public async Task<bool> CanDeleteAsync(int? createdByUserId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.IsSiteAdmin) return true;
        if (snapshot.UserId is null) return false;
        if (createdByUserId is not null && createdByUserId == snapshot.UserId) return true;
        return snapshot.IsOrgAdmin;
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not delete the project owned by <paramref name="createdByUserId"/>.
    /// </summary>
    public async Task EnsureCanDeleteAsync(int? createdByUserId, CancellationToken ct = default)
    {
        if (!await CanDeleteAsync(createdByUserId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "Only the project's owner or an organisation admin can delete it.");
        }
    }

    // ── View axis ───────────────────────────────────────────────────────

    /// <summary>
    /// True when the current user may see project <paramref name="projectId"/> at
    /// all. False only for a <see cref="ProjectVisibility.Private"/> project the
    /// caller neither owns, nor has a team on, nor administers. A project that
    /// doesn't exist in this org reads as visible — the caller's read will come back
    /// empty on its own, and answering "denied" here would confirm it exists
    /// somewhere else.
    /// </summary>
    public async Task<bool> CanViewAsync(int projectId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.BypassesVisibility) return true;

        var row = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Visibility, p.CreatedByUserId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return true;
        if (row.Visibility != ProjectVisibility.Private) return true;

        if (snapshot.UserId is null) return false;
        if (row.CreatedByUserId is not null && row.CreatedByUserId == snapshot.UserId) return true;
        return await IsOnAnAssignedTeamAsync(projectId, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not see project <paramref name="projectId"/>. Callers that render a page
    /// should map this onto their not-found state — a Private project must not be
    /// confirmed to exist beyond the locked name the projects list already shows.
    /// </summary>
    public async Task EnsureCanViewAsync(int projectId, CancellationToken ct = default)
    {
        if (!await CanViewAsync(projectId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "This project is private. Ask its owner or a member of its team for access.");
        }
    }

    /// <summary>
    /// The list-query form of <see cref="CanViewAsync"/>: which projects
    /// <paramref name="snapshot"/> may see. Compose it into a query over
    /// <c>Project</c> instead of checking row by row. Returns "everything" when the
    /// snapshot bypasses visibility, so the caller doesn't have to special-case it.
    /// </summary>
    public static Expression<Func<Project, bool>> VisibleProjectPredicate(AccessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BypassesVisibility) return _ => true;

        var userId = snapshot.UserId;
        var teamIds = snapshot.TeamIds.ToList();
        return p => p.Visibility != ProjectVisibility.Private
                    || (userId != null && p.CreatedByUserId == userId)
                    || p.Teams.Any(t => teamIds.Contains(t.TeamId));
    }

    /// <summary>
    /// The complement of <see cref="VisibleProjectPredicate"/>: the projects that
    /// appear in <c>/projects</c> as a locked, name-only row. Written out longhand
    /// rather than negated at the call site so both halves of the split read the
    /// same way and a reader can check them against each other.
    /// </summary>
    public static Expression<Func<Project, bool>> LockedProjectPredicate(AccessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BypassesVisibility) return _ => false;

        var userId = snapshot.UserId;
        var teamIds = snapshot.TeamIds.ToList();
        return p => p.Visibility == ProjectVisibility.Private
                    && (userId == null || p.CreatedByUserId != userId)
                    && !p.Teams.Any(t => teamIds.Contains(t.TeamId));
    }

    /// <summary>True when the snapshot's user is on at least one team assigned to the project.</summary>
    private async Task<bool> IsOnAnAssignedTeamAsync(int projectId, AccessSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.TeamIds.Count == 0) return false;
        var teamIds = snapshot.TeamIds.ToList();
        return await _db.OeProjectTeams.AsNoTracking()
            .AnyAsync(t => t.ProjectId == projectId && teamIds.Contains(t.TeamId), ct)
            .ConfigureAwait(false);
    }
}
