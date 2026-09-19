using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Operations;
using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// "Create repository" (issue #622): where the repository lands, what its first
/// commit contains, which credential makes each call, and every refusal on the
/// way there.
///
/// <para>The rules worth a test are the ones a mistake would be expensive for:
/// the repository is created in the organisation this organisation connected
/// and nowhere else, someone outside that GitHub organisation is refused before
/// anything exists, and the workspace arrives at the repository's root with its
/// saved settings in it - which is what lets New Extension fill itself in from
/// the repository later.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.
/// The request shapes are GitHub's documented ones - they have not been
/// exercised against api.github.com from this environment.</para>
/// </summary>
public sealed class GitHubWorkspaceRepositoryTests : IDisposable
{
    private const int UserId = 811;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoName = "CRONUS-Customer";
    private const string Repo = $"{OrgLogin}/{RepoName}";
    private const string InstallationToken = "ghs_installation";
    private const string UserToken = "ghu_access";

    private readonly TestDb _db = new();

    public GitHubWorkspaceRepositoryTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "dev@cronus.example",
            DisplayName = "Dev Eloper",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task It_creates_the_repository_in_the_connected_organisation()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.Repository.FullName.Should().Be(Repo);
        created.Repository.HtmlUrl.Should().Be($"https://github.com/{Repo}");
        created.FileCount.Should().BeGreaterThan(0);

