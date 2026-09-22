using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using ALDevToolbox.Components.Layout;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The palette's server-rendered half. What matters here is the "Go to" list:
/// it is the one part of the palette that is decided on the server, and a page
/// that leaks into it is a page the sidebar deliberately did not offer.
///
/// <para>The rows live inside a <c>&lt;template&gt;</c> so they are inert until
/// the script uses one, which is why the assertions reach into
/// <see cref="IHtmlTemplateElement.Content"/> rather than querying the document
/// - a template's children are not in the document tree at all.</para>
///
/// <para>Like <c>NavMenuTests</c>, this needs a database: the Upgrades entry
/// follows a per-team flag that deliberately never enters the sign-in claims.</para>
/// </summary>
public sealed class CommandPaletteTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly AmbientOrganizationContext _orgCtx = new();
    private readonly BunitAuthorizationContext _auth;
    private readonly FakeToolAvailability _tools = new();
    private readonly FakeSingleTenantMode _singleTenant = new();

    public CommandPaletteTests()
    {
        _auth = _ctx.AddAuthorization();
        _ctx.Services.AddSingleton<IOrganizationContext>(_orgCtx);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<IToolAvailability>(_tools);
        _ctx.Services.AddSingleton<ISingleTenantMode>(_singleTenant);
        _orgCtx.CurrentOrganizationId = TestDb.DefaultOrgId;
    }

    /// <summary>Every tool on by default; a test adds a key to switch one off site-wide.</summary>
    private sealed class FakeToolAvailability : IToolAvailability
    {
        public HashSet<ToolKey> Disabled { get; } = new();
        public bool IsSiteEnabled(ToolKey key) => !Disabled.Contains(key);
    }

    private sealed class FakeSingleTenantMode : ISingleTenantMode
    {
        public bool Enabled { get; set; }
        public bool IsEnabled => Enabled;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// The destinations rendered for whoever is signed in, read out of the
    /// inert template the script clones from.
    /// </summary>
    private List<string> GotoHrefs()
    {
        var cut = _ctx.Render<CommandPalette>();
        List<string> hrefs = [];
        cut.WaitForAssertion(() =>
        {
            var node = cut.Find("#cmdp-goto");
            var scope = node is IHtmlTemplateElement template ? (IParentNode)template.Content : node;
            hrefs = scope.QuerySelectorAll("a.cmdp-row")
                .Select(a => a.GetAttribute("href") ?? string.Empty)
                .ToList();
            hrefs.Should().NotBeEmpty("the palette always offers somewhere to go");
        });
        return hrefs;
    }

    [Fact]
    public void Nothing_renders_for_an_anonymous_visitor()
    {
        _auth.SetNotAuthorized();

        var cut = _ctx.Render<CommandPalette>();

        cut.Markup.Trim().Should().BeEmpty(
            "there is nowhere for a signed-out visitor to jump to, and the sign-in page "
            + "is the only thing the shell wants them to see");
    }

    [Fact]
    public void A_signed_in_user_gets_the_dialog_and_an_input_that_is_labelled()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<CommandPalette>();

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("#cmdp");
            dialog.GetAttribute("role").Should().Be("dialog");
            dialog.HasAttribute("hidden").Should().BeTrue("it is closed until Ctrl/Cmd+K");

            var input = cut.Find("#cmdp-input");
            input.GetAttribute("role").Should().Be("combobox");
            input.GetAttribute("aria-controls").Should().Be("cmdp-list");
            cut.Find("label[for='cmdp-input']").TextContent.Should().NotBeNullOrWhiteSpace();
            cut.Find("#cmdp-list").GetAttribute("role").Should().Be("listbox");
        });
    }

    /// <summary>
    /// The three things #888 added to the skeleton, each of which is invisible
    /// when it breaks: the live region (a removed one takes every "no search
    /// results" announcement with it and nothing logs), the group wrapper (a
    /// bare heading inside a listbox is not something a listbox may own), and
    /// the one link out to the docs.
    /// </summary>
    [Fact]
    public void The_dialog_can_speak_group_and_explain_itself()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<CommandPalette>();

        cut.WaitForAssertion(() =>
        {
            var live = cut.Find("#cmdp-live");
            live.GetAttribute("aria-live").Should().Be("polite");
            live.ClassName.Should().Contain("u-sr-only");

            var group = cut.Find("template[data-palette-group]");
            var content = group is IHtmlTemplateElement t ? (IParentNode)t.Content : group;
            content.QuerySelector(".cmdp__group-wrap")!.GetAttribute("role").Should().Be("group");
            content.QuerySelector(".cmdp__group")!.GetAttribute("aria-hidden").Should().Be("true");

            var links = cut.FindAll(".cmdp__foot a");
            links.Should().ContainSingle("the foot holds at most one thing that is not a key hint");
            links[0].GetAttribute("href").Should().Be("/docs/search");
        });
    }

    [Fact]
    public void Every_kind_of_result_row_has_a_template_to_clone()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<CommandPalette>();

        cut.WaitForAssertion(() =>
        {
            foreach (var kind in new[] { "solution", "environment", "release", "recipe", "person", "doc", "goto", "default" })
            {
                cut.FindAll($"template[data-palette-row='{kind}']").Should().ContainSingle(
                    $"the script clones a template per kind and falls back to \"default\"; "
                    + $"a missing \"{kind}\" would drop those rows silently");
            }
        });
    }

    [Fact]
    public void A_plain_user_is_offered_no_admin_page()
    {
        _auth.SetAuthorized("user@example.com");

        var hrefs = GotoHrefs();

        hrefs.Should().Contain("/solutions").And.Contain("/templates");
        hrefs.Should().NotContain(h => h.StartsWith("/admin", StringComparison.Ordinal),
            "a User uses the generator; the authoring pages are not theirs");
        hrefs.Should().NotContain(h => h.StartsWith("/site-admin", StringComparison.Ordinal));
        hrefs.Should().NotContain("/upgrades",
            "scheduling a customer's Business Central update is a per-team grant this user has not got");
    }

    [Fact]
    public void An_editor_is_offered_the_authoring_pages_but_not_Administration_or_the_audit_log()
    {
        _auth.SetAuthorized("editor@example.com");
        _auth.SetRoles("Editor");
        _orgCtx.IsSystemOrganization = false;

        var hrefs = GotoHrefs();

        hrefs.Should().Contain("/admin/templates").And.Contain("/admin/modules")
            .And.Contain("/admin/object-explorer");
        hrefs.Should().NotContain("/admin",
            "the Dashboard is an Admin page; an Editor's Admin section starts at Templates");
        hrefs.Should().NotContain("/admin/administration");
        hrefs.Should().NotContain("/admin/audit",
            "Editors do not see the audit log at all");
        hrefs.Should().NotContain(h => h.StartsWith("/site-admin", StringComparison.Ordinal));
    }

    [Fact]
    public void An_org_admin_is_offered_Administration_and_the_per_org_audit_log()
    {
        _auth.SetAuthorized("admin@example.com");
        _auth.SetRoles("Admin");
        _orgCtx.IsSystemOrganization = false;

        var hrefs = GotoHrefs();

        hrefs.Should().Contain("/admin").And.Contain("/admin/administration")
            .And.Contain("/admin/audit").And.Contain("/admin/templates/defaults");
        hrefs.Should().NotContain(h => h.StartsWith("/site-admin", StringComparison.Ordinal),
            "org admins stay inside their own organisation");
    }

    [Fact]
    public void A_site_admin_is_offered_the_cross_org_console_instead_of_the_per_org_audit_log()
    {
        _auth.SetAuthorized("siteadmin@example.com");
        _auth.SetRoles("Admin", HttpOrganizationContext.SiteAdminRole);
        _orgCtx.IsSystemOrganization = false;

        var hrefs = GotoHrefs();

        hrefs.Should().Contain("/site-admin/users").And.Contain("/site-admin/audit")
            .And.Contain("/site-admin/settings").And.Contain("/site-admin/workers");
        hrefs.Should().NotContain("/admin/audit",
            "the cross-org audit log replaces the per-org one, exactly as it does in the sidebar");
    }

    [Fact]
    public void A_tool_switched_off_site_wide_is_not_offered()
    {
        _auth.SetAuthorized("user@example.com");
        _tools.Disabled.Add(ToolKey.Translator);

        var hrefs = GotoHrefs();

        hrefs.Should().NotContain("/translator",
            "the palette must never offer a page the sidebar would not");
        hrefs.Should().Contain("/piper", "the other tools are unaffected");
    }

    [Fact]
    public void The_system_org_loses_the_per_org_template_pages_and_renames_the_users_link()
    {
        _auth.SetAuthorized("bootstrap@example.com");
        _auth.SetRoles("Admin", HttpOrganizationContext.SiteAdminRole);
        _orgCtx.IsSystemOrganization = true;

        var cut = _ctx.Render<CommandPalette>();

        cut.WaitForAssertion(() =>
        {
            var node = cut.Find("#cmdp-goto");
            var scope = node is IHtmlTemplateElement template ? (IParentNode)template.Content : node;
            var rows = scope.QuerySelectorAll("a.cmdp-row").ToList();
            rows.Should().NotBeEmpty();

            rows.Select(r => r.GetAttribute("href")).Should()
                .NotContain("/admin/templates/defaults",
                    "the system org has no per-org template shaping")
                .And.NotContain("/admin/administration");

            var users = rows.Single(r => r.GetAttribute("href") == "/site-admin/users");
            users.TextContent.Should().Contain("Users").And.NotContain("All users",
                "inside the system org there is no per-org users page to disambiguate from");
        });
    }
}
