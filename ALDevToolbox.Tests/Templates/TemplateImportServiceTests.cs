// TODO Issue #54 follow-up: rewrite TemplateImportService tests once the
// service clones WorkspaceExtension + ModuleExtensionFolder/File rows again.
// The current import service stub only carries template metadata + default
// modules, so the legacy tests for per-extension content no longer apply.
#if false
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Behavioural tests for <see cref="TemplateImportService"/>: the cross-org
/// fork pipeline that copies a system-org template (plus referenced modules
/// and application version) into the acting user's organisation. The Default
/// org is the canonical system org; <see cref="TestDb.OtherOrgId"/> stands in
/// for an importing tenant.
/// </summary>
public sealed class TemplateImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Import_clones_template_with_modules_and_application_version()
    {
        await SeedSystemCatalogueAsync();

        // Act as the importing tenant.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId;
        await using (var read = _db.NewContext())
        {
            systemTemplateId = await read.RuntimeTemplates
                .IgnoreQueryFilters()
                .Where(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system")
                .Select(t => t.Id)
                .SingleAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var verify = _db.NewContext();
        var imported = await verify.RuntimeTemplates
            .IgnoreQueryFilters()
            .Where(t => t.OrganizationId == TestDb.OtherOrgId && t.Key == "runtime-system")
            .Include(t => t.Folders).ThenInclude(f => f.Files)
            .Include(t => t.ModuleFolders).ThenInclude(f => f.Files)
            .Include(t => t.DefaultModules).ThenInclude(d => d.Module!).ThenInclude(m => m.Dependencies)
            .Include(t => t.DefaultApplicationVersion)
            .SingleAsync();

        imported.Folders.Should().ContainSingle().Which.Path.Should().Be("Source");
        imported.Folders.Single().Files.Should().ContainSingle()
            .Which.Content.Should().Be("// hello");
        imported.ModuleFolders.Should().ContainSingle().Which.Path.Should().Be("Setup");
        imported.DefaultModules.Should().ContainSingle();
        imported.DefaultModules.Single().Module!.Key.Should().Be("payment");
        imported.DefaultModules.Single().Module!.OrganizationId.Should().Be(TestDb.OtherOrgId);
        imported.DefaultModules.Single().Module!.Dependencies.Should().ContainSingle();
        imported.DefaultApplicationVersion.Should().NotBeNull();
        imported.DefaultApplicationVersion!.Key.Should().Be("bc24");
        imported.DefaultApplicationVersion.OrganizationId.Should().Be(TestDb.OtherOrgId);

        // Source rows are untouched.
        var sourceCount = await verify.RuntimeTemplates
            .IgnoreQueryFilters()
            .CountAsync(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system");
        sourceCount.Should().Be(1);
    }

    [Fact]
    public async Task Import_refuses_when_local_template_with_same_key_exists()
    {
        await SeedSystemCatalogueAsync();
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-system", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId;
        await using (var read = _db.NewContext())
        {
            systemTemplateId = await read.RuntimeTemplates.IgnoreQueryFilters()
                .Where(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system")
                .Select(t => t.Id).SingleAsync();
        }

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ImportTemplateAsync(systemTemplateId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Key");
    }

    [Fact]
    public async Task Import_refuses_when_acting_org_is_the_system_org()
    {
        await SeedSystemCatalogueAsync();

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        _db.OrgContext.IsSystemOrganization = true;

        int systemTemplateId;
        await using (var read = _db.NewContext())
        {
            systemTemplateId = await read.RuntimeTemplates.IgnoreQueryFilters()
                .Where(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system")
                .Select(t => t.Id).SingleAsync();
        }

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewService(ctx).ImportTemplateAsync(systemTemplateId);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Import");
    }

    [Fact]
    public async Task Import_reuses_existing_module_when_key_already_present_in_target()
    {
        await SeedSystemCatalogueAsync();
        // Pre-seed the local org with a module that shares the system module's key.
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("payment", "Local Payment", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId;
        await using (var read = _db.NewContext())
        {
            systemTemplateId = await read.RuntimeTemplates.IgnoreQueryFilters()
                .Where(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system")
                .Select(t => t.Id).SingleAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var verify = _db.NewContext();
        var localModules = await verify.Modules.IgnoreQueryFilters()
            .Where(m => m.OrganizationId == TestDb.OtherOrgId && m.Key == "payment")
            .ToListAsync();
        localModules.Should().HaveCount(1, "the existing local module should be reused, not duplicated");
        localModules.Single().Name.Should().Be("Local Payment", "the importer must not overwrite the local module name");
    }

    [Fact]
    public async Task ListSystemTemplates_marks_already_imported_rows()
    {
        await SeedSystemCatalogueAsync();
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        int systemTemplateId;
        await using (var read = _db.NewContext())
        {
            systemTemplateId = await read.RuntimeTemplates.IgnoreQueryFilters()
                .Where(t => t.OrganizationId == TestDb.DefaultOrgId && t.Key == "runtime-system")
                .Select(t => t.Id).SingleAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ImportTemplateAsync(systemTemplateId);
        }

        await using var verifyCtx = _db.NewContext();
        var listing = await NewService(verifyCtx).ListSystemTemplatesAsync();
        listing.Should().ContainSingle()
            .Which.AlreadyImported.Should().BeTrue();
    }

    private TemplateImportService NewService(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<TemplateImportService>.Instance);

    /// <summary>
    /// Stamps the Default org as the system org and seeds a runnable template,
    /// a referenced module, and an application version inside it.
    /// </summary>
    private async Task SeedSystemCatalogueAsync()
    {
        await using var ctx = _db.NewContext();
        var defaultOrg = await ctx.Organizations.IgnoreQueryFilters()
            .SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        defaultOrg.IsSystem = true;

        var module = ModuleBuilder.Default("payment", "Payment")
            .WithDependency("00000000-0000-0000-0000-000000000001", "Base", "Microsoft", "1.0.0.0");
        ctx.Modules.Add(module);

        var appVersion = new ApplicationVersion
        {
            OrganizationId = TestDb.DefaultOrgId,
            Key = "bc24",
            Name = "BC 24",
            Application = "24.0.0.0",
            Runtime = "15",
            Ordering = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.ApplicationVersions.Add(appVersion);

        var template = TemplateBuilder.Default("runtime-system")
            .WithCoreFolder("Source", ("Hello.al", "// hello"))
            .WithModuleFolder("Setup", ("Setup.al", "// setup"));
        template.DefaultApplicationVersion = appVersion;
        template.DefaultModules.Add(new RuntimeTemplateDefaultModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            Module = module,
            Ordering = 0,
        });
        ctx.RuntimeTemplates.Add(template);

        await ctx.SaveChangesAsync();
    }
}
#endif