        // The organisation is never a parameter: it is the one this toolbox
        // organisation connected, so a caller naming a repository cannot aim it
        // anywhere else.
        api.Calls.Should().Contain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
        var body = BodyOf(api, "POST", $"/orgs/{OrgLogin}/repos");
        body.Should().Contain($"\"name\":\"{RepoName}\"");
        body.Should().Contain("\"private\":true");
        // Nothing is auto-initialised: whatever the template says belongs in the
        // repository arrives in the first commit instead.
        body.Should().Contain("\"auto_init\":false");
    }

    [Fact]
    public async Task The_repository_is_created_by_the_organisations_app_not_by_the_person()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // The credential split from .design/github-integration.md: creating a
        // repository is an act of the organisation, so it rides the installation
        // token - the opposite of #623, where a write into an existing
        // repository goes out as the user.
        TokenFor(api, "POST", $"/orgs/{OrgLogin}/repos").Should().Be(InstallationToken);
        TokenFor(api, "PUT", $"/repos/{Repo}/contents/").Should().Be(InstallationToken);
        TokenFor(api, "POST", $"/repos/{Repo}/git/trees").Should().Be(InstallationToken);
        TokenFor(api, "POST", $"/repos/{Repo}/git/refs").Should().Be(InstallationToken);
        // The default-branch setting rides the same token, which is the grant
        // the branch-rules step already needs (#811).
        TokenFor(api, "PATCH", $"/repos/{Repo}").Should().Be(InstallationToken);
        // The person's own token is what answers "are they in this
        // organisation", and it is used for nothing else here.
        api.Credentials.Where(c => c.Token == UserToken)
            .Should().OnlyContain(c => c.Call.Contains($"/orgs/{OrgLogin}/members/"));
    }

    [Fact]
    public async Task The_first_commit_is_the_workspace_at_the_repositorys_root()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        var tree = BodyOf(api, "POST", "/git/trees");
        // The ZIP nests everything under the workspace folder because that is
        // what a user unzips. A repository is that folder, so the prefix comes
        // off - a repository whose files all sat one level down would have to be
        // rearranged by hand before it could be opened.
        tree.Should().NotContain("CRONUSCustomer/");
        tree.Should().Contain("Core/app.json");
        // Saved settings ride along, which is what lets the New Extension page
        // fill itself in from this repository afterwards (#623).
        tree.Should().Contain(WorkspaceConfigService.FileName);
        // Built from nothing: the repository has no history to layer onto.
        tree.Should().NotContain("base_tree");

        var commit = BodyOf(api, "POST", "/git/commits");
        // A root commit: the default branch does not exist yet, and building it
        // whole is what keeps a pull-request rule from refusing the workspace
        // (#811). The seed sits on a branch of its own, so it is not a parent.
        commit.Should().Contain("\"parents\":[]");
        commit.Should().Contain("Initial commit");
        // Credited to whoever asked for it, not to the app that made the call.
        // (The + in a GitHub noreply address comes back JSON-escaped.)
        commit.Should().Contain("cronus-dev@users.noreply.github.com");
        commit.Should().Contain("\"name\":\"cronus-dev\"");

        // The default branch is created at that commit, never moved on to it.
        BodyOf(api, "POST", "/git/refs").Should().Contain("\"ref\":\"refs/heads/main\"");
    }

    [Fact]
    public async Task The_first_write_into_the_empty_repository_goes_through_the_contents_api()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // A repository created with auto_init: false has no commits, and GitHub
        // answers 409 "Git Repository is empty." to every Git Data call on one.
        // PUT contents is the only route that works there, so it has to come
        // first - the whole flow died on its first blob before it did.
        var firstWrite = api.Calls.First(
            c => c.Contains($"/repos/{Repo}/") && !c.StartsWith("GET", StringComparison.Ordinal));
        firstWrite.Should().StartWith("PUT").And.Contain("/contents/");

        // Seeded with the README: it is what GitHub itself would have put in an
        // initial commit, and it is a file the generator produced rather than
        // one auto-init invented.
        firstWrite.Should().Contain("/contents/README.md");
        // Onto a branch of its own, not onto the default branch: the default
        // branch has to stay unborn so it can be created at the finished commit
        // instead of updated (#811).
        BodyOf(api, "PUT", "/contents/").Should().Contain("\"branch\":\"aldt/seed\"");
    }

    [Fact]
    public async Task The_default_branch_is_created_at_the_finished_commit_and_never_updated()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.Delivery.Should().Be(GitHubWorkspaceDelivery.DefaultBranch);
        created.PullRequestUrl.Should().BeNull();

        // The whole point of #811: an organisation ruleset that requires a pull
        // request refuses an update of the default branch and does not govern
        // creating it. One creation, and no update at all.
        api.Bodies.Count(b => b.Call.StartsWith("POST") && b.Call.Contains("/git/refs"))
            .Should().Be(1);
        BodyOf(api, "POST", "/git/refs").Should().Contain("\"ref\":\"refs/heads/main\"");
        api.Calls.Should().NotContain(c => c.StartsWith("PATCH") && c.Contains("/git/refs/heads/"));

        // Seeding made the throwaway branch the repository's default, so the
        // real branch has to be pointed at by hand - a repository setting, not
        // a write to a ref - and the throwaway branch then goes.
        DefaultBranchBody(api).Should().Contain("\"default_branch\":\"main\"");
        api.Calls.Should().Contain(c => c.StartsWith("DELETE") && c.Contains("/git/refs/heads/aldt/seed"));
    }

    private const string ValidationFailedJson =
        """{"message":"Validation Failed","errors":[{"resource":"Repository","field":"default_branch","code":"invalid"}]}""";

    [Fact]
    public async Task A_default_branch_switch_github_is_not_ready_for_is_asked_again()
    {
        await ReadyAsync();
        // Seen against a real organisation: the branch was created, and the
        // settings write two milliseconds later came back 422 Validation Failed.
        var api = WritableApi()
            .OnSequence(HttpMethod.Patch, $"/repos/{Repo}",
                (HttpStatusCode.UnprocessableEntity, ValidationFailedJson),
                (HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo)));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.DefaultBranchWarning.Should().BeNull();
        api.Calls.Count(c => c.StartsWith("PATCH") && c.EndsWith($"/repos/{Repo}")).Should().Be(2);
        api.Calls.Should().Contain(c => c.StartsWith("DELETE") && c.Contains("/git/refs/heads/aldt/seed"));
    }

    [Fact]
    public async Task A_default_branch_github_will_not_switch_is_a_warning_on_a_repository_that_is_full()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Patch, $"/repos/{Repo}", HttpStatusCode.UnprocessableEntity, ValidationFailedJson)
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK,
                FakeGitHubApi.RepositoryJson(Repo, defaultBranch: "aldt/seed"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // The workspace is whole on main by now, so this is a success with one
        // thing left to do by hand - not "GitHub refused to create the
        // repository", and not a repository missing its solution and audit entry.
        created.Delivery.Should().Be(GitHubWorkspaceDelivery.DefaultBranch);
        created.DefaultBranchWarning.Should().Contain("Default branch").And.Contain("main");
        created.DefaultBranchWarning.Should().NotContain("Validation Failed").And.NotContain("aldt/");
        created.SolutionWarning.Should().BeNull();
        // The throwaway branch is the default, and GitHub does not delete one.
        api.Calls.Should().NotContain(c => c.StartsWith("DELETE") && c.Contains("/git/refs/heads/aldt/seed"));

        await using var read = _db.NewContext();
        (await read.AuditLog.AsNoTracking()
            .CountAsync(e => e.EntityType == AuditEntityType.GitHubRepository)).Should().Be(1);
    }

    [Fact]
    public async Task The_organisations_standards_ride_in_the_initial_commit_and_win_on_a_shared_path()
    {
        await ReadyAsync();
        await ConfigureStandardsAsync(files:
        [
            new GitHubStandardFileInput(null, ".github/workflows/build.yml", "name: build"),
            // The same path the generator produces, which is how an
            // organisation overrides what a template ships (#628).
            new GitHubStandardFileInput(null, "README.md", "# The organisation's README"),
        ]);
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.StandardsFileCount.Should().Be(2);
        created.StandardsWarning.Should().BeNull();

        // One commit, not three: the default branch can only be created at a
        // commit that already describes the whole repository (#811).
        api.Calls.Count(c => c.Contains("/git/commits")).Should().Be(1);
        api.Calls.Count(c => c.StartsWith("POST") && c.Contains("/git/trees")).Should().Be(1);

        var tree = BodyOf(api, "POST", "/git/trees");
        tree.Should().Contain(".github/workflows/build.yml");
        tree.Should().Contain("Core/app.json");
        tree.Should().NotContain("base_tree");
        // One entry for a path both sides produce, and it is the organisation's
        // blob: the standard replaces the template's file rather than joining it.
        System.Text.RegularExpressions.Regex.Matches(tree, "\"README.md\"").Count.Should().Be(1);
        var readme = api.Bodies
            .Where(b => b.Call.Contains("/git/blobs"))
            .Select(b => b.Body)
            .Where(b => b.Contains(Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("# The organisation's README"))));
        readme.Should().ContainSingle("the organisation's README is the one that was uploaded");
    }

    [Fact]
    public async Task The_seed_commit_names_the_same_person_as_the_workspace_commit()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // The seed goes out on the installation token, so with no author GitHub
        // credits it to the app - leaving a new repository opening on an initial
        // commit by a bot followed by one by the person who asked for it.
        var seed = BodyOf(api, "PUT", "/contents/");
        seed.Should().Contain("cronus-dev@users.noreply.github.com");
        seed.Should().Contain("\"name\":\"cronus-dev\"");
        // Both objects: GitHub splits author from committer, and the committer
        // is the one its commit list shows.
        seed.Should().Contain("\"committer\":");

        var commit = BodyOf(api, "POST", "/git/commits");
        commit.Should().Contain("cronus-dev@users.noreply.github.com");
    }

    [Fact]
    public async Task Something_else_writing_the_seed_path_first_is_refused_as_a_race()
    {
        await ReadyAsync();
        var api = WritableApi()
            // The seed write quotes no sha, so GitHub answers an existing path
            // with this 422 rather than the 409 a stale-sha write gets. Both
            // mean the same thing to a caller that expected to be creating the
            // file, and in a repository this new it means something else got in
            // between the create and the first write.
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.UnprocessableEntity,
                """{"message":"Invalid request. \"sha\" wasn't supplied."}""");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // The race refusal, not the generic "GitHub refused the request" - the
        // user is told what happened and that the repository is already there.
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Values.Should().ContainSingle()
            .Which.Should().Contain("pushed to");
    }

    [Fact]
    public async Task The_workspace_commit_carries_every_generated_file_including_the_seeded_one()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // The tree is built from nothing, so it has to list everything the
        // repository should end up with. Leaving the seeded file out would not
        // duplicate it - it would delete it.
        var tree = BodyOf(api, "POST", "/git/trees");
        tree.Should().Contain("README.md");
        tree.Should().NotContain("base_tree");
        System.Text.RegularExpressions.Regex.Matches(tree, "\"README.md\"").Count
            .Should().Be(1, "the seeded file is one entry, not two");
        api.Calls.Count(c => c.Contains("/git/blobs")).Should().Be(created.FileCount);
    }

    [Fact]
    public async Task It_is_recorded_in_the_audit_log_against_the_person_who_asked()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        await using var read = _db.NewContext();
        var entry = await read.AuditLog.AsNoTracking()
            .SingleAsync(e => e.EntityType == AuditEntityType.GitHubRepository);
        entry.Action.Should().Be(AuditAction.Created);
        entry.EntityName.Should().Be(Repo);
        entry.ChangedByUserId.Should().Be(UserId);
        entry.ChangedBy.Should().Contain("dev@cronus.example");
        entry.OrganizationId.Should().Be(TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task Someone_outside_the_github_organisation_is_refused_before_anything_is_created()
    {
        await ReadyAsync();
        // GitHub answers 404 for a membership you do not have.
        var api = WritableApi()
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain(OrgLogin);
        api.Calls.Should().NotContain(c => c.Contains("/repos"),
            "nothing is created for someone the organisation does not list");
    }

    [Fact]
    public async Task A_membership_github_would_not_answer_is_a_refusal_rather_than_a_pass()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.ServiceUnavailable);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GitHubRepository");
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
    }

    [Fact]
    public async Task A_name_the_organisation_already_uses_is_reported_on_the_name_field()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.UnprocessableEntity,
                // GitHub's own shape: message says only that it failed, and the
                // reason is in errors[].
                "{\"message\":\"Repository creation failed.\",\"errors\":[{\"resource\":\"Repository\","
                + "\"field\":\"name\",\"message\":\"name already exists on this account\"}]}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        var errors = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors;
        errors.Should().ContainKey("GitHubRepositoryName");
        errors["GitHubRepositoryName"].Should().Contain(RepoName);
    }

    [Fact]
    public async Task An_app_that_may_not_create_repositories_says_so_without_naming_a_permission()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.Forbidden,
                "{\"message\":\"Resource not accessible by integration\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        var message = (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"];
        message.Should().Contain(OrgLogin);
        // CLAUDE.md bans surfacing the machine name of the grant; the reader has
        // to ask somebody for something, not quote "administration:write".
        message.Should().NotContain("administration");
    }

    [Fact]
    public async Task A_recorded_grant_that_is_missing_is_refused_before_the_round_trip()
    {
        await ReadyAsync(permissions: "{\"administration\":\"read\",\"contents\":\"write\"}");
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GitHubRepository");
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("slash/es")]
    [InlineData("..")]
    [InlineData("")]
    public async Task A_name_github_would_not_keep_is_refused_before_anything_is_asked(string name)
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), name, isPrivate: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GitHubRepositoryName");
        api.Calls.Should().BeEmpty("a name GitHub would rewrite is not worth a round trip");
    }

    [Fact]
    public async Task An_invalid_plan_is_refused_on_its_own_fields_before_github_is_asked_anything()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(
            WorkspacePlan() with { WorkspaceName = "!!!" }, RepoName, isPrivate: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("WorkspaceName");
        api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unlinked_person_is_told_to_connect_their_own_github_account()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await SeedTemplateAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("Connect your own GitHub account");
    }

    [Fact]
    public async Task An_organisation_with_no_github_connection_is_pointed_at_the_admin_who_can_make_one()
    {
        await ConfigureDeploymentAsync();
        await SeedTemplateAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("Administration -> Repositories");
    }

    [Theory]
    // The shape people actually name repositories, from the customer's name.
    // Lowercase kebab-case since #755, so a repository is found by the same
    // form whoever typed the customer in - and so "Jørgensen Møbler" reaches a
    // name GitHub will take. Only a suggestion - the field is editable.
    [InlineData("CRONUS Customer", "cronus-customer")]
    [InlineData("CRONUS A/S", "cronus-a-s")]
    [InlineData("CRONUS  Customer", "cronus-customer")]
    [InlineData("  CRONUS  ", "cronus")]
    [InlineData("Jørgensen Møbler", "jorgensen-mobler")]
    [InlineData("", "")]
    public void The_suggested_name_is_one_github_would_keep(string workspaceName, string expected)
    {
        GitHubWorkspaceRepositoryService.SuggestName(workspaceName).Should().Be(expected);
    }

    [Theory]
    // The style is the organisation's (#757); the default above is only the
    // default. Whatever it is set to, the suggestion follows it.
    [InlineData(NamingStyle.SnakeCase, "jorgensen_mobler")]
    [InlineData(NamingStyle.Lowercase, "jorgensenmobler")]
    [InlineData(NamingStyle.PascalCase, "JorgensenMobler")]
    public void The_suggested_name_follows_the_organisations_repository_style(
        NamingStyle style, string expected)
    {
        GitHubWorkspaceRepositoryService.SuggestName("Jørgensen Møbler", style).Should().Be(expected);
    }

    // --- repository standards (#628) ----------------------------------------

    [Fact]
    public async Task With_no_standards_configured_the_flow_is_the_one_it_was_before()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.StandardsFileCount.Should().Be(0);
        created.StandardsWarning.Should().BeNull();
        // One commit, and nothing was asked about branch rules: an organisation
        // that has set no standards must not pay for the feature.
        api.Calls.Count(c => c.Contains("/git/commits")).Should().Be(1);
        api.Calls.Should().NotContain(c => c.Contains("/rulesets"));
        api.Calls.Should().NotContain(c => c.Contains("/git/ref/heads/"));
    }

    [Fact]
    public async Task A_branch_ruleset_is_created_on_the_default_branch_after_the_files()
    {
        await ReadyAsync();
        await ConfigureStandardsAsync(ruleset: new GitHubRepositoryRuleset
        {
            RequirePullRequest = true,
            RequiredApprovals = 2,
            RequireLinearHistory = true,
            BlockForcePushes = true,
            RequiredStatusChecks = { "build" },
        });
        var api = RulesetApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.StandardsWarning.Should().BeNull();
        var body = BodyOf(api, "POST", $"/repos/{Repo}/rulesets");
        body.Should().Contain("\"target\":\"branch\"");
        body.Should().Contain("\"enforcement\":\"active\"");
        // The symbolic name, so the rules keep meaning the right branch whatever
        // the repository renames it to.
        body.Should().Contain("~DEFAULT_BRANCH");
        body.Should().Contain("\"pull_request\"");
        body.Should().Contain("\"required_approving_review_count\":2");
        body.Should().Contain("\"required_linear_history\"");
        body.Should().Contain("\"non_fast_forward\"");
        body.Should().Contain("\"context\":\"build\"");
        TokenFor(api, "POST", $"/repos/{Repo}/rulesets").Should().Be(InstallationToken);
    }

    [Fact]
    public async Task A_ruleset_asking_for_nothing_is_never_sent()
    {
        await ReadyAsync();
        // What an admin who unticked everything leaves behind. A ruleset named
        // after us that enforces nothing is worse than no ruleset.
        await ConfigureStandardsAsync(ruleset: new GitHubRepositoryRuleset { RequiredApprovals = 2 });
        var api = RulesetApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        api.Calls.Should().NotContain(c => c.Contains("/rulesets"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"Resource not accessible by integration\"}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Repository rule violations\"}")]
    public async Task A_refused_ruleset_leaves_a_created_repository_and_a_warning(
        HttpStatusCode status, string json)
    {
        await ReadyAsync();
        await ConfigureStandardsAsync(
            ruleset: new GitHubRepositoryRuleset { RequirePullRequest = true, RequiredApprovals = 1 },
            files: [new GitHubStandardFileInput(null, "CODEOWNERS", "* @cronus-dk/al-team")]);
        var api = RulesetApi().On(HttpMethod.Post, $"/repos/{Repo}/rulesets", status, json);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // The repository exists and holds both commits by this point; failing
        // here would leave it behind with a stack trace over it.
        created.Repository.FullName.Should().Be(Repo);
        created.StandardsFileCount.Should().Be(1);
        created.StandardsWarning.Should().NotBeNullOrEmpty();
        // CLAUDE.md bans quoting the machine name of the grant at a reader.
        created.StandardsWarning!.Should().NotContain("administration");

        await using var read = _db.NewContext();
        var entry = await read.AuditLog.AsNoTracking()
            .SingleAsync(e => e.EntityType == AuditEntityType.GitHubRepository);
        entry.EntityName.Should().Be(Repo);
    }

    // --- Organisations that only take pull requests (#811) ------------------

    [Fact]
    public async Task The_rules_on_the_default_branch_are_read_before_anything_is_written()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/rules/branches/", HttpStatusCode.OK,
                FakeGitHubApi.BranchRulesJson("pull_request", "non_fast_forward"));
        var log = new CapturingLoggerProvider();
        var (service, ctx) = NewService(api, logger: LoggerFor(log));
        await using var _ = ctx;

        await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // Read first, so a support question months later has an answer without
        // a trip to the organisation's settings.
        var rulesRead = api.Calls.FindIndex(c => c.Contains("/rules/branches/"));
        var firstWrite = api.Calls.FindIndex(c => c.StartsWith("PUT") || c.StartsWith("POST") && c.Contains($"/repos/{Repo}/"));
        rulesRead.Should().BeGreaterThanOrEqualTo(0);
        rulesRead.Should().BeLessThan(firstWrite);
        log.Messages.Should().Contain(m => m.Contains("pull_request"));

        // And it steers nothing: GitHub's own refusal is the authority, so the
        // direct route is still what is tried.
        api.Calls.Should().NotContain(c => c.Contains("/pulls"));
    }

    [Fact]
    public async Task A_branch_rule_that_refuses_the_new_branch_sends_the_workspace_through_a_pull_request()
    {
        await ReadyAsync();
        var api = PullRequestOnlyApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // A repository is still made and the files are still in it - the person
        // has one thing left to do rather than a one-file repository and an
        // error message (#811).
        created.Repository.FullName.Should().Be(Repo);
        created.Delivery.Should().Be(GitHubWorkspaceDelivery.PullRequest);
        created.PullRequestUrl.Should().Be($"https://github.com/{Repo}/pull/1");
        created.FileCount.Should().BeGreaterThan(0);

        var pull = BodyOf(api, "POST", "/pulls");
        pull.Should().Contain("\"head\":\"aldt/initial-workspace\"");
        // Into the branch the repository was created with. The throwaway branch
        // is not something to leave a customer's repository built around.
        pull.Should().Contain("\"base\":\"main\"");
        pull.Should().Contain("Add the CRONUS Customer workspace");
        pull.Should().Contain("pull request");

        // The repository is left in the shape it would have had anyway, minus
        // the merge: main exists and holds the seeded file, it is the default
        // branch, and the throwaway branch is gone.
        api.Bodies.Should().Contain(b =>
            b.Call.StartsWith("PUT") && b.Call.Contains("/contents/") && b.Body.Contains("\"branch\":\"main\""));
        DefaultBranchBody(api).Should().Contain("\"default_branch\":\"main\"");
        api.Calls.Should().Contain(c => c.StartsWith("DELETE") && c.Contains("/git/refs/heads/aldt/seed"));

        // And the workspace branch grows out of what is on main rather than
        // orphaning it, so merging the pull request is a fast-forward.
        var commits = api.Bodies.Where(b => b.Call.Contains("/git/commits")).Select(b => b.Body).ToList();
        commits[^1].Should().Contain("\"parents\":[\"seed-commit-sha\"]");
    }

    [Fact]
    public async Task The_registration_and_the_audit_entry_happen_on_the_pull_request_route_too()
    {
        await ReadyAsync();
        var api = PullRequestOnlyApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.SolutionCreated.Should().BeTrue();
        created.SolutionWarning.Should().BeNull();

        await using var read = _db.NewContext();
        var entry = await read.AuditLog.AsNoTracking()
            .SingleAsync(e => e.EntityType == AuditEntityType.GitHubRepository);
        entry.EntityName.Should().Be(Repo);
    }

    [Fact]
    public async Task A_pull_request_github_also_refuses_says_what_is_on_the_repository_and_what_to_do()
    {
        await ReadyAsync();
        var api = PullRequestOnlyApi()
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.UnprocessableEntity,
                """{"message":"Validation Failed"}""");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        var message = (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"];
        message.Should().Contain("pull request");
        // What is actually on the repository, named so they can find it, and
        // the two ways out - not GitHub's wording quoted at somebody who cannot
        // act on it, and not the name of a branch the toolbox invented.
        message.Should().Contain(Repo);
        message.Should().Contain("single placeholder file");
        message.Should().Contain("Download ZIP");
        message.Should().Contain("delete the repository");
        message.Should().NotContain("Validation Failed");
        message.Should().NotContain("aldt/");
    }

    /// <summary>
    /// A GitHub whose organisation ruleset refuses a write to the default
    /// branch, which is how issue #811 was reported: creating
    /// <c>refs/heads/main</c> comes back as a rule violation, and the flow has
    /// to reach for a pull request instead.
    /// </summary>
    private static FakeGitHubApi PullRequestOnlyApi() =>
        WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/rules/branches/", HttpStatusCode.OK,
                FakeGitHubApi.BranchRulesJson("pull_request"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", request =>
                // The throwaway branch is fine; the default branch is not.
                ReadBody(request).Contains("refs/heads/main", StringComparison.Ordinal)
                    ? (HttpStatusCode.UnprocessableEntity, FakeGitHubApi.RuleViolationJson())
                    : (HttpStatusCode.Created, FakeGitHubApi.RefJson("aldt/initial-workspace")))
            // Seeding made the throwaway branch the default, which is what the
            // flow has to put right before it opens anything.
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK,
                FakeGitHubApi.RepositoryJson(Repo, defaultBranch: "aldt/seed"))
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/", request =>
                // main was never created - its ref creation is the one that was
                // refused - while the throwaway branch carries the seed commit.
                (request.RequestUri?.AbsolutePath ?? string.Empty).EndsWith("/heads/main", StringComparison.Ordinal)
                    ? (HttpStatusCode.NotFound, """{"message":"Not Found"}""")
                    : (HttpStatusCode.OK, """{"object":{"sha":"seed-commit-sha"}}"""))
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                FakeGitHubApi.PullRequestJson(Repo));

    private static string ReadBody(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

    private static ILogger<GitHubWorkspaceRepositoryService> LoggerFor(CapturingLoggerProvider provider) =>
        LoggerFactory.Create(b => b.AddProvider(provider))
            .CreateLogger<GitHubWorkspaceRepositoryService>();

    /// <summary>
    /// The body of the one PATCH that changes a repository setting rather than
    /// a ref - the default-branch switch.
    /// </summary>
    private static string DefaultBranchBody(FakeGitHubApi api) =>
        api.Bodies
            .Where(b => b.Call.StartsWith("PATCH", StringComparison.Ordinal) && b.Call.EndsWith($"/repos/{Repo}", StringComparison.Ordinal))
            .Select(b => b.Body)
            .LastOrDefault()
            ?? throw new InvalidOperationException("The repository's default branch was never set.");
    // --- The customer as a solution (#759) ---------------------------------

    [Fact]
    public async Task A_chosen_solution_gets_the_new_repository()
    {
        await ReadyAsync();
        var solutionId = await SeedSolutionAsync("CRONUS Customer", ownedByCaller: true);
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var created = await service.CreateAsync(
            WorkspacePlan(), RepoName, isPrivate: true, solutionId: solutionId);

        created.SolutionId.Should().Be(solutionId);
        created.SolutionName.Should().Be("CRONUS Customer");
        created.SolutionCreated.Should().BeFalse();
        created.SolutionWarning.Should().BeNull();

        await using var read = _db.NewContext();
        var repo = await read.OeProjectRepositories.AsNoTracking()
            .SingleAsync(r => r.ProjectId == solutionId);
        // The clone URL and the repository's own name, so the row is the one a
        // person adding it by hand would have typed - discovery and the build
        // pipeline read it the same way either way.
        repo.Provider.Should().Be(RepositoryProvider.GitHub);
        repo.Url.Should().Be($"https://github.com/{Repo}.git");
        repo.DisplayName.Should().Be(RepoName);
    }

    [Fact]
    public async Task With_no_solution_chosen_the_customer_becomes_one()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var created = await service.CreateAsync(
            WorkspacePlan(shortName: "CRO"), RepoName, isPrivate: true);

        created.SolutionCreated.Should().BeTrue();
        created.SolutionName.Should().Be("CRONUS Customer");
        created.SolutionWarning.Should().BeNull();

        await using var read = _db.NewContext();
        var solution = await read.OeProjects.AsNoTracking()
            .Include(p => p.Repositories)
            .SingleAsync(p => p.Id == created.SolutionId);
        solution.Name.Should().Be("CRONUS Customer");
        solution.ShortName.Should().Be("CRO");
        // Public and owned by whoever pressed the button: what every solution
        // starts as, so this on-ramp leaves nothing to explain later.
        solution.Visibility.Should().Be(
            ALDevToolbox.Domain.Entities.ObjectExplorer.ProjectVisibility.Public);
        solution.CreatedByUserId.Should().Be(UserId);
        solution.Repositories.Should().ContainSingle()
            .Which.Url.Should().Be($"https://github.com/{Repo}.git");
    }

    [Fact]
    public async Task A_solution_the_caller_cannot_manage_is_refused_before_anything_is_created()
    {
        await ReadyAsync();
        var solutionId = await SeedSolutionAsync("Somebody else's customer", ownedByCaller: false);
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(
            WorkspacePlan(), RepoName, isPrivate: true, solutionId: solutionId);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(GitHubWorkspaceRepositoryService.SolutionField);
        // The whole point of ruling it out up front: no repository is left
        // behind with nowhere to go.
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
    }

    [Fact]
    public async Task A_solution_that_will_not_save_is_a_warning_on_a_success()
    {
        await ReadyAsync();
        // Another solution already carries this customer's name, which the
        // solution validator refuses - the likeliest way this step fails.
        await SeedSolutionAsync("CRONUS Customer", ownedByCaller: false);
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        // The repository exists and holds the files by this point; losing it
        // over a name clash would be the worst answer available.
        created.Repository.FullName.Should().Be(Repo);
        created.FileCount.Should().BeGreaterThan(0);
        created.SolutionId.Should().BeNull();
        created.SolutionWarning.Should().NotBeNullOrEmpty();
        created.SolutionWarning!.Should().Contain("Solutions");

        await using var read = _db.NewContext();
        var entry = await read.AuditLog.AsNoTracking()
            .SingleAsync(e => e.EntityType == AuditEntityType.GitHubRepository);
        entry.EntityName.Should().Be(Repo);
        entry.EntityId.Should().Be(0, "there is no solution for the entry to name");
    }

    [Fact]
    public async Task The_audit_entry_names_the_solution_the_repository_was_registered_on()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        await using var read = _db.NewContext();
        var entry = await read.AuditLog.AsNoTracking()
            .SingleAsync(e => e.EntityType == AuditEntityType.GitHubRepository);
        entry.EntityName.Should().Be(Repo);
        entry.EntityId.Should().Be(created.SolutionId!.Value);
    }

    // --- Solutions switched off for the organisation (#772) ----------------

    [Fact]
    public async Task With_solutions_switched_off_the_repository_is_created_and_nothing_is_registered()
    {
        await ReadyAsync();
        await DisableSolutionsForOrgAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(shortName: "CRO"), RepoName, isPrivate: true);

        created.Repository.FullName.Should().Be(Repo);
        created.FileCount.Should().BeGreaterThan(0);
        // Not a failure and not a warning: this organisation asked not to have
        // Solutions, so there is nothing to report about one.
        created.SolutionId.Should().BeNull();
        created.SolutionName.Should().BeNull();
        created.SolutionCreated.Should().BeFalse();
        created.SolutionWarning.Should().BeNull();

        await using var read = _db.NewContext();
        (await read.OeProjects.AsNoTracking().AnyAsync()).Should().BeFalse();
        var entry = await read.AuditLog.AsNoTracking()
            .SingleAsync(e => e.EntityType == AuditEntityType.GitHubRepository);
        entry.EntityId.Should().Be(0, "there is no solution for the entry to name");
    }

    [Fact]
    public async Task A_solution_named_while_solutions_are_switched_off_is_refused_before_anything_is_created()
    {
        await ReadyAsync();
        var solutionId = await SeedSolutionAsync("CRONUS Customer", ownedByCaller: true);
        await DisableSolutionsForOrgAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.CreateAsync(
            WorkspacePlan(), RepoName, isPrivate: true, solutionId: solutionId);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(GitHubWorkspaceRepositoryService.SolutionField);
        ex.Which.Errors[GitHubWorkspaceRepositoryService.SolutionField]
            .Should().Contain("switched off");
        // Refused up front, like every other refusal: no repository is left
        // behind by an argument the organisation cannot honour.
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
    }

    [Fact]
    public async Task Solutions_switched_off_site_wide_stops_the_registration_too()
    {
        await ReadyAsync();
        var siteToggles = TestDb.EverythingEnabled();
        siteToggles.Set(new[] { ToolKey.Projects });
        var (service, ctx) = NewService(WritableApi(), siteToggles);
        await using var _ = ctx;

        var created = await service.CreateAsync(WorkspacePlan(), RepoName, isPrivate: true);

        created.Repository.FullName.Should().Be(Repo);
        created.SolutionId.Should().BeNull();
        created.SolutionWarning.Should().BeNull();

        await using var read = _db.NewContext();
        (await read.OeProjects.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Switches Solutions off for the acting organisation, the way an org Admin
    /// does on the Administration tools page.
    /// </summary>
    private async Task DisableSolutionsForOrgAsync()
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        org.DisabledTools = new List<string> { nameof(ToolKey.Projects) };
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// One solution to point at, either this caller's or somebody else's - the
    /// difference between a customer they may add a repository to and one they
    /// may not.
    /// </summary>
    private async Task<int> SeedSolutionAsync(string name, bool ownedByCaller)
    {
        await using var ctx = _db.NewContext();
        if (!ownedByCaller)
        {
            ctx.Users.Add(new User
            {
                Id = UserId + 1,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "other@cronus.example",
                DisplayName = "Other Person",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
        }
        var project = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            DefaultArtifactCountry = "dk",
            CreatedByUserId = ownedByCaller ? UserId : UserId + 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>
    /// A GitHub that also answers the two reads and the one write the standards
    /// phase makes: where the branch is, what tree that commit points at, and
    /// the ruleset.
    /// </summary>
    private static FakeGitHubApi RulesetApi() =>
        WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/", HttpStatusCode.OK,
                """{"object":{"sha":"new-commit-sha"}}""")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/commits/", HttpStatusCode.OK,
                """{"sha":"new-commit-sha","tree":{"sha":"workspace-tree-sha"}}""")
            .On(HttpMethod.Post, $"/repos/{Repo}/rulesets", HttpStatusCode.Created, """{"id":7}""");

    /// <summary>The standards an Admin would have saved from Administration -> Repositories.</summary>
    private async Task ConfigureStandardsAsync(
        GitHubRepositoryRuleset? ruleset = null, IReadOnlyList<GitHubStandardFileInput>? files = null)
    {
        await using var ctx = _db.NewContext();
        await _db.NewGitHubRepositoryStandardsService(ctx).SaveAsync(ruleset, files ?? []);
    }

    // --- helpers ------------------------------------------------------------

    private (GitHubWorkspaceRepositoryService Service, AppDbContext Context) NewService(
        FakeGitHubApi api, ALDevToolbox.Services.Tools.IToolAvailability? toolAvailability = null,
        ILogger<GitHubWorkspaceRepositoryService>? logger = null)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewGitHubWorkspaceRepositoryService(ctx, client, access, toolAvailability, logger), ctx);
    }

    private static ProjectPlan WorkspacePlan(string? shortName = null) => PlanBuilder.WorkspacePlan(
        workspaceName: "CRONUS Customer", shortName: shortName, extensionPrefix: "CRONUS");

    private static string BodyOf(FakeGitHubApi api, string method, string pathSuffix) =>
        api.Bodies
            .Where(b => b.Call.StartsWith(method, StringComparison.Ordinal) && b.Call.Contains(pathSuffix))
            .Select(b => b.Body)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"No {method} request to a path containing '{pathSuffix}' was made.");

    private static string? TokenFor(FakeGitHubApi api, string method, string pathSuffix) =>
        api.Credentials
            .Where(c => c.Call.StartsWith(method, StringComparison.Ordinal) && c.Call.Contains(pathSuffix))
            .Select(c => c.Token)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"No {method} request to a path containing '{pathSuffix}' was made.");

    /// <summary>
    /// A GitHub that answers every call the flow needs: the membership check,
    /// the installation token, the repository, the blobs, the tree, the commit
    /// and the branch the commit is pointed at.
    /// </summary>
    private static FakeGitHubApi WritableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NoContent)
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.Created,
                FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.Created, FakeGitHubApi.FileWriteJson())
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", HttpStatusCode.OK, FakeGitHubApi.ShaJson("new-commit-sha"))
            // Since #811 the default branch is created at a finished commit
            // rather than moved on to one, its seed lands on a throwaway branch
            // that is deleted afterwards, and the repository's default branch
            // setting is pointed at the real branch.
            .On(HttpMethod.Get, $"/repos/{Repo}/rules/branches/", HttpStatusCode.OK, FakeGitHubApi.BranchRulesJson())
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.RefJson("main"))
            .On(HttpMethod.Delete, $"/repos/{Repo}/git/refs/heads/", HttpStatusCode.NoContent)
            .On(HttpMethod.Patch, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo))
            // GitHub refuses the Git Data API until a repository has a commit,
            // which is what the Contents write above is for.
            .EmptyRepository(Repo);

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-dev-toolbox", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    /// <param name="permissions">
    /// What GitHub said the installation was granted, as the connect handshake
    /// records it. Null leaves the column empty, which is what an older
    /// connection looks like.
    /// </param>
    private async Task ConnectOrganisationAsync(string? permissions = null)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = InstallationId,
            GitHubOrgLogin = OrgLogin,
            GitHubInstallationPermissions = permissions,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task LinkAsync()
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson(UserToken))
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson(4711, "cronus-dev"));
        await using var ctx = _db.NewContext();
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api));
        await access.LinkAsync("the-code");
    }

    /// <summary>Deployment configured, organisation connected, user linked, and a template to generate from.</summary>
    private async Task ReadyAsync(string? permissions = null)
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync(permissions);
        await LinkAsync();
        await SeedTemplateAsync();
    }

    /// <summary>
    /// Seeds the template and joins it to the organisation's files, so the
    /// generated workspace has the same shape the download does - see
    /// <c>GitHubExtensionDeliveryTests</c>, which does the same.
    /// </summary>
    private async Task SeedTemplateAsync()
    {
        var template = TemplateBuilder.Default();
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();

        var orgFileIds = await ctx.OrganizationFiles
            .Where(f => f.OrganizationId == template.OrganizationId)
            .OrderBy(f => f.Ordering)
            .Select(f => f.Id)
            .ToListAsync();
        for (var i = 0; i < orgFileIds.Count; i++)
        {
            ctx.Set<RuntimeTemplateIncludedFile>().Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = template.OrganizationId,
                RuntimeTemplateId = template.Id,
                OrganizationFileId = orgFileIds[i],
                Ordering = i,
            });
        }
        if (orgFileIds.Count > 0)
        {
            await ctx.SaveChangesAsync();
        }
    }
}
