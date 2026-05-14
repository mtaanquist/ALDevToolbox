namespace ALDevToolbox.Services;

/// <summary>
/// Cross-service guards that operate on <see cref="IOrganizationContext"/>.
/// Living as an extension keeps individual services free of the boilerplate
/// (#79).
/// </summary>
internal static class OrganizationContextExtensions
{
    /// <summary>
    /// Throws when the acting principal is not a SiteAdmin. Guards every
    /// mutation in SiteAdmin-only services so an organisation admin who
    /// happens to reach a SiteAdmin endpoint by URL guessing can't do
    /// anything. The endpoint layer is expected to already 404 — this is
    /// belt-and-braces.
    /// </summary>
    public static void RequireSiteAdmin(this IOrganizationContext context)
    {
        if (!context.IsSiteAdmin)
        {
            throw new InvalidOperationException(
                "SiteAdmin context is required for this operation. The endpoint should already be 404-guarded.");
        }
    }
}
