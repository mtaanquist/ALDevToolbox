using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for the cross-org audit log. The page is a list + filter
/// form; the filter selects are bound to query-string parameters so a
/// SiteAdmin can deep-link a specific entity-type slice. The "no audit
/// entries match" copy is the empty state — same three-state pattern as
/// every other list page.
/// </summary>
public sealed class SiteAdminAuditTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();
    private readonly TestAuthorizationContext _auth;

    public SiteAdminAuditTests()
    {
        _auth = _ctx.AddTestAuthorization();
        _auth.SetAuthorized("siteadmin@example.com");
        _auth.SetRoles(HttpOrganizationContext.SiteAdminRole);

        // SiteAdminService.RequireSiteAdmin() checks the org-context flag in
        // addition to the role claim; the page hits ListOrganizationsAsync in
        // OnInitializedAsync, so the flag must be set or the page throws.
        _db.OrgContext.IsSiteAdmin = true;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<SiteAdminService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_audit_log_renders_the_no_match_copy()
    {
        var cut = _ctx.RenderComponent<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("No audit entries match the current filters.",
                "empty-state copy must read naturally whether or not filters are applied — "
                + "the message holds in both cases"));
    }

    [Fact]
    public async Task Populated_audit_log_renders_one_row_per_entry_with_action_pills()
    {
        await using (var seed = _db.NewContext())
        {
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.DefaultOrgId,
                EntityType = AuditEntityType.RuntimeTemplate,
                EntityId = 1,
                Action = AuditAction.Updated,
                ChangedBy = "alice@example.com",
                Timestamp = new DateTime(2026, 5, 14, 10, 0, 0, DateTimeKind.Utc),
                SnapshotJson = "{}",
            });
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.OtherOrgId,
                EntityType = AuditEntityType.Module,
                EntityId = 2,
                Action = AuditAction.Created,
                ChangedBy = "bob@example.com",
                Timestamp = new DateTime(2026, 5, 14, 11, 0, 0, DateTimeKind.Utc),
                SnapshotJson = "{\"key\":\"value\"}",
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("table.audit-table tbody tr").Should().HaveCount(2,
                "SearchAuditAsync with no filters returns every entry — cross-org "
                + "visibility is the whole point of /site-admin/audit");

            var actionPills = cut.FindAll("span.audit-action").Select(e => e.GetAttribute("class") ?? "").ToList();
            actionPills.Should().Contain(c => c!.Contains("audit-action--updated"));
            actionPills.Should().Contain(c => c!.Contains("audit-action--created"));
        });
    }

    [Fact]
    public async Task Entity_type_filter_passed_via_query_string_narrows_the_visible_rows()
    {
        await using (var seed = _db.NewContext())
        {
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.DefaultOrgId,
                EntityType = AuditEntityType.RuntimeTemplate,
                EntityId = 1,
                Action = AuditAction.Updated,
                ChangedBy = "alice@example.com",
                Timestamp = new DateTime(2026, 5, 14, 10, 0, 0, DateTimeKind.Utc),
            });
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.DefaultOrgId,
                EntityType = AuditEntityType.Module,
                EntityId = 2,
                Action = AuditAction.Created,
                ChangedBy = "bob@example.com",
                Timestamp = new DateTime(2026, 5, 14, 11, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        // SupplyParameterFromQuery binds from NavigationManager.Uri — navigate
        // before rendering so the EntityType parameter is populated.
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/site-admin/audit?entityType={AuditEntityType.Module}");

        var cut = _ctx.RenderComponent<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("table.audit-table tbody tr").Should().HaveCount(1,
                "EntityType filter narrows the SearchAuditAsync result; the page must "
                + "respect the query string so bookmarked slices keep working");
        });
    }
}
