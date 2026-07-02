using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// Covers <see cref="AuditInterceptor"/>: created/updated/deleted rows produce
/// the right action, modified rows snapshot OriginalValues (not the new state),
/// principal entities inline their child collections, file content is hashed
/// rather than copied verbatim, and a "save with no real edits" doesn't write
/// audit rows.
/// </summary>
public sealed class AuditInterceptorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Adding_a_template_writes_a_created_row_with_no_snapshot()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-x"));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.ToListAsync();
        rows.Should().ContainSingle(r =>
            r.EntityType == AuditEntityType.RuntimeTemplate
            && r.Action == AuditAction.Created
            && r.SnapshotJson == null
            && r.ChangedBy == "admin");
    }

    [Fact]
    public async Task Modifying_a_template_writes_an_updated_row_with_original_values()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            template.Name = "Original Name";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var template = await ctx.RuntimeTemplates.FirstAsync(t => t.Id == templateId);
            template.Name = "New Name";
            template.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.RuntimeTemplate && r.Action == AuditAction.Updated)
            .SingleAsync();
        row.ChangedBy.Should().Be("alice");
        row.SnapshotJson.Should().NotBeNullOrEmpty();
        // The snapshot captures pre-save state — "Original Name", not "New Name".
        var snapshot = JsonDocument.Parse(row.SnapshotJson!);
        snapshot.RootElement.GetProperty("Name").GetString().Should().Be("Original Name");
    }

    [Fact]
    public async Task Modifying_only_updated_at_is_treated_as_a_no_op()
    {
        // Reconciliation services rewrite UpdatedAt unconditionally on save;
        // the interceptor must filter that case so admins don't see noise rows
        // every time they click Save without making real edits.
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var template = await ctx.RuntimeTemplates.FirstAsync(t => t.Id == templateId);
            template.UpdatedAt = template.UpdatedAt.AddSeconds(1);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync(r => r.Action == AuditAction.Updated))
            .Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_template_writes_a_deleted_row_with_pre_delete_snapshot()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            template.Name = "Doomed Template";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var template = await ctx.RuntimeTemplates.FirstAsync(t => t.Id == templateId);
            ctx.RuntimeTemplates.Remove(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.RuntimeTemplate && r.Action == AuditAction.Deleted)
            .SingleAsync();
        row.SnapshotJson.Should().NotBeNullOrEmpty();
        JsonDocument.Parse(row.SnapshotJson!)
            .RootElement.GetProperty("Name").GetString()
            .Should().Be("Doomed Template");
    }

    [Fact]
    public async Task Extension_snapshot_inlines_folders()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-ext-snapshot")
                .WithCoreFolder("Source", ("Hello.al", "codeunit 50000 H { }"))
                .WithCoreFolder("Translations");
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var template = await ctx.RuntimeTemplates
                .Include(t => t.WorkspaceExtensions)
                    .ThenInclude(e => e.Folders)
                        .ThenInclude(f => f.Files)
                .FirstAsync(t => t.Id == templateId);
            ctx.RuntimeTemplates.Remove(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.WorkspaceExtension && r.Action == AuditAction.Deleted)
            .SingleAsync();
        var snapshot = JsonDocument.Parse(row.SnapshotJson!).RootElement;
        snapshot.GetProperty("folders").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Workspace_extension_file_snapshot_hashes_content()
    {
        const string content = "codeunit 50000 LargeAlBody { trigger OnRun() begin Message('hi'); end; }";
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-hash")
                .WithCoreFolder("Source", ("Hello.al", content));
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var template = await ctx.RuntimeTemplates
                .Include(t => t.WorkspaceExtensions)
                    .ThenInclude(e => e.Folders)
                        .ThenInclude(f => f.Files)
                .FirstAsync(t => t.Id == templateId);
            ctx.RuntimeTemplates.Remove(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.WorkspaceExtensionFile && r.Action == AuditAction.Deleted)
            .SingleAsync();
        var snapshot = JsonDocument.Parse(row.SnapshotJson!).RootElement;
        snapshot.TryGetProperty("Content", out _).Should().BeFalse();
        snapshot.GetProperty("ContentSha256").GetString()
            .Should().MatchRegex("^[a-f0-9]{64}$");
    }

    [Fact]
    public async Task Module_snapshot_inlines_dependencies()
    {
        int moduleId;
        await using (var seed = _db.NewContext())
        {
            var module = ModuleBuilder.Default("alpha", "Alpha")
                .WithDependency("00000000-0000-0000-0000-000000000001", "Base App", "Microsoft", "24.0.0.0")
                .WithDependency("00000000-0000-0000-0000-000000000002", "System App", "Microsoft", "24.0.0.0");
            seed.Modules.Add(module);
            await seed.SaveChangesAsync();
            moduleId = module.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var module = await ctx.Modules.Include(m => m.Dependencies).FirstAsync(m => m.Id == moduleId);
            ctx.Modules.Remove(module);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.Module && r.Action == AuditAction.Deleted)
            .SingleAsync();
        var snapshot = JsonDocument.Parse(row.SnapshotJson!).RootElement;
        snapshot.GetProperty("dependencies").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Unauthenticated_save_records_unknown_actor()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor(name: null)))
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-anon"));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        // TemplateBuilder.Default seeds both the RuntimeTemplate row and its
        // required Core WorkspaceExtension (the unified-extensions model
        // requires at least one extension); pick the parent row to assert on.
        (await read.AuditLog.Where(r => r.EntityType == AuditEntityType.RuntimeTemplate).SingleAsync())
            .ChangedBy.Should().Be("unknown");
    }

    // ── SaaS-delivery: release pipelines are audited; projects only for their
    //    BC connection/secret (discovery-cache churn and creation are filtered out).

    [Fact]
    public async Task Changing_a_project_bc_connection_writes_an_updated_row()
    {
        int projectId;
        await using (var seed = _db.NewContext())
        {
            var p = new Project { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS A/S", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var p = await ctx.OeProjects.FirstAsync(x => x.Id == projectId);
            p.BcClientId = "client-123";
            p.BcClientSecretExpiresAt = DateTime.UtcNow.AddYears(1);
            p.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.Where(r => r.EntityType == AuditEntityType.Project && r.Action == AuditAction.Updated).SingleAsync())
            .ChangedBy.Should().Be("alice");
    }

    [Fact]
    public async Task Changing_a_project_bc_secret_redacts_it_in_the_snapshot()
    {
        int projectId;
        await using (var seed = _db.NewContext())
        {
            var p = new Project
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "CRONUS",
                BcClientSecretEncrypted = "cipher-old",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var p = await ctx.OeProjects.FirstAsync(x => x.Id == projectId);
            p.BcClientSecretEncrypted = "cipher-new";
            p.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.Project && r.Action == AuditAction.Updated)
            .SingleAsync();
        // The before-snapshot records that the secret changed, not the ciphertext.
        JsonDocument.Parse(row.SnapshotJson!).RootElement
            .GetProperty(nameof(Project.BcClientSecretEncrypted)).GetString()
            .Should().Be("[redacted]");
    }

    [Fact]
    public async Task Changing_only_project_discovery_cache_is_not_audited()
    {
        // The background ProjectDiscoveryWorker rewrites these columns with no HTTP
        // user; those writes must not flood the audit log with "unknown" churn.
        int projectId;
        await using (var seed = _db.NewContext())
        {
            var p = new Project { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor(name: null)))
        {
            var p = await ctx.OeProjects.FirstAsync(x => x.Id == projectId);
            p.DiscoveredExtensionsJson = "[]";
            p.DiscoveredAt = DateTime.UtcNow;
            p.DiscoveryError = null;
            p.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync(r => r.EntityType == AuditEntityType.Project)).Should().Be(0);
    }

    [Fact]
    public async Task Creating_a_project_is_not_audited()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            ctx.OeProjects.Add(new Project { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS new", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync(r => r.EntityType == AuditEntityType.Project)).Should().Be(0);
    }

    [Fact]
    public async Task Creating_a_release_pipeline_writes_a_created_row()
    {
        int projectId, pipelineId, envId;
        await using (var seed = _db.NewContext())
        {
            var now = DateTime.UtcNow;
            var p = new Project { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", CreatedAt = now, UpdatedAt = now };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;

            var pipe = new Pipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build", CreatedAt = now, UpdatedAt = now };
            seed.OePipelines.Add(pipe);
            await seed.SaveChangesAsync();
            pipelineId = pipe.Id;

            var env = new ProjectEnvironment { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Production", Type = "Production", FetchedAt = now };
            seed.OeProjectEnvironments.Add(env);
            await seed.SaveChangesAsync();
            envId = env.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            ctx.OeReleasePipelines.Add(new ReleasePipeline
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                Name = "CRONUS App → Production",
                BuildPipelineId = pipelineId,
                ProjectEnvironmentId = envId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.Where(r => r.EntityType == AuditEntityType.ReleasePipeline && r.Action == AuditAction.Created).SingleAsync())
            .ChangedBy.Should().Be("alice");
    }

    [Fact]
    public async Task Changing_a_user_redacts_the_password_hash_in_the_snapshot()
    {
        int userId;
        await using (var seed = _db.NewContext())
        {
            var u = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "user@cronus.example",
                DisplayName = "CRONUS User",
                PasswordHash = "$2a$11$oldhasholdhasholdhasholdhasholdhasholdhasholdhasholdha",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            };
            seed.Users.Add(u);
            await seed.SaveChangesAsync();
            userId = u.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var u = await ctx.Users.FirstAsync(x => x.Id == userId);
            u.PasswordHash = "$2a$11$newhashnewhashnewhashnewhashnewhashnewhashnewhashnewha";
            u.Role = UserRole.Editor;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.User && r.Action == AuditAction.Updated)
            .SingleAsync();
        // The before-snapshot records the role change but never the BCrypt hash —
        // it's offline-cracking material and must not reach org Admins. See #476.
        row.SnapshotJson.Should().NotContain("oldhash");
        JsonDocument.Parse(row.SnapshotJson!).RootElement
            .GetProperty(nameof(User.PasswordHash)).GetString()
            .Should().Be("[redacted]");
    }

    private async Task ClearAuditAsync()
    {
        await using var ctx = _db.NewContext();
        var rows = await ctx.AuditLog.ToListAsync();
        ctx.AuditLog.RemoveRange(rows);
        await ctx.SaveChangesAsync();
    }

    private static AuditInterceptor NewInterceptor(string? name)
    {
        var http = new HttpContextAccessor();
        if (name is not null)
        {
            var ctx = new DefaultHttpContext();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, "test"));
            http.HttpContext = ctx;
        }
        return new AuditInterceptor(http);
    }
}
