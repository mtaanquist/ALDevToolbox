using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// CRUD + validation contract for <see cref="ProjectService"/>: a project and
/// its repositories round-trip, validation rejects blank names, duplicate names,
/// and provider/URL mismatches with field-keyed errors, update replaces the repo
/// set, soft-delete hides the row, the creator is stamped as owner, and the org
/// query filter keeps projects from other orgs invisible.
/// </summary>
public sealed class ProjectServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    /// <summary>The acting user — seeded so the owner FK holds and the creator owns what they create.</summary>
    private const int OwnerUserId = 9100;

    public ProjectServiceTests()
    {
        // Run the tests as a real, signed-in user so CreateProjectAsync stamps a
        // valid owner and the owner-or-admin gate on update/delete is satisfied.
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Shared discovery queue so a test can assert that a repo change warmed the cache (enqueued a discovery).</summary>
    private readonly ProjectDiscoveryQueue _discoveryQueue = new();

    /// <summary>A <see cref="ProjectService"/> wired with the shared org context, its access gate, and the discovery surface.</summary>
    private ProjectService Svc(ALDevToolbox.Data.AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var discovery = new ProjectDiscoveryService(
            ctx, _db.OrgContext, access, _discoveryQueue, NullLogger<ProjectDiscoveryService>.Instance);
        return new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance);
    }

    private static ProjectInput NewInput(
        string name = "Acme",
        string? country = "dk",
        params ProjectRepositoryInput[] repos)
        => new(name, country, repos.Length == 0
            ? new[] { new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core", "Core") }
            : repos);

    [Fact]
    public async Task Create_persists_project_and_repositories()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput(
            "Acme A/S", "dk",
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core", "Core"),
            new ProjectRepositoryInput(RepositoryProvider.AzureDevOps, "https://dev.azure.com/acme/bc/_git/exts", "Exts")));

        var loaded = await svc.GetProjectAsync(id);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Acme A/S");
        loaded.DefaultArtifactCountry.Should().Be("dk");
        loaded.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        loaded.Repositories.Should().HaveCount(2);
        loaded.Repositories.Should().Contain(r => r.Provider == RepositoryProvider.GitHub && r.DisplayName == "Core");
        loaded.Repositories.Should().OnlyContain(r => r.OrganizationId == TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task Create_defaults_display_name_to_repo_slug_when_blank()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput("Acme", null,
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core.git", "")));

        var loaded = await svc.GetProjectAsync(id);
        loaded!.Repositories.Single().DisplayName.Should().Be("core");
        loaded.DefaultArtifactCountry.Should().BeNull("blank country is stored as null");
    }

    [Fact]
    public async Task Create_rejects_blank_name()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var act = () => svc.CreateProjectAsync(NewInput(name: "   "));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Theory]
    [InlineData(RepositoryProvider.AzureDevOps, "https://github.com/acme/core")]
    [InlineData(RepositoryProvider.GitHub, "https://dev.azure.com/acme/bc/_git/core")]
    [InlineData(RepositoryProvider.GitHub, "http://github.com/acme/core")] // not https
    [InlineData(RepositoryProvider.GitHub, "not-a-url")]
    public async Task Create_rejects_provider_url_mismatch(RepositoryProvider provider, string url)
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var act = () => svc.CreateProjectAsync(NewInput("Acme", "dk",
            new ProjectRepositoryInput(provider, url, "Repo")));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Repositories[0].Url");
    }

    [Fact]
    public async Task Create_rejects_duplicate_active_name()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.CreateProjectAsync(NewInput("Acme"));

        var act = () => svc.CreateProjectAsync(NewInput("acme")); // case-insensitive clash

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Create_stamps_the_acting_user_as_owner()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S"));

        (await svc.GetProjectAsync(id))!.CreatedByUserId.Should().Be(OwnerUserId);
    }

    [Fact]
    public async Task Update_and_delete_are_blocked_for_a_non_owner_non_admin()
    {
        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(NewInput("CRONUS A/S"));

        // A different signed-in user who is neither the owner nor an Admin.
        const int strangerId = 9200;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = strangerId,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "stranger@example.com",
                PasswordHash = "x",
                DisplayName = "Stranger",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = strangerId;
        try
        {
            await using var ctx2 = _db.NewContext();
            var svc = Svc(ctx2);

            var update = () => svc.UpdateProjectAsync(id, NewInput("CRONUS A/S"));
            await update.Should().ThrowAsync<ProjectAccessDeniedException>();

            var delete = () => svc.SoftDeleteProjectAsync(id);
            await delete.Should().ThrowAsync<ProjectAccessDeniedException>();
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }

    [Fact]
    public async Task An_org_admin_can_manage_a_project_they_do_not_own()
    {
        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(NewInput("CRONUS A/S"));

        const int adminId = 9300;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = adminId,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "admin@example.com",
                PasswordHash = "x",
                DisplayName = "Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = adminId;
        try
        {
            await using var ctx2 = _db.NewContext();
            var svc = Svc(ctx2);

            var update = () => svc.UpdateProjectAsync(id, NewInput("CRONUS Renamed"));
            await update.Should().NotThrowAsync();
            (await svc.CanManageAsync(id)).Should().BeTrue();
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }

    [Fact]
    public async Task Update_replaces_repository_set()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme", "dk",
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/old", "Old")));

        await svc.UpdateProjectAsync(id, new ProjectInput("Acme Renamed", "w1", new[]
        {
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/new", "New"),
        }));

        var loaded = await svc.GetProjectAsync(id);
        loaded!.Name.Should().Be("Acme Renamed");
        loaded.DefaultArtifactCountry.Should().Be("w1");
        loaded.Repositories.Should().ContainSingle().Which.DisplayName.Should().Be("New");

        await using var verify = _db.NewContext();
        var orphanRepos = await verify.OeProjectRepositories.CountAsync(r => r.Url.Contains("old"));
        orphanRepos.Should().Be(0, "the replaced repo rows are removed, not left dangling");
    }

    [Fact]
    public async Task SoftDelete_hides_project_from_list_and_frees_the_name()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        await svc.SoftDeleteProjectAsync(id);

        (await svc.ListProjectsAsync()).Should().BeEmpty();
        (await svc.GetProjectAsync(id)).Should().BeNull();
        // The soft-delete filter on the unique index frees the name for reuse.
        var act = () => svc.CreateProjectAsync(NewInput("Acme"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListProjectReleases_returns_releases_linked_via_import_jobs()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var projectId = await svc.CreateProjectAsync(NewInput("Acme"));

        // A project release + the import job that links it back to the project.
        await using (var seed = _db.NewContext())
        {
            var rel = new Release
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "Acme on BC 26.0",
                Kind = "project",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleases.Add(rel);
            await seed.SaveChangesAsync();
            seed.OeImportJobs.Add(new ImportJob
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = rel.Id,
                ProjectId = projectId,
                Kind = "project_build",
                Status = "completed",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var releases = await svc.ListProjectReleasesAsync(projectId);
        releases.Should().ContainSingle().Which.Label.Should().Be("Acme on BC 26.0");
    }

    [Fact]
    public async Task AddSupplementalSymbols_persists_and_lists_with_size()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        await svc.AddSupplementalSymbolsAsync(id, new[]
        {
            new SupplementalSymbolUpload("Continia_Document Capture_12.0.0.0.app", new byte[] { 1, 2, 3, 4 }),
        });

        var rows = await svc.ListSupplementalSymbolsAsync(id);
        rows.Should().ContainSingle();
        rows[0].FileName.Should().Be("Continia_Document Capture_12.0.0.0.app");
        rows[0].ContentLength.Should().Be(4);
    }

    [Fact]
    public async Task AddSupplementalSymbols_replaces_same_named_package()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        await svc.AddSupplementalSymbolsAsync(id, new[] { new SupplementalSymbolUpload("Dep.app", new byte[] { 1 }) });
        await svc.AddSupplementalSymbolsAsync(id, new[] { new SupplementalSymbolUpload("Dep.app", new byte[] { 9, 9, 9 }) });

        var rows = await svc.ListSupplementalSymbolsAsync(id);
        rows.Should().ContainSingle("a re-upload of the same name replaces, not duplicates");
        rows[0].ContentLength.Should().Be(3, "the latest upload wins");
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("")]
    public async Task AddSupplementalSymbols_rejects_non_app(string fileName)
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        var act = () => svc.AddSupplementalSymbolsAsync(id, new[] { new SupplementalSymbolUpload(fileName, new byte[] { 1 }) });

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Symbols");
    }

    [Fact]
    public async Task AddSupplementalSymbols_rejects_empty_upload_list()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        var act = () => svc.AddSupplementalSymbolsAsync(id, Array.Empty<SupplementalSymbolUpload>());

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Symbols");
    }

    [Fact]
    public async Task DeleteSupplementalSymbol_removes_only_the_targeted_row()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));
        await svc.AddSupplementalSymbolsAsync(id, new[]
        {
            new SupplementalSymbolUpload("A.app", new byte[] { 1 }),
            new SupplementalSymbolUpload("B.app", new byte[] { 2 }),
        });
        var rows = await svc.ListSupplementalSymbolsAsync(id);

        await svc.DeleteSupplementalSymbolAsync(id, rows.Single(r => r.FileName == "A.app").Id);

        (await svc.ListSupplementalSymbolsAsync(id)).Should().ContainSingle().Which.FileName.Should().Be("B.app");
    }

    [Fact]
    public async Task Create_with_repos_warms_the_discovery_cache()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput("Acme")); // NewInput seeds one repo

        _discoveryQueue.IsInFlight(id).Should().BeTrue("a project created with repositories warms its discovery cache in the background");
    }

    [Fact]
    public async Task Update_with_repos_warms_the_discovery_cache()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));
        // The create already enqueued; clear it so we observe the update's own warm.
        _discoveryQueue.Complete(id);

        await svc.UpdateProjectAsync(id, new ProjectInput("Acme", "dk", new[]
        {
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/new", "New"),
        }));

        _discoveryQueue.IsInFlight(id).Should().BeTrue("changing the repo set re-warms the discovery cache");
    }

    [Fact]
    public async Task Projects_from_another_org_are_invisible()
    {
        // Insert a project owned by the other org directly (write filters don't
        // apply); the org-scoped read must not surface it.
        await using (var seed = _db.NewContext())
        {
            seed.OeProjects.Add(new Project
            {
                OrganizationId = TestDb.OtherOrgId,
                Name = "Other Co",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        (await svc.ListProjectsAsync()).Should().BeEmpty("the query filter scopes to the acting org");
    }
}
