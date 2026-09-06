namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// How visible a <see cref="OeProject"/> is to people outside the teams assigned to
/// it. One level for the whole project — its pipelines, builds, deliveries, and
/// releases all inherit it. Stored as text (<c>HasConversion&lt;string&gt;()</c>)
/// so the column reads plainly in the database and a new level doesn't renumber
/// the existing rows. See <c>.design/teams-and-visibility.md</c>.
///
/// <para>The invariant tying this to team assignment —
/// <c>Visibility != Public</c> ⇔ at least one team assigned — is enforced
/// atomically by <see cref="Services.ObjectExplorer.ProjectService"/>'s
/// <c>SetAccessAsync</c>, never by two independent writes.</para>
/// </summary>
public enum ProjectVisibility
{
    /// <summary>Everyone in the organisation reads it; the owner and admins manage it. The default.</summary>
    Public,

    /// <summary>Everyone in the organisation reads it; assigned-team members manage it too.</summary>
    ReadOnly,

    /// <summary>
    /// Only assigned-team members, the owner, org Admins, and SiteAdmins can see it
    /// at all. Everyone else sees just its name in the projects list, as a locked
    /// row — enough to know who to ask, not enough to leak activity.
    /// </summary>
    Private,
}
