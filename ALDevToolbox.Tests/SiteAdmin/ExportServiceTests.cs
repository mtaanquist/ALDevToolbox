using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Smoke coverage for <see cref="ExportService"/>: ZIP layout (templates
/// under their key, modules under <c>modules/</c>, application versions
/// under <c>application-versions/</c>, catalogue at
/// <c>catalog/well-known-deps.toml</c>) and soft-deleted rows are excluded
/// from the archive. The org-config block is covered separately in
/// <see cref="ALDevToolbox.Tests.Configuration.ConfigExportImportRoundTripTests"/>.
/// </summary>
public sealed class ExportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ExportAllAsync_writes_active_rows_under_expected_paths()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-active"));
            seed.Modules.Add(ModuleBuilder.Default("mod-active"));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000099", "Base"));
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc24", Name = "BC 24",
                Application = "24.0.0.0", Runtime = "15.0",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var entries = await ExportAndListEntriesAsync();

        entries.Should().Contain("runtime-active/template.toml");
        entries.Should().Contain("modules/mod-active.toml");
        entries.Should().Contain("application-versions/bc24.toml");
        entries.Should().Contain("catalog/well-known-deps.toml");
    }

    [Fact]
    public async Task ExportAllAsync_excludes_soft_deleted_rows()
    {
        await using (var seed = _db.NewContext())
        {
            var deletedTemplate = TemplateBuilder.Default("runtime-deleted");
            deletedTemplate.DeletedAt = DateTime.UtcNow;
            seed.RuntimeTemplates.Add(deletedTemplate);

            var deletedModule = ModuleBuilder.Default("mod-deleted");
            deletedModule.DeletedAt = DateTime.UtcNow;
            seed.Modules.Add(deletedModule);

            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc-deleted", Name = "BC Deleted",
                Application = "20.0.0.0", Runtime = "11.0",
                DeletedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var entries = await ExportAndListEntriesAsync();

        entries.Should().NotContain("runtime-deleted/template.toml");
        entries.Should().NotContain("modules/mod-deleted.toml");
        entries.Should().NotContain("application-versions/bc-deleted.toml");
    }

    [Fact]
    public async Task ExportAllAsync_excludes_rows_from_other_orgs()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-mine"));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-theirs",
                organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var entries = await ExportAndListEntriesAsync();

        entries.Should().Contain("runtime-mine/template.toml");
        entries.Should().NotContain("runtime-theirs/template.toml");
    }

    [Fact]
    public async Task ExportAllAsync_template_toml_round_trips_through_the_mapper()
    {
        // Use the same TemplateTomlMapper the admin TOML editor uses, so a
        // freshly-exported template parses back into an equivalent model.
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-rt"));
            await seed.SaveChangesAsync();
        }

        string templateToml;
        await using (var ctx = _db.NewContext())
        {
            var svc = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
            var archive = await svc.ExportAllAsync();
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("runtime-rt/template.toml");
            entry.Should().NotBeNull();
            using var reader = new StreamReader(entry!.Open());
            templateToml = await reader.ReadToEndAsync();
        }

        var parsed = TemplateTomlMapper.FromToml(templateToml, deprecated: false);
        parsed.Key.Should().Be("runtime-rt");
    }

    private async Task<List<string>> ExportAndListEntriesAsync()
    {
        await using var ctx = _db.NewContext();
        var svc = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
        var archive = await svc.ExportAllAsync();
        using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }
}
