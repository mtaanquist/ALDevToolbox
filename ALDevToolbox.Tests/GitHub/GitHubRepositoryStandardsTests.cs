using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The per-organisation repository standards (issue #628): what is stored, what
/// is refused, and that one organisation's standards are invisible to another.
/// </summary>
public sealed class GitHubRepositoryStandardsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Nothing_configured_reads_as_nothing_rather_than_as_a_default()
    {
        await using var ctx = _db.NewContext();
        var standards = await _db.NewGitHubRepositoryStandardsService(ctx).GetAsync();

        standards.Ruleset.Should().BeNull();
        standards.Files.Should().BeEmpty();
        (await _db.NewGitHubRepositoryStandardsService(ctx).GetSummaryAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Files_and_a_ruleset_are_saved_and_read_back_in_the_admins_order()
    {
        await using var ctx = _db.NewContext();
        var service = _db.NewGitHubRepositoryStandardsService(ctx);

        await service.SaveAsync(
            new GitHubRepositoryRuleset
            {
                RequirePullRequest = true,
                RequiredApprovals = 2,
                RequireLinearHistory = true,
                BlockForcePushes = true,
                RequiredStatusChecks = { "build", "test" },
            },
            [
                new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al-team"),
                new GitHubStandardFileInput(null, ".github/workflows/build.yml", "name: build"),
            ]);

        await using var read = _db.NewContext();
        var standards = await _db.NewGitHubRepositoryStandardsService(read).GetAsync();

        standards.Files.Select(f => f.Path).Should()
            .Equal("CODEOWNERS", ".github/workflows/build.yml");
        standards.Ruleset!.RequiredApprovals.Should().Be(2);
        standards.Ruleset.RequiredStatusChecks.Should().Equal("build", "test");
        standards.Ruleset.BlockForcePushes.Should().BeTrue();
    }

    [Fact]
    public async Task Saving_again_updates_the_rows_it_was_given_and_deletes_the_ones_it_was_not()
    {
        await using var ctx = _db.NewContext();
        var service = _db.NewGitHubRepositoryStandardsService(ctx);
        await service.SaveAsync(null,
        [
            new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al-team"),
            new GitHubStandardFileInput(null, "SECURITY.md", "Report to security@cronus.example"),
        ]);

        await using (var second = _db.NewContext())
        {
            var current = await _db.NewGitHubRepositoryStandardsService(second).GetAsync();
            var keep = current.Files.Single(f => f.Path == "CODEOWNERS");
            await _db.NewGitHubRepositoryStandardsService(second).SaveAsync(null,
                [new GitHubStandardFileInput(keep.Id, "CODEOWNERS", "* @cronus-dk/platform")]);
        }

        await using var read = _db.NewContext();
        var files = (await _db.NewGitHubRepositoryStandardsService(read).GetAsync()).Files;
        files.Should().ContainSingle()
            .Which.Content.Should().Be("* @cronus-dk/platform");
    }

    [Fact]
    public async Task A_ruleset_with_nothing_ticked_can_be_cleared_back_to_nothing()
    {
        await using var ctx = _db.NewContext();
        await _db.NewGitHubRepositoryStandardsService(ctx).SaveAsync(
            new GitHubRepositoryRuleset { BlockForcePushes = true }, []);

        await using (var second = _db.NewContext())
        {
            await _db.NewGitHubRepositoryStandardsService(second).SaveAsync(null, []);
        }

        await using var read = _db.NewContext();
        (await _db.NewGitHubRepositoryStandardsService(read).GetAsync()).Ruleset.Should().BeNull();
    }

    [Fact]
    public async Task The_summary_is_the_sentence_the_admin_page_shows()
    {
        await using var ctx = _db.NewContext();
        var service = _db.NewGitHubRepositoryStandardsService(ctx);

        await service.SaveAsync(null, [new GitHubStandardFileInput(null, "CODEOWNERS", "*")]);
        await using (var one = _db.NewContext())
        {
            (await _db.NewGitHubRepositoryStandardsService(one).GetSummaryAsync())
                .Should().Be("1 file");
        }

        await using (var two = _db.NewContext())
        {
            var current = await _db.NewGitHubRepositoryStandardsService(two).GetAsync();
            await _db.NewGitHubRepositoryStandardsService(two).SaveAsync(
                new GitHubRepositoryRuleset { RequirePullRequest = true, RequiredApprovals = 1 },
                current.Files.Select(f => new GitHubStandardFileInput(f.Id, f.Path, f.Content)).ToList());
        }

        await using var read = _db.NewContext();
        (await _db.NewGitHubRepositoryStandardsService(read).GetSummaryAsync())
            .Should().Be("1 file and a branch ruleset");
    }

    [Fact]
    public async Task A_ruleset_that_asks_for_nothing_is_not_counted_as_configured()
    {
        await using var ctx = _db.NewContext();
        await _db.NewGitHubRepositoryStandardsService(ctx).SaveAsync(
            new GitHubRepositoryRuleset { RequiredApprovals = 3 }, []);

        await using var read = _db.NewContext();
        // Approvals without "require a pull request" ask GitHub for nothing, so
        // the pages that only say whether standards exist must not claim one.
        (await _db.NewGitHubRepositoryStandardsService(read).GetSummaryAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("/CODEOWNERS")]
    [InlineData("../escape.yml")]
    [InlineData(".github/../../etc/passwd")]
    [InlineData("")]
    public async Task A_path_that_would_not_stay_inside_the_repository_is_refused_on_its_own_row(string path)
    {
        await using var ctx = _db.NewContext();
        var service = _db.NewGitHubRepositoryStandardsService(ctx);

        var act = () => service.SaveAsync(null, [new GitHubStandardFileInput(null, path, "x")]);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Files[0].Path");
    }

    [Fact]
    public async Task Two_files_at_the_same_path_are_refused_before_the_unique_index_says_so()
    {
        await using var ctx = _db.NewContext();
        var service = _db.NewGitHubRepositoryStandardsService(ctx);

        var act = () => service.SaveAsync(null,
        [
            new GitHubStandardFileInput(null, "CODEOWNERS", "a"),
            new GitHubStandardFileInput(null, "codeowners", "b"),
        ]);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Files[1].Path");
    }

    [Fact]
    public async Task More_approvals_than_github_accepts_is_a_form_error_not_a_refusal_from_github()
    {
        await using var ctx = _db.NewContext();
        var service = _db.NewGitHubRepositoryStandardsService(ctx);

        var act = () => service.SaveAsync(
            new GitHubRepositoryRuleset { RequirePullRequest = true, RequiredApprovals = 99 }, []);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey(GitHubRepositoryStandardsService.RulesetField);
    }

    [Fact]
    public async Task One_organisations_standards_are_invisible_to_another()
    {
        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubRepositoryStandardsService(ctx).SaveAsync(
                new GitHubRepositoryRuleset { BlockForcePushes = true },
                [new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al-team")]);
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using (var other = _db.NewContext())
        {
            var standards = await _db.NewGitHubRepositoryStandardsService(other).GetAsync();
            standards.Files.Should().BeEmpty();
            standards.Ruleset.Should().BeNull();
        }

        // And the other organisation's own save does not touch the first one's rows.
        await using (var other = _db.NewContext())
        {
            await _db.NewGitHubRepositoryStandardsService(other).SaveAsync(
                null, [new GitHubStandardFileInput(null, "OTHER.md", "theirs")]);
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using var read = _db.NewContext();
        var mine = await _db.NewGitHubRepositoryStandardsService(read).GetAsync();
        mine.Files.Select(f => f.Path).Should().Equal("CODEOWNERS");
        mine.Ruleset!.BlockForcePushes.Should().BeTrue();
    }

    [Fact]
    public async Task The_rows_carry_the_acting_organisation()
    {
        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubRepositoryStandardsService(ctx).SaveAsync(
                null, [new GitHubStandardFileInput(null, "CODEOWNERS", "*")]);
        }

        await using var read = _db.NewContext();
        var row = await read.GitHubRepositoryStandardFiles.AsNoTracking().SingleAsync();
        row.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        row.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Saving_outside_an_authenticated_request_is_refused_rather_than_writing_nowhere()
    {
        _db.OrgContext.CurrentOrganizationId = null;
        await using var ctx = _db.NewContext();
        var service = _db.NewGitHubRepositoryStandardsService(ctx);

        var act = () => service.SaveAsync(null, []);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
    }
}
