using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
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

    // TODO Issue #54 follow-up: re-add the inline-snapshot and content-hash
    // tests once the audit interceptor surfaces WorkspaceExtension /
    // WorkspaceExtensionFolder / WorkspaceExtensionFile / ModuleExtensionFolder /
    // ModuleExtensionFile rows. The interceptor already targets the new types
    // (AuditInterceptor.AuditedTypes) but the tests' folder fixtures need to
    // be reworked around the recursive folder tree.

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
        (await read.AuditLog.SingleAsync()).ChangedBy.Should().Be("unknown");
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
