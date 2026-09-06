namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One <see cref="Team"/> assigned to one <see cref="OeProject"/> — the join that
/// grants that team's members view and manage rights on the project (everything
/// except deleting it). See <c>.design/teams-and-visibility.md</c>.
///
/// <para>Carries its own <c>OrganizationId</c> rather than reaching through the
/// project, so the standard <c>ScopeToOrganization</c> query filter applies to it
/// directly like every other tenanted row. Denormalised — a project, its teams,
/// and this row always belong to the same org, and the service enforces that on
/// insert.</para>
/// </summary>
public class OeProjectTeam
{
    public int Id { get; set; }

    /// <summary>Owning organisation (same as the project's and the team's). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    public DateTime CreatedAt { get; set; }
}
