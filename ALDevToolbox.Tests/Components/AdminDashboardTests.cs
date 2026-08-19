using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke tests for <c>/admin</c> on the dashboard archetype.
///
/// Two things here are only visible in the markup, which is why they are
/// pinned. First, the design system forbids status pills in favour of the edge
/// keyline, and an attention row is exactly the place a pill would creep back
/// in. Second, the cue row is six columns wide by construction — a seventh cue
/// wraps to a lonely second row, so the trim is load-bearing rather than
/// cosmetic and no gating combination may push past it.
/// </summary>
public sealed class AdminDashboardTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminDashboardTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@cronus.example");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<DashboardService>();
        _ctx.Services.AddScoped<AuditService>();
        _ctx.Services.AddScoped(sp => _db.NewOrganizationConfigService(sp.GetRequiredService<AppDbContext>()));
        _ctx.Services.AddSingleton<ISingleTenantMode>(new SingleTenantModeState(false));
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void A_quiet_organisation_says_nothing_is_waiting_rather_than_showing_a_bare_card()
    {
        _db.OrgContext.IsSystemOrganization = false;
        SeedSomeContent();

        var cut = _ctx.RenderComponent<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".activity--edge .activity__row").Should().BeEmpty();
            cut.Markup.Should().Contain("Nothing is waiting on you");
        });
    }

    [Fact]
    public void A_waiting_signup_turns_its_cue_red_and_adds_a_row_with_a_keyline_not_a_pill()
    {
        _db.OrgContext.IsSystemOrganization = false;
        SeedSomeContent();
        Seed(seed => seed.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "newcomer@cronus.example",
            RequestedAt = DateTime.UtcNow.AddDays(-3),
            Decision = SignupDecision.Pending,
        }));

        var cut = _ctx.RenderComponent<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Attention is the fill, not a badge — that is the whole point of
            // the cue component, and a badge would be the easy thing to add.
            cut.FindAll(".cue--attention").Should().NotBeEmpty();

            var rows = cut.FindAll(".activity--edge .activity__row");
            rows.Should().HaveCount(1);
            rows[0].ClassList.Should().Contain("is-queued");
            rows[0].QuerySelector(".status-pill").Should().BeNull(
                "the design system carries row state on the 4px edge keyline; a pill "
                + "in a row is the thing RowStateIcon exists to prevent");
            cut.Markup.Should().Contain("newcomer@cronus.example");
        });
    }

    [Fact]
    public void The_cue_row_never_exceeds_the_six_columns_it_is_drawn_for()
    {
        _db.OrgContext.IsSystemOrganization = false;
        SeedSomeContent();

        var cut = _ctx.RenderComponent<AdminDashboard>();

        cut.WaitForAssertion(() => cut.FindAll(".cue").Should().HaveCount(6));
    }

    [Fact]
    public void The_system_organisation_drops_the_per_org_cues_and_still_fills_the_row()
    {
        // In multi-tenant hosting the system org curates content and has no user
        // list of its own, so the two user-shaped cues have nothing to point at.
        _db.OrgContext.IsSystemOrganization = true;
        SeedSomeContent();

        var cut = _ctx.RenderComponent<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            var cues = cut.FindAll(".cue");
            cues.Should().HaveCount(6);
            // Asserted on the cues themselves rather than on the page's text:
            // the attention card's empty-state copy names the same queues in
            // prose, so a substring check over the whole markup passes for the
            // wrong reason.
            cues.Select(c => c.GetAttribute("href"))
                .Should().NotContain("/admin/administration/users");
        });
    }

    [Fact]
    public void A_brand_new_organisation_gets_one_thing_to_do_instead_of_six_zeroes()
    {
        _db.OrgContext.IsSystemOrganization = false;

        var cut = _ctx.RenderComponent<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Six cues reading 0 over two "nothing" panels is accurate and
            // useless. Until the org owns something there is exactly one move.
            cut.FindAll(".cue").Should().BeEmpty();
            cut.FindAll(".dash-cols").Should().BeEmpty();
            cut.Markup.Should().Contain("Your organisation is empty");
            var action = cut.FindAll(".empty-state__action a").Single();
            action.GetAttribute("href").Should().Be("/admin/templates");
            action.ClassList.Should().Contain("btn--primary");
            action.TextContent.Trim().Should().Be("Import starter content");
            // And nothing competes with it: no "Invite user" into an
            // organisation that has nothing to invite anyone to, and no second
            // copy of the same button in the header.
            cut.FindAll(".page-head__actions").Should().BeEmpty();
        });
    }

    [Fact]
    public void The_system_organisation_is_told_to_author_rather_than_import()
    {
        // It is the source every other organisation copies from, so there is
        // nothing for it to import. Single-tenant hosting runs on this org too.
        _db.OrgContext.IsSystemOrganization = true;

        var cut = _ctx.RenderComponent<AdminDashboard>();

        cut.WaitForAssertion(() =>
            cut.FindAll(".empty-state__action a").Single()
                .TextContent.Trim().Should().Be("Add a template"));
    }

    [Fact]
    public void One_person_waiting_is_enough_to_leave_the_first_run_state()
    {
        _db.OrgContext.IsSystemOrganization = false;
        Seed(seed => seed.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "newcomer@cronus.example",
            RequestedAt = DateTime.UtcNow.AddDays(-1),
            Decision = SignupDecision.Pending,
        }));

        var cut = _ctx.RenderComponent<AdminDashboard>();

        // An empty org that someone is waiting to join has something to say,
        // even though it owns no content.
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain("Your organisation is empty");
            cut.FindAll(".activity--edge .activity__row").Should().HaveCount(1);
        });
    }

    /// <summary>Just enough that the org is past its first run.</summary>
    private void SeedSomeContent() => Seed(seed =>
    {
        seed.RuntimeTemplates.Add(TemplateBuilder.Default("one"));
        seed.Recipes.Add(RecipeBuilder.Default("One recipe"));
    });

    private void Seed(Action<AppDbContext> arrange)
    {
        using var seed = _db.NewContext();
        arrange(seed);
        seed.SaveChanges();
    }
}
