using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The Upgrades page's read contract (<see cref="UpgradeFleetService"/>, issue #657).
/// Two axes cross here and neither may be dropped: <em>seeing</em> an environment is
/// its project's visibility, and <em>acting</em> on it is the per-team
/// environment-updates flag. The environments table has no visibility rule of its own,
/// so the join through the visible-projects predicate is the guard — the first test
/// below is the one that would catch its removal.
/// </summary>
public sealed class UpgradeFleetServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRefreshQueue _queue = new();

    private const int OwnerUserId = 9500;
    private const int AdminUserId = 9501;
    private const int FlagUserId = 9502;
    private const int PlainTeamUserId = 9503;
    private const int OutsiderUserId = 9504;

    public UpgradeFleetServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.AddRange(
            NewUser(OwnerUserId, "owner@example.com", UserRole.Editor),
            NewUser(AdminUserId, "admin@example.com", UserRole.Admin),
            NewUser(FlagUserId, "upgrade@example.com", UserRole.User),
            NewUser(PlainTeamUserId, "colleague@example.com", UserRole.User),
            NewUser(OutsiderUserId, "outsider@example.com", UserRole.User));
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(int id, string email, UserRole role) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email,
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private void ActAs(int? userId, bool siteAdmin = false)
    {
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = siteAdmin;
    }

    private UpgradeFleetService Svc(AppDbContext ctx) => new(
        ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), _queue,
        NullLogger<UpgradeFleetService>.Instance);

    // ── Seeding ─────────────────────────────────────────────────────────

    private async Task<int> SeedProjectAsync(
        string name, ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            Visibility = visibility,
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>
    /// Puts <paramref name="flagHolders"/> and <paramref name="plainMembers"/> on one
    /// team and assigns it to the project. Only a flag held on a team the project is
    /// assigned to grants the ops axis, which is what these tests are pulling apart.
    /// </summary>
    private async Task SeedTeamAsync(int projectId, int[] flagHolders, int[] plainMembers)
    {
        await using var ctx = _db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = $"Team for {projectId}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();

        foreach (var userId in flagHolders)
        {
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = userId,
                ManagesUpdates = true, CreatedAt = DateTime.UtcNow,
            });
        }
        foreach (var userId in plainMembers)
        {
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = userId,
                CreatedAt = DateTime.UtcNow,
            });
        }
        ctx.OeProjectTeams.Add(new OeProjectTeam
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, TeamId = team.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedEnvironmentAsync(
        int projectId, string name = "Production", string type = "Production",
        DateTime? missingSince = null, string? nextVersion = "27.6")
    {
        await using var ctx = _db.NewContext();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Type = type,
            Status = "Active",
            Version = "27.5.12345.0",
            FetchedAt = DateTime.UtcNow,
            MissingSince = missingSince,
            BcNextUpdateVersion = nextVersion,
            BcNextUpdateDate = nextVersion is null ? null : new DateTime(2026, 9, 14, 22, 0, 0, DateTimeKind.Utc),
            BcNextUpdateLatestDate = nextVersion is null ? null : new DateTime(2026, 10, 12, 22, 0, 0, DateTimeKind.Utc),
            BcNextUpdateFetchedAt = nextVersion is null ? null : DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return env.Id;
    }

    // ── Visibility: the join is the guard ───────────────────────────────

    [Fact]
    public async Task A_private_projects_environments_are_invisible_to_someone_with_no_grant()
    {
        var open = await SeedProjectAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(open);
        var closed = await SeedProjectAsync("CRONUS Norway", ProjectVisibility.Private);
        await SeedTeamAsync(closed, flagHolders: new[] { FlagUserId }, plainMembers: Array.Empty<int>());
        await SeedEnvironmentAsync(closed);

        ActAs(OutsiderUserId);
        await using var ctx = _db.NewContext();
        var rows = await Svc(ctx).ListFleetAsync();

        rows.Should().ContainSingle().Which.ProjectName.Should().Be("CRONUS Denmark",
            "environments inherit their project's visibility - the join through the visible-projects predicate is the only thing enforcing it");
    }

    [Fact]
    public async Task A_private_projects_environments_are_visible_to_its_team()
    {
        var closed = await SeedProjectAsync("CRONUS Norway", ProjectVisibility.Private);
        await SeedTeamAsync(closed, flagHolders: Array.Empty<int>(), plainMembers: new[] { PlainTeamUserId });
        await SeedEnvironmentAsync(closed);

        ActAs(PlainTeamUserId);
        await using var ctx = _db.NewContext();
        var rows = await Svc(ctx).ListFleetAsync();

        rows.Should().ContainSingle();
    }

    // ── The ops axis, per row ───────────────────────────────────────────

    [Fact]
    public async Task A_visible_row_the_person_cannot_act_on_comes_back_locked()
    {
        var project = await SeedProjectAsync("CRONUS Denmark");
        await SeedTeamAsync(project, flagHolders: new[] { FlagUserId }, plainMembers: new[] { PlainTeamUserId });
        await SeedEnvironmentAsync(project);

        ActAs(PlainTeamUserId);
        await using var ctx = _db.NewContext();
        var rows = await Svc(ctx).ListFleetAsync();

        rows.Should().ContainSingle().Which.CanAct.Should().BeFalse(
            "being on the team is enough to see the customer, not to move their update date");
    }

    [Fact]
    public async Task The_flag_holder_can_act_on_the_projects_their_team_is_assigned_to()
    {
        var assigned = await SeedProjectAsync("CRONUS Denmark");
        await SeedTeamAsync(assigned, flagHolders: new[] { FlagUserId }, plainMembers: Array.Empty<int>());
        await SeedEnvironmentAsync(assigned);
        // A Public project with no team at all: nobody but an admin acts on it.
        var unassigned = await SeedProjectAsync("CRONUS Sweden");
        await SeedEnvironmentAsync(unassigned);

        ActAs(FlagUserId);
        await using var ctx = _db.NewContext();
        var rows = await Svc(ctx).ListFleetAsync();

        rows.Should().HaveCount(2);
        rows.Single(r => r.ProjectName == "CRONUS Denmark").CanAct.Should().BeTrue();
        rows.Single(r => r.ProjectName == "CRONUS Sweden").CanAct.Should().BeFalse();
    }

    [Fact]
    public async Task An_org_admin_sees_and_can_act_on_everything()
    {
        var open = await SeedProjectAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(open);
        var closed = await SeedProjectAsync("CRONUS Norway", ProjectVisibility.Private);
        await SeedTeamAsync(closed, flagHolders: Array.Empty<int>(), plainMembers: new[] { PlainTeamUserId });
        await SeedEnvironmentAsync(closed);

        ActAs(AdminUserId);
        await using var ctx = _db.NewContext();
        var rows = await Svc(ctx).ListFleetAsync();

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.CanAct);
    }

    // ── What the list leaves out, and how it is ordered ─────────────────

    [Fact]
    public async Task An_environment_business_central_no_longer_reports_is_left_out()
    {
        var project = await SeedProjectAsync("CRONUS Denmark");
        await SeedTeamAsync(project, flagHolders: new[] { FlagUserId }, plainMembers: Array.Empty<int>());
        await SeedEnvironmentAsync(project, "Production");
        await SeedEnvironmentAsync(project, "OldSandbox", "Sandbox", missingSince: DateTime.UtcNow);

        ActAs(FlagUserId);
        await using var ctx = _db.NewContext();
        var rows = await Svc(ctx).ListFleetAsync();

        rows.Should().ContainSingle().Which.EnvironmentName.Should().Be("Production");
    }

    [Fact]
    public async Task Rows_come_back_by_customer_with_production_first()
    {
        var second = await SeedProjectAsync("CRONUS Norway");
        await SeedEnvironmentAsync(second, "Production");
        var first = await SeedProjectAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(first, "Alpha", "Sandbox");
        await SeedEnvironmentAsync(first, "Production");

        ActAs(AdminUserId);
        await using var ctx = _db.NewContext();
        var rows = await Svc(ctx).ListFleetAsync();

        rows.Select(r => $"{r.ProjectName}/{r.EnvironmentName}").Should().Equal(
            "CRONUS Denmark/Production", "CRONUS Denmark/Alpha", "CRONUS Norway/Production");
    }

    [Fact]
    public async Task A_row_with_nothing_on_offer_reports_no_update_and_nothing_to_push()
    {
        var project = await SeedProjectAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(project, nextVersion: null);

        ActAs(AdminUserId);
        await using var ctx = _db.NewContext();
        var row = (await Svc(ctx).ListFleetAsync()).Single();

        row.HasUpdate.Should().BeFalse();
        row.CanPushDate.Should().BeFalse();
    }

    [Fact]
    public async Task A_date_already_at_the_latest_reports_nothing_to_push()
    {
        var project = await SeedProjectAsync("CRONUS Denmark");
        var envId = await SeedEnvironmentAsync(project);
        await using (var seed = _db.NewContext())
        {
            var env = await seed.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
            env.BcNextUpdateDate = env.BcNextUpdateLatestDate;
            await seed.SaveChangesAsync();
        }

        ActAs(AdminUserId);
        await using var ctx = _db.NewContext();
        var row = (await Svc(ctx).ListFleetAsync()).Single();

        row.HasUpdate.Should().BeTrue();
        row.CanPushDate.Should().BeFalse(
            "the page's preview must refuse exactly what the write refuses");
    }

    // ── Refresh ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_queues_only_the_projects_the_person_may_act_on()
    {
        var mine = await SeedProjectAsync("CRONUS Denmark");
        await SeedTeamAsync(mine, flagHolders: new[] { FlagUserId }, plainMembers: Array.Empty<int>());
        await SeedEnvironmentAsync(mine);
        var theirs = await SeedProjectAsync("CRONUS Sweden");
        await SeedEnvironmentAsync(theirs);

        ActAs(FlagUserId);
        await using var ctx = _db.NewContext();
        var result = await Svc(ctx).RequestRefreshAsync(new[] { mine, theirs });

        result.Queued.Should().Be(1);
        result.Skipped.Should().Be(1, "a row somebody can't touch is passed over, never an error for the whole run");
        _queue.IsInFlight(mine).Should().BeTrue();
        _queue.IsInFlight(theirs).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_coalesces_a_project_that_is_already_being_refreshed()
    {
        var project = await SeedProjectAsync("CRONUS Denmark");
        await SeedEnvironmentAsync(project);

        ActAs(AdminUserId);
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        (await svc.RequestRefreshAsync(new[] { project })).Queued.Should().Be(1);
        var second = await svc.RequestRefreshAsync(new[] { project });

        second.Queued.Should().Be(0);
        second.AlreadyRunning.Should().Be(1,
            "the queue dedupes per project, so a hand-triggered refresh and the nightly sweep coalesce");
    }

    [Fact]
    public async Task Refresh_with_nothing_selected_does_nothing()
    {
        ActAs(AdminUserId);
        await using var ctx = _db.NewContext();
        var result = await Svc(ctx).RequestRefreshAsync(Array.Empty<int>());

        result.Should().Be(new UpgradeRefreshResult(0, 0, 0));
    }
}
