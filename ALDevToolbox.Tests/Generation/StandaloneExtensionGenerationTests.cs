using System.IO.Compression;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Standalone New Extension flow through
/// <see cref="GenerationService.GenerateExtensionAsync"/>. Pins the split that
/// issue #520 asked for: a spaced extension name is kept verbatim in
/// <c>app.json</c>'s <c>name</c>, while the folder name strips the spaces
/// (via <see cref="ALDevToolbox.Services.Generation.GenerationNaming.StripWhitespace"/>).
/// </summary>
public sealed class StandaloneExtensionGenerationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Spaced_extension_name_keeps_spaces_in_app_json_but_strips_them_from_the_folder()
    {
        await SeedTemplateAsync(TemplateBuilder.Default());

        using var zip = await GenerateExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"));

        // Folder name strips spaces: the ZIP roots the extension at
        // "MyCustomFeature/…", not "My Custom Feature/…".
        var appJsonEntry = zip.GetEntry("MyCustomFeature/app.json");
        appJsonEntry.Should().NotBeNull("the folder name should strip spaces from the extension name");
        zip.GetEntry("My Custom Feature/app.json").Should().BeNull(
            "the spaced name must not leak into the on-disk folder path");

        // Display name keeps the spaces: app.json "name" is the raw input.
        var appJson = JsonDocument.Parse(ReadEntry(appJsonEntry!));
        appJson.RootElement.GetProperty("name").GetString().Should().Be("My Custom Feature",
            "app.json is where spaces are wanted (issue #520)");
    }

    // ===== helpers =====

    private GenerationService NewService()
    {
        var ctx = _db.NewContext();
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        return new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
    }

    private async Task<ZipArchive> GenerateExtensionAsync(ALDevToolbox.Domain.ValueObjects.StandaloneExtensionPlan plan)
    {
        var archive = await NewService().GenerateExtensionAsync(plan);
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    /// <summary>
    /// Seeds the template and joins it to the Default org's files so the
    /// per-extension <c>app.json</c> is emitted — same shape as
    /// <c>WorkspaceGenerationTests.SeedTemplateAsync</c>.
    /// </summary>
    private async Task SeedTemplateAsync(RuntimeTemplate template)
    {
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

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
