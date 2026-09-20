using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Customer tab of a solution. The named reader is a support consultant on a call;
/// the named editor is the consultant who owns the customer. It reads first and edits
/// second, which is why there is no input on screen until somebody asks for one.
/// </summary>
public sealed class ProjectDetailCustomerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int OwnerUserId = 9895;

    public ProjectDetailCustomerTests()
    {
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ProjectCustomerInfoService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = OwnerUserId, OrganizationId = TestDb.DefaultOrgId, Email = "owner@example.com", PasswordHash = "x",
            DisplayName = "Owner", Role = UserRole.Editor, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task<int> SeedAsync(Action<OeProject>? shape = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Denmark", CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        shape?.Invoke(project);
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private IRenderedComponent<ProjectDetailCustomer> Render(int id, bool canManage = true)
    {
        var cut = _ctx.Render<ProjectDetailCustomer>(p => p.Add(c => c.Id, id).Add(c => c.CanManage, canManage));
        cut.WaitForAssertion(() => cut.FindAll(".loading-block").Should().BeEmpty());
        return cut;
    }

    [Fact]
    public async Task With_nothing_entered_it_says_so_and_offers_the_one_next_step()
    {
        var id = await SeedAsync();

        var cut = Render(id);

        cut.Markup.Should().Contain("Nothing about this customer yet");
        cut.FindAll("input, select").Should().BeEmpty("it is a read view until somebody asks to edit");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Add customer details");
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_reads_it_and_is_offered_no_edit()
    {
        var id = await SeedAsync(p => { p.HostingType = ProjectHostingType.CustomerHardware; p.BcVersion = "NAV 2018 CU12"; });

        var cut = Render(id, canManage: false);

        cut.Markup.Should().Contain("The customer's own hardware").And.Contain("NAV 2018 CU12");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public async Task The_tenant_id_is_only_asked_for_once_the_hosting_is_on_premises()
    {
        var id = await SeedAsync();
        var cut = Render(id);
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add customer details").Click());

        cut.WaitForAssertion(() => cut.Find("#cust-hosting").Should().NotBeNull());
        cut.FindAll("#cust-tenant").Should().BeEmpty("an online solution's tenant is set on the Business Central tab");

        cut.WaitForAssertion(() => cut.Find("#cust-hosting").Change(ProjectHostingType.OurCloud.ToString()));

        cut.WaitForAssertion(() => cut.FindAll("#cust-tenant").Should().HaveCount(1));
    }

    [Fact]
    public async Task Saving_goes_back_to_the_read_view_with_what_was_typed()
    {
        var id = await SeedAsync();
        var saved = false;
        var cut = _ctx.Render<ProjectDetailCustomer>(p => p
            .Add(c => c.Id, id).Add(c => c.CanManage, true).Add(c => c.OnSaved, () => saved = true));
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add customer details").Click());
        cut.WaitForAssertion(() => cut.Find("#cust-version").Change("BC 25.3"));
        cut.WaitForAssertion(() => cut.Find("#cust-hosting").Change(ProjectHostingType.MicrosoftCloud.ToString()));

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save customer details").Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("input, select").Should().BeEmpty();
            cut.Markup.Should().Contain("BC 25.3").And.Contain("Business Central online").And.Contain("Customer details saved.");
        });
        saved.Should().BeTrue("the page decides which tabs exist from the hosting");
    }

    [Fact]
    public async Task A_refused_save_stays_in_the_editor_with_the_reason_beside_the_field()
    {
        var id = await SeedAsync();
        var cut = Render(id);
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add customer details").Click());
        cut.WaitForAssertion(() => cut.Find("#cust-url").Change("bc.cronus.example"));

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save customer details").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("starting with https://"));
        cut.FindAll("#cust-url").Should().HaveCount(1);
    }
}
