namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One person's membership of one <see cref="Team"/>. See
/// <c>.design/teams-and-visibility.md</c>.
///
/// <para>Carries its own <c>OrganizationId</c> rather than reaching through the
/// team, so the standard <c>ScopeToOrganization</c> query filter applies to it
/// directly like every other tenanted row. It is denormalised — a member and
/// their team always belong to the same org, and the service enforces that on
/// insert.</para>
/// </summary>
public class TeamMember
{
    public int Id { get; set; }

    /// <summary>Owning organisation (same as the team's). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// True when this member may manage the team — rename it, and add, remove or
    /// promote members. One boolean rather than a role enum because there is
    /// exactly one elevated capability. A team with <em>no</em> manager is allowed:
    /// org Admins can always manage it, so there is nothing to recover from and no
    /// last-manager guard.
    /// </summary>
    public bool IsManager { get; set; }

    /// <summary>
    /// True when this member may run Business Central platform-update actions —
    /// scheduling and re-scheduling an environment's update — on the projects this
    /// team is assigned to. A separate axis from <see cref="IsManager"/> and from
    /// project manage: holding it grants nothing else, and managing the team or the
    /// project does not grant it. Deliberately a per-membership flag rather than a
    /// fourth <c>UserRole</c>, so it composes with project visibility instead of
    /// duplicating it. Enforced by <c>ProjectAccess</c>; never enters claims. See
    /// <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    public bool ManagesUpdates { get; set; }

    public DateTime CreatedAt { get; set; }
}
