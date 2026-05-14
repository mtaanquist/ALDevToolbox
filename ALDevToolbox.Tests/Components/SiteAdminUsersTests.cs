using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/site-admin/users</c>. Pins the cross-org user-search
/// page's three-state contract and the "no match for current query" vs
/// "no users at all" branch from CLAUDE.md §"three states" — both empty
/// states must render distinct copy. Also confirms the
/// <see cref="Authorize"/> attribute keys on the SiteAdmin role rather
/// than Admin (the per-org admin must not reach this page).
/// </summary>
public sealed class SiteAdminUsersTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();
    private readonly TestAuthorizationContext _auth;

    public SiteAdminUsersTests()
    {
        _auth = _ctx.AddTestAuthorization();
        _auth.SetAuthorized("siteadmin@example.com");
        _auth.SetRoles(HttpOrganizationContext.SiteAdminRole);

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
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
    public void Empty_user_set_with_no_query_renders_the_no_users_yet_copy()
    {
        var cut = _ctx.RenderComponent<SiteAdminUsers>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("No users yet",
                "the empty-state copy distinguishes \"empty database\" from "
                + "\"no match for the current search\" — see CLAUDE.md §three states"));
    }

    [Fact]
    public void Empty_user_set_with_a_query_renders_the_no_match_copy()
    {
        var cut = _ctx.RenderComponent<SiteAdminUsers>(p => p
            .Add(c => c.Query, "alice"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No users matched that search.");
            cut.Markup.Should().NotContain("No users yet",
                "the two empty states must not both render — distinct copy is "
                + "what makes the search affordance discoverable");
        });
    }

    [Fact]
    public async Task Populated_user_set_renders_one_row_per_user_with_org_and_site_admin_flag()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "alice@example.com",
                DisplayName = "Alice",
                PasswordHash = "x",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                IsSiteAdmin = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.OtherOrgId,
                Email = "bob@example.com",
                DisplayName = "Bob",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = UserStatus.Active,
                IsSiteAdmin = false,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<SiteAdminUsers>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(2,
                "SearchUsersAsync with no query must still return both rows — the page "
                + "first-loads with the most-recent users to give the operator context");
            cut.Markup.Should().Contain("alice@example.com");
            cut.Markup.Should().Contain("bob@example.com");

            // The "Make SiteAdmin" button is the only primary action; the
            // already-SiteAdmin row shows "Remove SiteAdmin" (non-primary).
            cut.FindAll("button.btn--primary").Should().HaveCount(1,
                "primary action appears only on the non-SiteAdmin row");
        });
    }
}
