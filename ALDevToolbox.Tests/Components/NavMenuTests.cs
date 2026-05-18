using ALDevToolbox.Components.Layout;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the role / system-org branching in the sidebar. The component's own
/// header comment calls out four moving parts (Admin vs SiteAdmin, system
/// vs non-system org) that combine to a 2×2 visibility matrix — this test
/// covers all four corners. No DB; NavMenu only needs an
/// <see cref="IOrganizationContext"/>, the icon catalogue, and an auth
/// context.
/// </summary>
public sealed class NavMenuTests : IDisposable
{
    private readonly TestContext _ctx = new();
    private readonly AmbientOrganizationContext _orgCtx = new();
    private readonly TestAuthorizationContext _auth;
    private readonly FakeMcpAvailability _mcpAvailability = new();

    public NavMenuTests()
    {
        _auth = _ctx.AddTestAuthorization();
        _ctx.Services.AddSingleton<IOrganizationContext>(_orgCtx);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<IMcpAvailability>(_mcpAvailability);
    }

    private sealed class FakeMcpAvailability : IMcpAvailability
    {
        public bool Enabled { get; set; }
        public bool IsEnabled => Enabled;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Anonymous_user_sees_only_the_tools_section()
    {
        _auth.SetNotAuthorized();

        var cut = _ctx.RenderComponent<NavMenu>();

        cut.Markup.Should().Contain("Projects",
            "the Tools section is rendered to every visitor — it's outside the AuthorizeView");
        cut.FindAll("a[href='/admin']").Should().BeEmpty(
            "the Admin section is gated by AuthorizeView Roles=\"Admin\"");
        cut.FindAll("a[href^='/site-admin/']").Should().BeEmpty();
    }

    [Fact]
    public void Mcp_link_hidden_when_availability_says_off()
    {
        _auth.SetAuthorized("user@example.com");
        _mcpAvailability.Enabled = false;

        var cut = _ctx.RenderComponent<NavMenu>();
        cut.FindAll("a[href='/tools/mcp']").Should().BeEmpty();
    }

    [Fact]
    public void Mcp_link_hidden_for_anonymous_even_when_availability_is_on()
    {
        _auth.SetNotAuthorized();
        _mcpAvailability.Enabled = true;

        var cut = _ctx.RenderComponent<NavMenu>();
        cut.FindAll("a[href='/tools/mcp']").Should().BeEmpty(
            "the link sits inside AuthorizeView — anonymous visitors never see it");
    }

    [Fact]
    public void Mcp_link_shows_for_signed_in_user_when_availability_is_on()
    {
        _auth.SetAuthorized("user@example.com");
        _mcpAvailability.Enabled = true;

        var cut = _ctx.RenderComponent<NavMenu>();
        cut.FindAll("a[href='/tools/mcp']").Should().ContainSingle();
    }

    [Fact]
    public void Plain_user_without_admin_role_sees_no_admin_section()
    {
        _auth.SetAuthorized("user@example.com");
        // No role — AuthorizeView Roles="Admin" excludes us.

        var cut = _ctx.RenderComponent<NavMenu>();

        cut.FindAll("a[href='/admin']").Should().BeEmpty();
        cut.FindAll("a[href='/admin/users']").Should().BeEmpty();
    }

    [Fact]
    public void Admin_in_non_system_org_sees_per_org_users_configuration_audit_and_export()
    {
        _auth.SetAuthorized("admin@example.com");
        _auth.SetRoles("Admin");
        _orgCtx.CurrentOrganizationId = 1;
        _orgCtx.IsSystemOrganization = false;

        var cut = _ctx.RenderComponent<NavMenu>();

        cut.FindAll("a[href='/admin']").Should().NotBeEmpty();
        cut.FindAll("a[href='/admin/users']").Should().NotBeEmpty(
            "per-org Users link is only hidden in the system org");
        cut.FindAll("a[href='/admin/configuration/defaults']").Should().NotBeEmpty();
        cut.FindAll("a[href='/admin/audit']").Should().NotBeEmpty(
            "non-SiteAdmin admins get the per-org audit log, not the cross-org one");
        cut.FindAll("a[href='/admin/export']").Should().NotBeEmpty();

        cut.FindAll("a[href^='/site-admin/']").Should().BeEmpty(
            "SiteAdmin-only entries must stay hidden from regular org admins");
        cut.FindAll("a[href='/site-admin/audit']").Should().BeEmpty();
    }

    [Fact]
    public void Site_admin_in_non_system_org_sees_both_per_org_and_site_admin_entries()
    {
        _auth.SetAuthorized("siteadmin@example.com");
        _auth.SetRoles("Admin", HttpOrganizationContext.SiteAdminRole);
        _orgCtx.CurrentOrganizationId = 1;
        _orgCtx.IsSystemOrganization = false;

        var cut = _ctx.RenderComponent<NavMenu>();

        cut.FindAll("a[href='/admin/users']").Should().NotBeEmpty(
            "the per-org Users entry stays visible — SiteAdmin sees both");
        var siteUsers = cut.FindAll("a[href='/site-admin/users']");
        siteUsers.Should().NotBeEmpty();
        siteUsers[0].TextContent.Should().Contain("All users",
            "in a non-system org the cross-org users link is labelled \"All users\" to "
            + "distinguish it from the per-org one");

        cut.FindAll("a[href='/site-admin/audit']").Should().NotBeEmpty();
        cut.FindAll("a[href='/admin/audit']").Should().BeEmpty(
            "SiteAdmin's /site-admin/audit replaces the per-org one — see NavMenu's header comment");

        var siteBackups = cut.FindAll("a[href='/site-admin/backups']");
        siteBackups[0].TextContent.Should().Contain("Database backups");
        cut.FindAll("a[href='/site-admin/settings']").Should().NotBeEmpty();
    }

    [Fact]
    public void Site_admin_in_system_org_hides_per_org_pages_and_relabels_the_site_admin_links()
    {
        _auth.SetAuthorized("bootstrap@example.com");
        _auth.SetRoles("Admin", HttpOrganizationContext.SiteAdminRole);
        _orgCtx.CurrentOrganizationId = 1;
        _orgCtx.IsSystemOrganization = true;

        var cut = _ctx.RenderComponent<NavMenu>();

        cut.FindAll("a[href='/admin/users']").Should().BeEmpty(
            "system org hides the per-org Users page — there are no per-org users to manage");
        cut.FindAll("a[href='/admin/configuration/defaults']").Should().BeEmpty(
            "system org has no per-org configuration");
        cut.FindAll("a[href='/admin/export']").Should().BeEmpty();

        var siteUsers = cut.FindAll("a[href='/site-admin/users']");
        siteUsers.Should().NotBeEmpty();
        siteUsers[0].TextContent.Should().Contain("Users")
            .And.NotContain("All users",
                "inside the system org there is no \"per-org\" users page to disambiguate from");

        var siteBackups = cut.FindAll("a[href='/site-admin/backups']");
        siteBackups[0].TextContent.Should().Contain("Backups")
            .And.NotContain("Database backups");

        cut.FindAll("a[href='/site-admin/settings']").Should().NotBeEmpty();
        cut.FindAll("a[href='/site-admin/audit']").Should().NotBeEmpty();
    }
}
