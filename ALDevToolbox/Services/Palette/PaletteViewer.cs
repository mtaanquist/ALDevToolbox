using System.Security.Claims;
using ALDevToolbox.Domain.Navigation;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Tools;

namespace ALDevToolbox.Services.Palette;

/// <summary>
/// Builds the <see cref="NavViewer"/> the "Go to" gates are decided against,
/// from the same facts the sidebar reads. Two callers: the palette skeleton,
/// which renders the Go to list, and <see cref="PaletteContextService"/>, which
/// re-checks a remembered Go to page before offering it under Recent. One
/// builder, so the list a person is shown and the list a recent is checked
/// against cannot drift.
/// </summary>
public static class PaletteViewer
{
    /// <param name="canUseEnvironmentOps">
    /// The one fact the claims cannot answer (a per-team grant). Each caller
    /// asks <c>ProjectAccess</c> for it in the way its own lifetime allows.
    /// </param>
    public static NavViewer For(
        ClaimsPrincipal user,
        IOrganizationContext orgContext,
        ISingleTenantMode singleTenant,
        IToolAvailability tools,
        bool canUseEnvironmentOps)
    {
        ArgumentNullException.ThrowIfNull(user);
        var orgDisabled = EndpointHelpers.ReadDisabledTools(user);
        var visibleTools = ToolCatalog.All
            .Select(t => t.Key)
            .Where(k => tools.IsSiteEnabled(k) && !orgDisabled.Contains(k))
            .ToHashSet();

        return new NavViewer(
            IsAuthenticated: user.Identity?.IsAuthenticated == true,
            IsAdmin: user.IsInRole("Admin"),
            IsEditor: user.IsInRole("Editor"),
            IsSiteAdmin: user.IsInRole(HttpOrganizationContext.SiteAdminRole),
            IsSystemOrganization: orgContext.IsSystemOrganization,
            SingleTenantMode: singleTenant.IsEnabled,
            CanUseEnvironmentOps: canUseEnvironmentOps,
            VisibleTools: visibleTools);
    }
}
