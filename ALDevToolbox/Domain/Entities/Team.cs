namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A named group of people inside one organisation. An org Admin creates teams;
/// a team's <see cref="TeamMember.IsManager">managers</see> (and any org Admin /
/// SiteAdmin) manage its membership. Teams are the unit project visibility is
/// granted to — see <c>.design/teams-and-visibility.md</c>.
///
/// <para>Teams are <b>hard-deleted</b>, deliberately. There is no content to
/// recover — a team is a name plus a membership list — and a soft-deleted team
/// would have to be excluded from every future visibility predicate, which is
/// exactly the kind of condition that gets forgotten in one place and silently
/// exposes a private project. Membership rows cascade with the team.</para>
///
/// <para>Slice 1 (this one) creates teams and manages membership; being on a team
/// grants nothing yet. The visibility grants arrive with
/// <c>oe_project_teams</c>.</para>
/// </summary>
public class Team
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// The team's display name — unique per organisation, compared
    /// case-insensitively. The service pre-checks for a friendly inline error;
    /// a functional unique index on <c>(organization_id, lower(name))</c> is the
    /// backstop (see the <c>AddTeams</c> migration).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Membership rows. Cascade-deleted with the team.</summary>
    public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();
}
