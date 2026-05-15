using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OeModule = ALDevToolbox.Domain.Entities.ObjectExplorer.Module;
using OeModuleObject = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleObject;
using OeModuleReference = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleReference;
using OeRelease = ALDevToolbox.Domain.Entities.ObjectExplorer.Release;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Smoke coverage for the new Object Explorer schema (PR 1 of the milestone).
/// Verifies the migration applies, the entities round-trip through EF, the
/// multi-tenant query filter scopes reads to the current org, and the
/// parent-Release self-FK refuses deletion of a parent that still has
/// children. Nothing here exercises the ingest pipeline or query service —
/// those land in later PRs.
/// </summary>
public sealed class ObjectExplorerSchemaSmokeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Release_module_object_reference_round_trip()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;

        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "BC 25.18",
            BcVersion = "25.18.48229.0",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972"),
            Name = "Base Application",
            Publisher = "Microsoft",
            Version = "25.18.48229.0",
            CreatedAt = now,
        };
        ctx.OeModules.Add(module);
        await ctx.SaveChangesAsync();

        var obj = new OeModuleObject
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = module.Id,
            Kind = "codeunit",
            ObjectId = 80,
            Name = "Sales-Post",
            LineNumber = 1,
        };
        ctx.OeModuleObjects.Add(obj);
        await ctx.SaveChangesAsync();

        var reference = new OeModuleReference
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = module.Id,
            SourceObjectId = obj.Id,
            TargetAppId = module.AppId,
            TargetObjectKind = "table",
            TargetObjectId = 36,
            TargetObjectName = "Sales Header",
            ReferenceKind = "variable_type",
        };
        ctx.OeModuleReferences.Add(reference);
        await ctx.SaveChangesAsync();

        // Round-trip read.
        await using var readCtx = _db.NewContext();
        var refs = await readCtx.OeModuleReferences
            .Where(r => r.TargetAppId == module.AppId && r.TargetObjectKind == "table" && r.TargetObjectId == 36)
            .ToListAsync();
        refs.Should().ContainSingle().Which.SourceObjectId.Should().Be(obj.Id);
    }

    [Fact]
    public async Task Parent_release_chain_is_walkable()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;

        var parent = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "BC 25.18",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleases.Add(parent);
        await ctx.SaveChangesAsync();

        var child = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Continia DC 6.5 on BC 25.18",
            Kind = "third_party",
            ParentReleaseId = parent.Id,
            Status = "ready",
            ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleases.Add(child);
        await ctx.SaveChangesAsync();

        await using var readCtx = _db.NewContext();
        var loaded = await readCtx.OeReleases.AsNoTracking().Include(r => r.ParentRelease)
            .SingleAsync(r => r.Id == child.Id);
        loaded.ParentRelease.Should().NotBeNull();
        loaded.ParentRelease!.Label.Should().Be("BC 25.18");
    }

    [Fact]
    public async Task Parent_release_deletion_is_restricted_while_child_exists()
    {
        // Seed parent + child via one context, then delete the parent via a
        // fresh context so EF can't fix up the tracked child's FK before issuing
        // the DELETE. The point of this test is the database constraint, not
        // EF's client-side relationship logic.
        int parentId;
        {
            await using var setupCtx = _db.NewContext();
            var now = DateTime.UtcNow;
            var parent = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = "BC 25.18", Kind = "first_party",
                Status = "ready", ImportedAt = now, CreatedAt = now, UpdatedAt = now,
            };
            var child = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = "Customer X on BC 25.18", Kind = "customer",
                ParentRelease = parent, Status = "ready", ImportedAt = now, CreatedAt = now, UpdatedAt = now,
            };
            setupCtx.OeReleases.AddRange(parent, child);
            await setupCtx.SaveChangesAsync();
            parentId = parent.Id;
        }

        await using var delCtx = _db.NewContext();
        var parentToDelete = await delCtx.OeReleases.FindAsync(parentId);
        parentToDelete.Should().NotBeNull();
        delCtx.OeReleases.Remove(parentToDelete!);

        var act = async () => await delCtx.SaveChangesAsync();
        // PostgreSQL refuses the DELETE with sqlstate 23503 (foreign_key_violation);
        // EF Core wraps it in DbUpdateException with a PostgresException inner.
        (await act.Should().ThrowAsync<DbUpdateException>(
                because: "ON DELETE RESTRICT refuses parent removal while child still references it"))
            .WithInnerException<Npgsql.PostgresException>()
            .Which.SqlState.Should().Be("23503");
    }

    [Fact]
    public async Task Module_uniqueness_is_per_release_app_id_version()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "BC 25.18", Kind = "first_party",
            Status = "ready", ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        var appId = Guid.NewGuid();
        ctx.OeModules.Add(new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId, ReleaseId = release.Id,
            AppId = appId, Name = "Foo", Publisher = "Microsoft", Version = "1.0.0.0",
            CreatedAt = now,
        });
        await ctx.SaveChangesAsync();

        // Same (release, appId, version) — should fail.
        ctx.OeModules.Add(new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId, ReleaseId = release.Id,
            AppId = appId, Name = "Foo dup", Publisher = "Microsoft", Version = "1.0.0.0",
            CreatedAt = now,
        });
        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();

        // Different version — should succeed (multi-version shadowing case).
        await using var ctx2 = _db.NewContext();
        ctx2.OeModules.Add(new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId, ReleaseId = release.Id,
            AppId = appId, Name = "Foo v2", Publisher = "Microsoft", Version = "2.0.0.0",
            CreatedAt = now,
        });
        await ctx2.SaveChangesAsync();
    }

    [Fact]
    public async Task Tenant_filter_scopes_releases_to_current_org()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        ctx.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "Default org release", Kind = "first_party",
            Status = "ready", ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        });
        ctx.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.OtherOrgId, Label = "Other org release", Kind = "first_party",
            Status = "ready", ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();

        // OrgContext defaults to DefaultOrgId — should see only that org's row.
        await using var readCtx = _db.NewContext();
        var visible = await readCtx.OeReleases.AsNoTracking().ToListAsync();
        visible.Should().ContainSingle().Which.Label.Should().Be("Default org release");
    }
}
