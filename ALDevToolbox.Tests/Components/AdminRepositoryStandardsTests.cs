using Microsoft.AspNetCore.DataProtection;
using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The repository-standards editor (issue #628). A screenshot is not possible
/// here, so these renders are the evidence for the three states, for the empty
/// state naming a file the admin would recognise, and for the page keeping the
/// "one primary action" rule (Save standards, as on the other admin forms).
/// </summary>
public sealed class AdminRepositoryStandardsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminRepositoryStandardsTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@cronus.example");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<GitHubRepositoryStandardsService>();
        // The Repositories tab renders the row that links here, so its own
        // services have to resolve too. Nothing reaches GitHub in these tests.
        _db.AddGitHubServices(_ctx.Services);
        _ctx.Services.AddDataProtection();
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
    public void With_nothing_set_up_the_empty_state_names_a_file_and_offers_the_way_to_add_one()
    {
        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Trim().Should().Be("No files yet");
            cut.Markup.Should().Contain(".github/workflows/build.yml");
            cut.Markup.Should().Contain("CODEOWNERS");
            cut.Find(".empty-state__action").TextContent.Should().Contain("Add a file");
        });
    }

    [Fact]
    public async Task Saved_standards_render_as_a_row_each_with_the_rules_ticked()
    {
        await SeedAsync(
            new GitHubRepositoryRuleset
            {
                RequirePullRequest = true,
                RequiredApprovals = 2,
                BlockForcePushes = true,
                RequiredStatusChecks = { "build" },
            },
            [
                new GitHubStandardFileInput(null, ".github/workflows/build.yml", "name: build"),
                new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al-team"),
            ]);

        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".code-block").Should().HaveCount(2);
            cut.Markup.Should().Contain("Files (2)");
            cut.FindAll(".empty-state").Should().BeEmpty();
            cut.Find("#std-approvals").GetAttribute("value").Should().Be("2");
            cut.Find("#std-checks").GetAttribute("value").Should().Be("build");
        });
    }

    [Fact]
    public void Save_is_the_pages_one_primary_action_and_is_disabled_until_something_changes()
    {
        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button.btn--primary").Should().ContainSingle(
                "an admin form's Save is its primary action, and there is only one");
            cut.Find(".form-actions button").HasAttribute("disabled").Should().BeTrue(
                "nothing has changed yet, so there is nothing to save");
        });
    }

    [Fact]
    public void Saving_waits_while_the_editor_still_holds_a_file_that_is_not_on_the_list()
    {
        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();
        cut.WaitForElement("#std-file-path");

        cut.Find("#std-file-path").Input("CODEOWNERS");

        cut.WaitForAssertion(() =>
        {
            cut.Find(".form-actions button.btn--primary").HasAttribute("disabled").Should().BeTrue();
            cut.Find(".form-actions__note").TextContent.Should()
                .Contain("Add the file you are editing to the list first");
        });

        // Putting it on the list is what unblocks saving.
        cut.Find("#std-editor .card__foot button").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".form-actions button.btn--primary").HasAttribute("disabled").Should().BeFalse();
            cut.Find(".form-actions__note").TextContent.Should().Contain("Unsaved changes");
        });
    }

    [Fact]
    public async Task Editing_a_file_marks_its_row_and_a_long_file_says_the_preview_is_cut_short()
    {
        var longBody = new string('x', 400);
        await SeedAsync(null, [new GitHubStandardFileInput(null, "CODEOWNERS", longBody)]);

        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();
        cut.WaitForElement(".code-block");

        cut.Markup.Should().Contain("Showing the first 240 characters");
        cut.FindAll(".code-block .badge").Should().BeEmpty();

        cut.Find(".code-block__bar button").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".code-block .badge").TextContent.Trim().Should().Be("Editing"));
    }

    [Fact]
    public async Task Opening_a_file_to_read_it_does_not_block_saving()
    {
        // Clicking a file to look at it fills the editor with what is already on
        // the list. That is not an unapplied edit, and treating it as one left
        // Save refused for somebody who had changed nothing at all.
        await SeedAsync(null, [new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al")]);

        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();
        cut.WaitForElement(".code-block");
        cut.FindAll(".check input[type=checkbox]")[0].Change(true);
        cut.WaitForAssertion(() =>
            cut.Find(".form-actions button.btn--primary").HasAttribute("disabled").Should().BeFalse());

        cut.Find(".code-block__bar button").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".code-block .badge").TextContent.Trim().Should().Be("Editing");
            cut.Find(".form-actions button.btn--primary").HasAttribute("disabled").Should().BeFalse(
                "the editor holds the file as it already is, so there is nothing unapplied");
        });
    }

    [Fact]
    public async Task Changing_an_open_file_does_block_saving_until_it_is_applied()
    {
        await SeedAsync(null, [new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al")]);

        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();
        cut.WaitForElement(".code-block");
        cut.Find(".code-block__bar button").Click();
        cut.WaitForElement("#std-file-path");

        cut.Find("#std-file-content").Input("* @cronus-dk/consultants");

        cut.WaitForAssertion(() =>
        {
            cut.Find(".form-actions button.btn--primary").HasAttribute("disabled").Should().BeTrue();
            cut.Find(".form-actions__note").TextContent.Should()
                .Contain("Add the file you are editing to the list first");
        });
    }

    [Fact]
    public void A_path_that_would_escape_the_repository_is_refused_in_the_page_before_the_server_sees_it()
    {
        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();
        cut.WaitForElement("#std-file-path");

        cut.Find("#std-file-path").Input("../outside.yml");
        cut.Find("#std-editor .card__foot button").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".field-error").TextContent.Should().Contain("inside the repository");
            cut.FindAll(".code-block").Should().BeEmpty();
        });
    }

    [Fact]
    public void Two_files_at_the_same_path_are_refused_in_the_page()
    {
        var cut = _ctx.Render<AdminAdministrationRepositoryStandards>();
        cut.WaitForElement("#std-file-path");

        cut.Find("#std-file-path").Input("CODEOWNERS");
        cut.Find("#std-editor .card__foot button").Click();
        cut.Find("#std-file-path").Input("codeowners");
        cut.Find("#std-editor .card__foot button").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".field-error").TextContent.Should().Contain("already goes to");
            cut.FindAll(".code-block").Should().HaveCount(1);
        });
    }

    [Fact]
    public async Task The_repositories_tab_offers_the_standards_and_says_what_is_configured()
    {
        await ConnectGitHubAsync();

        var before = _ctx.Render<AdminAdministrationRepositories>();
        before.WaitForAssertion(() =>
        {
            before.Markup.Should().Contain("Repository standards");
            before.Markup.Should().Contain("Nothing set up yet");
            before.Markup.Should().Contain("/admin/administration/repositories/standards");
        });

        await SeedAsync(
            new GitHubRepositoryRuleset { BlockForcePushes = true },
            [new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al-team")]);

        var after = _ctx.Render<AdminAdministrationRepositories>();
        after.WaitForAssertion(() =>
            after.Markup.Should().Contain("Every new repository gets 1 file and your branch rules."));
    }

    /// <summary>The connection the standards row only appears alongside.</summary>
    private async Task ConnectGitHubAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = 42,
            GitHubOrgLogin = "cronus-dk",
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedAsync(
        GitHubRepositoryRuleset? ruleset, IReadOnlyList<GitHubStandardFileInput> files)
    {
        await using var ctx = _db.NewContext();
        await _db.NewGitHubRepositoryStandardsService(ctx).SaveAsync(ruleset, files);
    }
}
