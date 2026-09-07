using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The customer combobox (issue #758). The named user is a BC consultant
/// starting work for a customer who may or may not already be registered here,
/// so the two things pinned are that an existing customer can be found by
/// typing part of their name, and that a customer who is <em>not</em> here is
/// never a dead end - the last row offers to make them one, including when the
/// organisation has no solutions at all.
///
/// <para>Nothing here creates a solution: the create row only records the
/// intent. See <c>.design/customer-naming.md</c>.</para>
/// </summary>
public sealed class SolutionPickerTests : IDisposable
{
    private const int UserId = 9760;

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public SolutionPickerTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("consultant@cronus.example");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "consultant@cronus.example",
            DisplayName = "Connie Consultant",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task SeedSolutionAsync(string name, string? shortName = null, Guid? tenantId = null)
    {
        await using var seed = _db.NewContext();
        seed.OeProjects.Add(new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            ShortName = shortName,
            BcTenantId = tenantId,
            DefaultArtifactCountry = "dk",
            CreatedByUserId = UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync();
    }

    /// <summary>Renders the picker with the two-way bindings a host page gives it.</summary>
    private IRenderedComponent<SolutionPicker> RenderPicker(
        Action<SolutionOption?>? onSelected = null, Action<bool>? onCreate = null)
    {
        var value = string.Empty;
        return _ctx.Render<SolutionPicker>(p => p
            .Add(c => c.Value, value)
            .Add(c => c.ValueChanged, (string v) => value = v)
            .Add(c => c.SelectedChanged, (SolutionOption? s) => onSelected?.Invoke(s))
            .Add(c => c.CreateNewChanged, (bool b) => onCreate?.Invoke(b)));
    }

    [Fact]
    public async Task Typing_part_of_a_customers_name_lists_the_solutions_it_matches()
    {
        await SeedSolutionAsync("CRONUS Denmark", "CRO");
        await SeedSolutionAsync("Jorgensen Mobler");

        var cut = RenderPicker();
        cut.Find("input").Focus();
        cut.Find("input").Input("CRONUS");

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[role='option']");
            rows.Should().HaveCount(2, "the match, plus the offer to create what was typed");
            rows[0].TextContent.Should().Contain("CRONUS Denmark").And.Contain("CRO");
        });
    }

    [Fact]
    public async Task A_customer_who_is_not_here_yet_is_offered_as_a_new_solution()
    {
        await SeedSolutionAsync("CRONUS Denmark");
        var created = false;

        var cut = RenderPicker(onCreate: b => created = b);
        cut.Find("input").Focus();
        cut.Find("input").Input("Jorgensen Mobler");

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[role='option']");
            rows.Should().ContainSingle();
            rows[0].TextContent.Should().Contain("Create \"Jorgensen Mobler\" as a new solution");
        });

        await cut.InvokeAsync(() => cut.Find("[role='option']").Click());
        cut.WaitForAssertion(() => created.Should().BeTrue(
            "choosing the row records the intent; the solution itself is created later"));
    }

    /// <summary>
    /// The first-run state. An organisation with nothing registered must still
    /// be able to start a workspace, and the create row is the whole affordance.
    /// </summary>
    [Fact]
    public void With_no_solutions_at_all_the_create_row_is_the_only_row()
    {
        var cut = RenderPicker();
        cut.Find("input").Focus();
        cut.Find("input").Input("CRONUS A/S");

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[role='option']");
            rows.Should().ContainSingle();
            rows[0].TextContent.Should().Contain("Create \"CRONUS A/S\" as a new solution");
        });
    }

    [Fact]
    public async Task An_exact_name_is_a_match_rather_than_something_to_create_again()
    {
        await SeedSolutionAsync("CRONUS Denmark");

        var cut = RenderPicker();
        cut.Find("input").Focus();
        cut.Find("input").Input("cronus denmark");

        cut.WaitForAssertion(() =>
            cut.FindAll("[role='option']").Should().ContainSingle()
                .Which.TextContent.Should().Contain("CRONUS Denmark"));
        cut.Markup.Should().NotContain("as a new solution");
    }

    [Fact]
    public async Task Arrow_keys_and_enter_pick_a_solution_without_the_mouse()
    {
        await SeedSolutionAsync("CRONUS Denmark", "CRO");
        SolutionOption? picked = null;

        var cut = RenderPicker(onSelected: s => picked = s);
        cut.Find("input").Focus();
        cut.Find("input").Input("CRONUS");
        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Should().HaveCount(2));

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.WaitForAssertion(() =>
            cut.Find("input").GetAttribute("aria-activedescendant").Should().NotBeNullOrEmpty(
                "the highlighted row has to be announced to a screen reader"));
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => picked!.Name.Should().Be("CRONUS Denmark"));
        picked!.ShortName.Should().Be("CRO");
    }

    [Fact]
    public async Task Escape_puts_the_list_away_and_leaves_what_was_typed()
    {
        await SeedSolutionAsync("CRONUS Denmark");

        var cut = RenderPicker();
        cut.Find("input").Focus();
        cut.Find("input").Input("CRONUS");
        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Should().NotBeEmpty());

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Should().BeEmpty());
        cut.Find("input").GetAttribute("value").Should().Be("CRONUS");
    }
}
