namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Thrown when the acting user isn't allowed to manage a project — i.e. they're
/// neither its owner (<c>Project.CreatedByUserId</c>) nor an org Admin / SiteAdmin.
/// Gates the mutate/build/delete paths in the service layer (the source of truth);
/// the UI hides those affordances too, so this is defense-in-depth rather than the
/// primary guard. See <c>.design/artifacts.md</c> ("Roles &amp; ownership").
/// </summary>
public sealed class ProjectAccessDeniedException : Exception
{
    public ProjectAccessDeniedException(string message) : base(message)
    {
    }

    public ProjectAccessDeniedException()
        : base("Only the project owner or an organisation admin can do this.")
    {
    }
}
