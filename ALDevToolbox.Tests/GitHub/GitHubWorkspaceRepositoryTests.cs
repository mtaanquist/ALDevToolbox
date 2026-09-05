using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

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
        TokenFor(api, "PATCH", $"/repos/{Repo}/git/refs/heads/").Should().Be(InstallationToken);
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
        // On top of the seed commit, which is the only way a repository created
        // empty can have a tree at all - see the note on CommitAsync.
        commit.Should().Contain("\"parents\":[\"seed-commit-sha\"]");
        commit.Should().Contain("Add the CRONUS Customer workspace");
        // Credited to whoever asked for it, not to the app that made the call.
        // (The + in a GitHub noreply address comes back JSON-escaped.)
        commit.Should().Contain("cronus-dev@users.noreply.github.com");
        commit.Should().Contain("\"name\":\"cronus-dev\"");

        // The seed write put the branch there; this moves it on to the commit
        // that carries the whole workspace.
        api.Calls.Should().Contain(c => c.Contains("PATCH") && c.Contains("/git/refs/heads/main"));
        BodyOf(api, "PATCH", "/git/refs/heads/main").Should().Contain("\"sha\":\"new-commit-sha\"");
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
        var firstWrite = api.Calls.First(c => c.Contains($"/repos/{Repo}/"));
        firstWrite.Should().StartWith("PUT").And.Contain("/contents/");

        // Seeded with the README: it is what GitHub itself would have put in an
        // initial commit, and it is a file the generator produced rather than
        // one auto-init invented.
        firstWrite.Should().Contain("/contents/README.md");
        BodyOf(api, "PUT", "/contents/").Should().Contain("\"branch\":\"main\"");
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
            WorkspacePlan() with { WorkspaceName = "9 Lives" }, RepoName, isPrivate: true);

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
    // The shape people actually name repositories, from the shape they name
    // workspaces. Only a suggestion - the field is editable.
    [InlineData("CRONUS Customer", "CRONUS-Customer")]
    [InlineData("CRONUS A/S", "CRONUS-A-S")]
    [InlineData("CRONUS  Customer", "CRONUS-Customer")]
    [InlineData("  CRONUS  ", "CRONUS")]
    [InlineData("", "")]
    public void The_suggested_name_is_one_github_would_keep(string workspaceName, string expected)
    {
        GitHubWorkspaceRepositoryService.SuggestName(workspaceName).Should().Be(expected);
    }

    // --- helpers ------------------------------------------------------------

    private (GitHubWorkspaceRepositoryService Service, AppDbContext Context) NewService(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewGitHubWorkspaceRepositoryService(ctx, client, access), ctx);
    }

    private static ProjectPlan WorkspacePlan() => PlanBuilder.WorkspacePlan(
        workspaceName: "CRONUS Customer", extensionPrefix: "CRONUS");

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
