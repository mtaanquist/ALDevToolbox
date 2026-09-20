using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Customer basics on a solution (.design/solution-customer-info.md, slice 1): every
/// field optional, on-premises derived from the hosting type, and the tenant id owned by
/// this form only when the Business Central tab is gone.
/// </summary>
public sealed class ProjectCustomerInfoServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int OwnerUserId = 9890;
    private const int OtherUserId = 9891;

    public ProjectCustomerInfoServiceTests()
    {
        using var seed = _db.NewContext();
        seed.Users.AddRange(NewUser(OwnerUserId, "owner@example.com"), NewUser(OtherUserId, "other@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(int id, string email) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = email,
        Role = UserRole.Editor, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    private ProjectCustomerInfoService Svc(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ProjectCustomerInfoService>.Instance);

    private async Task<int> SeedAsync(Guid? tenantId = null, bool withEnvironment = false)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Denmark", CreatedByUserId = OwnerUserId,
            BcTenantId = tenantId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        if (withEnvironment)
        {
            ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
                FetchedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        return project.Id;
    }

    private static CustomerBasicsInput Input(
        ProjectHostingType? hosting = null, string? version = null, string? url = null,
        string? voice = null, string? tenant = null) =>
        new(hosting, version, null, null, url, voice, tenant);

    [Fact]
    public async Task A_solution_nobody_has_described_reads_as_empty_and_online()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var basics = await Svc(ctx).GetBasicsAsync(id);

        basics!.IsEmpty.Should().BeTrue();
        basics.IsOnPremises.Should().BeFalse("every solution that existed before this did is online, and must keep its tabs");
    }

    [Fact]
    public async Task The_basics_round_trip_trimmed_and_blank_means_not_said()
    {
        var id = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).SaveBasicsAsync(id, new CustomerBasicsInput(
                ProjectHostingType.CustomerHardware, "  NAV 2018 CU12 ", ProjectLicenseType.Purchased,
                ProjectUserExperience.Premium, " https://bc.cronus.example/BC250 ", " 5123456 ", "   "));
        }

        await using var read = _db.NewContext();
        var basics = await Svc(read).GetBasicsAsync(id);

        basics.Should().BeEquivalentTo(new CustomerBasics(
            ProjectHostingType.CustomerHardware, "NAV 2018 CU12", ProjectLicenseType.Purchased,
            ProjectUserExperience.Premium, "https://bc.cronus.example/BC250", "5123456", null));
        basics!.IsOnPremises.Should().BeTrue();
    }

    [Fact]
    public async Task An_on_premises_customer_has_a_microsoft_tenant_too_and_it_is_set_from_here()
    {
        var id = await SeedAsync();
        var tenant = Guid.NewGuid();
        await using (var ctx = _db.NewContext())
            await Svc(ctx).SaveBasicsAsync(id, Input(ProjectHostingType.OurCloud, tenant: tenant.ToString()));

        await using var read = _db.NewContext();
        (await Svc(read).GetBasicsAsync(id))!.TenantId.Should().Be(tenant);
    }

    [Fact]
    public async Task An_online_solutions_tenant_belongs_to_the_business_central_tab_and_is_left_alone()
    {
        var connected = Guid.NewGuid();
        var id = await SeedAsync(tenantId: connected);
        await using (var ctx = _db.NewContext())
            await Svc(ctx).SaveBasicsAsync(id, Input(ProjectHostingType.MicrosoftCloud, tenant: Guid.NewGuid().ToString()));

        await using var read = _db.NewContext();
        (await Svc(read).GetBasicsAsync(id))!.TenantId.Should().Be(connected,
            "changing the tenant of a live connection resets it, which is that tab's job");
    }

    [Fact]
    public async Task A_solution_business_central_online_reports_environments_for_cannot_be_on_premises()
    {
        var id = await SeedAsync(tenantId: Guid.NewGuid(), withEnvironment: true);
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveBasicsAsync(id, Input(ProjectHostingType.HostingPartner));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("HostingType").WhoseValue.Should().Contain("reports an environment");
    }

    [Fact]
    public async Task What_was_typed_wrong_comes_back_keyed_to_its_field()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveBasicsAsync(id, Input(
            ProjectHostingType.CustomerHardware, version: new string('x', 51), url: "bc.cronus.example",
            voice: new string('9', 31), tenant: "not-a-guid"));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Keys.Should().BeEquivalentTo("BcVersion", "ClientUrl", "VoiceAccountNumber", "TenantId");
    }

    [Fact]
    public async Task Someone_who_can_see_the_solution_but_not_manage_it_reads_and_cannot_write()
    {
        var id = await SeedAsync();
        _db.OrgContext.CurrentUserId = OtherUserId;
        await using var ctx = _db.NewContext();

        (await Svc(ctx).GetBasicsAsync(id)).Should().NotBeNull();
        var act = () => Svc(ctx).SaveBasicsAsync(id, Input(version: "BC 25.3"));

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }
}
