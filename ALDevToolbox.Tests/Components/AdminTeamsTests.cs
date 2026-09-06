using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/admin/administration/teams</c>. Pins the three-state
/// contract: an org with no teams gets the empty state that explains what a team
/// is for (not a bare table), and a populated org gets one row per team with its
/// member count. See <c>.design/teams-and-visibility.md</c>.
/// </summary>
public sealed class AdminTeamsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const int AdminUserId = 9300;

    public AdminTeamsTests()
    {
        using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = AdminUserId,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "admin@example.com",
                PasswordHash = "x",
                DisplayName = "Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }
        _db.OrgContext.CurrentUserId = AdminUserId;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<TeamService>();
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
    public void Org_with_no_teams_renders_the_empty_state_and_a_way_out_of_it()
    {
        var cut = _ctx.Render<AdminAdministrationTeams>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".empty-state").Should().ContainSingle(
                "a first-time admin should meet an explanation, not a bare table");
            // The one piece of copy worth pinning: it says what a team is *for*
            // today, and marks the visibility grant as still to come. Present
            // tense here would tell an admin a project is protected when it is not.
            cut.Markup.Should().Contain("Teams group the colleagues who work on the same customer");
            cut.Markup.Should().Contain("Soon you'll be able to");
            cut.FindAll("button.btn--primary").Should().NotBeEmpty(
                "the empty state has to offer the next step, not just describe it");
        });
    }

    [Fact]
    public async Task Teams_render_one_row_each_with_their_member_count()
    {
        await using (var seed = _db.NewContext())
        {
            var team = new Team
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "Nordics",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.Teams.Add(team);
            await seed.SaveChangesAsync();

            seed.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId,
                TeamId = team.Id,
                UserId = AdminUserId,
                IsManager = true,
                CreatedAt = DateTime.UtcNow,
            });
            seed.Teams.Add(new Team
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "Benelux",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminAdministrationTeams>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".empty-state").Should().BeEmpty();
            cut.Markup.Should().Contain("Nordics");
            cut.Markup.Should().Contain("Benelux");
            cut.Markup.Should().Contain("Teams (2)",
                "the section header counter must reflect the rendered rows");
            cut.FindAll("tbody tr").Should().HaveCount(2);
            // A team with no manager says so rather than showing a bare 0, and a
            // managed team names its manager rather than counting them.
            cut.Markup.Should().Contain("No manager yet");
            cut.Markup.Should().Contain("Admin");
        });
    }
}
