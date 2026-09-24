using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Account;

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
    private readonly BunitContext _ctx = new();
    private readonly BunitAuthorizationContext _auth;

    public SiteAdminAuditTests()
    {
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("siteadmin@example.com");
        _auth.SetRoles(HttpOrganizationContext.SiteAdminRole);

        // SiteAdminService.RequireSiteAdmin() checks the org-context flag in
        // addition to the role claim; the page hits ListOrganizationsAsync in
        // OnInitializedAsync, so the flag must be set or the page throws.
        _db.OrgContext.IsSiteAdmin = true;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<SiteAdminService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_audit_log_renders_the_no_match_copy()
    {
        var cut = _ctx.Render<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("No changes match these filters",
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

        var cut = _ctx.Render<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".audit__entry").Should().HaveCount(2,
                "SearchAuditAsync with no filters returns every entry — cross-org "
                + "visibility is the whole point of /site-admin/audit");

            // The pill variant is what tells a deletion apart from an edit at a
            // glance, so assert the variant rather than just the label.
            var actionPills = cut.FindAll(".audit__entry .status-pill").Select(e => e.GetAttribute("class") ?? "").ToList();
            actionPills.Should().Contain(c => c!.Contains("status-pill--warn"), "an update is neither good nor destructive");
            actionPills.Should().Contain(c => c!.Contains("status-pill--success"), "a creation is");
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

        var cut = _ctx.Render<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".audit__entry").Should().HaveCount(1,
                "EntityType filter narrows the SearchAuditAsync result; the page must "
                + "respect the query string so bookmarked slices keep working");
        });
    }
    [Theory]
    // Clocks go forward at 01:00 UTC on 2026-03-29. The early change is at
    // 01:45 local (+01:00), the late one at 03:15 local (+02:00).
    [InlineData("from=2026-03-29T03:00", "bob@example.com")]
    [InlineData("to=2026-03-29T02:00", "alice@example.com")]
    public async Task Date_filters_are_read_in_the_display_zone_across_the_DST_change(string query, string expectedActor)
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");
        await using (var seed = _db.NewContext())
        {
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.DefaultOrgId,
                EntityType = AuditEntityType.RuntimeTemplate,
                EntityId = 1,
                Action = AuditAction.Updated,
                ChangedBy = "alice@example.com",
                Timestamp = new DateTime(2026, 3, 29, 0, 45, 0, DateTimeKind.Utc),
            });
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.DefaultOrgId,
                EntityType = AuditEntityType.RuntimeTemplate,
                EntityId = 1,
                Action = AuditAction.Updated,
                ChangedBy = "bob@example.com",
                Timestamp = new DateTime(2026, 3, 29, 1, 15, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/site-admin/audit?{query}");

        var cut = _ctx.Render<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
        {
            // Read as UTC, "from 03:00" would keep neither row and "to 02:00"
            // would keep both; only the zone-aware reading splits them.
            var rows = cut.FindAll(".audit__entry");
            rows.Should().HaveCount(1, "the typed time is a wall-clock time in the zone the rows are shown in");
            rows[0].TextContent.Should().Contain(expectedActor);
        });
    }

    [Fact]
    public async Task Rows_show_their_time_in_the_display_zone_with_UTC_on_hover()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");
        await using (var seed = _db.NewContext())
        {
            seed.AuditLog.Add(new AuditLogEntry
            {
                OrganizationId = TestDb.DefaultOrgId,
                EntityType = AuditEntityType.RuntimeTemplate,
                EntityId = 1,
                Action = AuditAction.Updated,
                ChangedBy = "alice@example.com",
                Timestamp = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<SiteAdminAudit>();

        cut.WaitForAssertion(() =>
        {
            var time = cut.Find(".audit__entry time");
            time.TextContent.Should().Be("2026-07-01 12:00");
            time.GetAttribute("title").Should().Be("2026-07-01 10:00:00 UTC");
        });
    }
}
