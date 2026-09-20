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

    // ── Notes and the three lists ───────────────────────────────────────

    [Fact]
    public async Task Notes_round_trip_and_blank_clears()
    {
        var id = await SeedAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx).SaveNotesAsync(id, new CustomerNotes("  VPN, then CRONUS-BC01 ", "   ", null));

        await using var read = _db.NewContext();
        var notes = await Svc(read).GetNotesAsync(id);

        notes.Should().Be(new CustomerNotes("VPN, then CRONUS-BC01", null, null));
    }

    [Fact]
    public async Task Notes_longer_than_the_column_are_refused_by_field()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveNotesAsync(id, new CustomerNotes(new string('x', 4001), null, null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Keys.Should().Equal("AccessDescription");
    }

    [Fact]
    public async Task Contacts_list_the_customers_own_people_first_and_can_be_changed_and_removed()
    {
        var id = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = Svc(ctx);
            await svc.SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.HostingPartner, "Peter Saddow", "CRONUS Hosting", null, "+45 87 65 43 21"));
            await svc.SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.Customer, "Annette Hill", null, "annette@cronus.example", null));
        }

        await using var read = _db.NewContext();
        var contacts = await Svc(read).ListContactsAsync(id);
        contacts.Select(c => c.Name).Should().Equal("Annette Hill", "Peter Saddow");

        await Svc(read).SaveContactAsync(id, contacts[0].Id, new CustomerContactInput(ProjectContactType.Customer, "Annette Hill-Jensen", null, "annette@cronus.example", null));
        await Svc(read).DeleteContactAsync(id, contacts[1].Id);

        (await Svc(read).ListContactsAsync(id)).Select(c => c.Name).Should().Equal("Annette Hill-Jensen");
    }

    [Theory]
    [InlineData(null, "a@cronus.example", null, "Name")]
    [InlineData("Annette Hill", null, null, "Email")]
    [InlineData("Annette Hill", "not an email", null, "Email")]
    public async Task A_contact_needs_a_name_and_a_way_to_reach_them(string? name, string? email, string? phone, string field)
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.Customer, name, null, email, phone));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task A_colleague_is_listed_once_and_their_role_is_edited_rather_than_stacked()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.SavePersonAsync(id, null, new CustomerPersonInput(OtherUserId, ProjectPersonRole.Consultant, "finance"));

        var again = () => svc.SavePersonAsync(id, null, new CustomerPersonInput(OtherUserId, ProjectPersonRole.Developer, null));
        (await again.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");

        var row = (await svc.ListPeopleAsync(id)).Single();
        await svc.SavePersonAsync(id, row.Id, new CustomerPersonInput(OtherUserId, ProjectPersonRole.Architect, "finance, warehouse"));

        (await svc.ListPeopleAsync(id)).Single().Should().BeEquivalentTo(
            new { UserId = OtherUserId, Role = ProjectPersonRole.Architect, Areas = "finance, warehouse", Email = "other@example.com" });
    }

    [Fact]
    public async Task A_colleague_has_to_be_someone_in_the_organisation()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SavePersonAsync(id, null, new CustomerPersonInput(424242, ProjectPersonRole.Consultant, null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task Integrations_round_trip_with_their_direction_and_need_a_name()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.SaveIntegrationAsync(id, null, new CustomerIntegrationInput(" Webshop orders ", ProjectIntegrationDirection.Inbound));

        (await svc.ListIntegrationsAsync(id)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = "Webshop orders", Direction = ProjectIntegrationDirection.Inbound });
        var blank = () => svc.SaveIntegrationAsync(id, null, new CustomerIntegrationInput("  ", ProjectIntegrationDirection.Both));
        (await blank.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task None_of_the_lists_can_be_changed_by_someone_who_only_sees_the_solution()
    {
        var id = await SeedAsync();
        _db.OrgContext.CurrentUserId = OtherUserId;
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await ((Func<Task>)(() => svc.SaveNotesAsync(id, new CustomerNotes("x", null, null)))).Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.Customer, "A", null, "a@cronus.example", null)))).Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.SavePersonAsync(id, null, new CustomerPersonInput(OwnerUserId, ProjectPersonRole.Consultant, null)))).Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.SaveIntegrationAsync(id, null, new CustomerIntegrationInput("Webshop", ProjectIntegrationDirection.Both)))).Should().ThrowAsync<ProjectAccessDeniedException>();
        (await svc.GetAllAsync(id)).Should().NotBeNull("reading follows the solution's visibility");
    }
}
