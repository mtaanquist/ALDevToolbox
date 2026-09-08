using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The GitHub half of <c>/templates/workspace</c> once the button has been
/// pressed: what the success state says.
///
/// <para>The rule being pinned is the one the customer-naming milestone added
/// (issue #759): creating the repository is also what registers the customer as
/// a solution, so the card has to say which solution that was and link to it.
/// Without the link the consultant has just made two things and been told about
/// one of them.</para>
/// </summary>
public sealed class NewWorkspaceRepositoryTests : IDisposable
{
    private const int UserId = 861;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoName = "cronus-customer";
    private const string Repo = $"{OrgLogin}/{RepoName}";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly FakeGitHubApi _api = WritableApi();

    public NewWorkspaceRepositoryTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("dev@cronus.example");
        // The page hands off to generate.js on submit; there is no browser here.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<WorkspaceConfigService>();
        _ctx.Services.AddSingleton<MustacheRenderer>();
        _ctx.Services.AddScoped<WorkspaceZipBuilder>();
        _ctx.Services.AddScoped<GenerationService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectAccess>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryQueue());
        _db.AddGitHubServices(_ctx.Services, _api);
        _ctx.Services.AddDataProtection();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "dev@cronus.example",
            DisplayName = "dev@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_success_card_links_to_the_solution_the_customer_became()
    {
        await ReadyAsync();

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']").Input("CRONUS Customer");
        cut.WaitForElement("button:contains('Create repository')").Click();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".ws-repo");
            card.TextContent.Should().Contain(Repo);
            // Created rather than picked, and reachable from here - a customer
            // registered somewhere the consultant cannot get to is half a
            // feature.
            card.TextContent.Should().Contain("Created the solution");
            card.TextContent.Should().Contain("CRONUS Customer");
            card.InnerHtml.Should().Contain("/solutions/");
        }, TimeSpan.FromSeconds(10));

        await using var read = _db.NewContext();
        var solution = await read.OeProjects.AsNoTracking()
            .Include(p => p.Repositories)
            .SingleAsync(p => p.Name == "CRONUS Customer");
        cut.Find(".ws-repo").InnerHtml.Should().Contain($"/solutions/{solution.Id}");
        solution.Repositories.Should().ContainSingle()
            .Which.Url.Should().Be($"https://github.com/{Repo}.git");
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// A GitHub that answers every call creating and filling a repository
    /// makes, mirroring <c>GitHubWorkspaceRepositoryTests</c>.
    /// </summary>
    private static FakeGitHubApi WritableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson())
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NoContent)
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.Created,
                FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.Created, FakeGitHubApi.FileWriteJson())
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", HttpStatusCode.OK, FakeGitHubApi.ShaJson("new-commit-sha"))
            .EmptyRepository(Repo);

    /// <summary>Deployment configured, organisation connected, user linked, one template.</summary>
    private async Task ReadyAsync()
    {
        using (var rsa = RSA.Create(2048))
        {
            await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
                AppId: "123456", AppSlug: "al-dev-toolbox", ClientId: "Iv1.cronus",
                ClientSecret: "s3cr3t", ClearClientSecret: false,
                PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
        }

        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = OrgLogin,
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, _api)).LinkAsync("the-code");
        }

        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default());
            await ctx.SaveChangesAsync();
        }
    }
}
