using System.IO.Compression;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translations;

/// <summary>
/// End-to-end coverage for <see cref="TranslationImportService"/>: the
/// single-file admin path, the per-release ZIP path, and the
/// clobber-on-re-upload semantics the user requested. Symbol-resolution
/// coverage is light because the FTU Core XLIFF refers to objects that
/// don't live in our DK Core / OIOUBL fixtures — that's fine; the
/// resolver gracefully drops to symbol_id=null and the rows still land
/// for search.
/// </summary>
public sealed class TranslationImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, NullLogger<TranslationImportService>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    private TranslationImportService NewTranslator(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<TranslationImportService>.Instance);

    /// <summary>
    /// Seeds a release that contains a module named "FTU Core" so the
    /// XLIFF upload's <c>&lt;file original&gt;</c> attribute matches. We
    /// borrow the DK Core .app as the carrier (its real name is "DK Core")
    /// and rename the module row after import; the symbol resolver works
    /// against the post-rename module, but the FTU XLIFF's hint names
    /// don't overlap with DK Core's symbols anyway.
    /// </summary>
    private async Task<(int ReleaseId, long ModuleId)> SeedFtuCoreShellAsync()
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewImporter(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
                Label: "Translation fixture release",
                Kind: "first_party",
                ParentReleaseId: null, ApplicationVersionId: null,
                Uploads: new[] { new AppFileUpload("Microsoft_DK_Core.app", s, null) }));
            releaseId = summary.ReleaseId;
        }

        long moduleId;
        await using (var ctx = _db.NewContext())
        {
            // Rename the module so the XLIFF's <file original="FTU Core">
            // matches our module name. Avoids cooking a second .app fixture.
            var mod = await ctx.OeModules.SingleAsync(m => m.ReleaseId == releaseId);
            mod.Name = "FTU Core";
            await ctx.SaveChangesAsync();
            moduleId = mod.Id;
        }

        return (releaseId, moduleId);
    }

    [Fact]
    public async Task ImportSingleAsync_persists_trans_units_with_normalised_language()
    {
        var (releaseId, moduleId) = await SeedFtuCoreShellAsync();

        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
            var summary = await svc.ImportSingleAsync(releaseId, moduleId, s, "FTU_Core.daDK.xlf");

            summary.LanguageCode.Should().Be("da-DK");
            summary.ModuleName.Should().Be("FTU Core");
            summary.Inserted.Should().BeGreaterThan(100);
        }

        await using (var read = _db.NewContext())
        {
            var rowCount = await read.OeModuleTranslations
                .CountAsync(t => t.ModuleId == moduleId && t.LanguageCode == "da-DK");
            rowCount.Should().BeGreaterThan(100);

            // Spot-check one resolved row to prove the developer-note
            // parser landed structured fields on the row, not just the
            // raw text.
            var sample = await read.OeModuleTranslations
                .FirstAsync(t => t.ModuleId == moduleId
                    && t.LanguageCode == "da-DK"
                    && t.SubName == "Activate Assembly On Service");
            sample.ObjectKind.Should().Be("table");
            sample.ObjectName.Should().Be("AppSetup");
            sample.SubKind.Should().Be("field");
            sample.PropertyName.Should().Be("Caption");
            sample.Kind.Should().Be("caption");
            sample.TargetText.Should().Contain("montageordrer");
        }
    }

    [Fact]
    public async Task ImportSingleAsync_re_upload_clobbers_previous_rows()
    {
        var (releaseId, moduleId) = await SeedFtuCoreShellAsync();

        int firstCount;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
            var summary = await svc.ImportSingleAsync(releaseId, moduleId, s, "FTU_Core.daDK.xlf");
            firstCount = summary.Inserted;
        }

        int secondCount;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
            var summary = await svc.ImportSingleAsync(releaseId, moduleId, s, "FTU_Core.daDK.xlf");
            secondCount = summary.Inserted;
        }

        secondCount.Should().Be(firstCount,
            because: "the importer should DELETE existing rows for (module, language) before re-inserting");

        await using (var read = _db.NewContext())
        {
            var rowCount = await read.OeModuleTranslations
                .CountAsync(t => t.ModuleId == moduleId && t.LanguageCode == "da-DK");
            rowCount.Should().Be(secondCount,
                because: "after a clobber the table holds exactly one copy, not two");
        }
    }

    [Fact]
    public async Task ImportSingleAsync_rejects_module_not_in_release()
    {
        var (releaseId, _) = await SeedFtuCoreShellAsync();
        const long missingModuleId = 99999;

        await using var ctx = _db.NewContext();
        var svc = NewTranslator(ctx);
        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));

        Func<Task> act = () => svc.ImportSingleAsync(releaseId, missingModuleId, s, "x.xlf");
        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task ImportZipAsync_matches_modules_by_file_original_attribute()
    {
        var (releaseId, moduleId) = await SeedFtuCoreShellAsync();

        // Pack the sample XLIFF into a ZIP plus a deliberately-unmatched
        // entry so we cover both the matched count and the
        // UnmatchedFiles list.
        await using var zipBuffer = new MemoryStream();
        using (var archive = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var matched = archive.CreateEntry("FTU_Core.daDK.xlf");
            await using (var entryStream = matched.Open())
            await using (var fs = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf")))
            {
                await fs.CopyToAsync(entryStream);
            }

            var unmatched = archive.CreateEntry("NotARealModule.daDK.xlf");
            await using (var entryStream = unmatched.Open())
            {
                var bogus =
                    """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
                      <file source-language="en-US" target-language="da-DK" original="No Such Module"><body /></file>
                    </xliff>
                    """;
                var bytes = System.Text.Encoding.UTF8.GetBytes(bogus);
                await entryStream.WriteAsync(bytes);
            }
        }
        zipBuffer.Position = 0;

        await using var ctx = _db.NewContext();
        var svc = NewTranslator(ctx);
        var summary = await svc.ImportZipAsync(releaseId, zipBuffer);

        summary.MatchedFiles.Should().Be(1);
        summary.UnmatchedFiles.Should().ContainSingle()
            .Which.Should().Be("NotARealModule.daDK.xlf");
        summary.TotalInserted.Should().BeGreaterThan(100);

        var moduleCount = await ctx.OeModuleTranslations
            .CountAsync(t => t.ModuleId == moduleId && t.LanguageCode == "da-DK");
        moduleCount.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task ListTranslationLanguagesAsync_surfaces_uploaded_languages()
    {
        var (releaseId, moduleId) = await SeedFtuCoreShellAsync();

        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
            await svc.ImportSingleAsync(releaseId, moduleId, s, "FTU_Core.daDK.xlf");
        }

        await using var read = _db.NewContext();
        var query = new ObjectExplorerService(read, NullLogger<ObjectExplorerService>.Instance);
        var langs = await query.ListTranslationLanguagesAsync(releaseId);
        langs.Should().ContainSingle()
            .Which.LanguageCode.Should().Be("da-DK");
    }

    [Fact]
    public async Task SearchTranslationsInReleaseAsync_finds_caption_by_translated_substring()
    {
        var (releaseId, moduleId) = await SeedFtuCoreShellAsync();

        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
            await svc.ImportSingleAsync(releaseId, moduleId, s, "FTU_Core.daDK.xlf");
        }

        await using var read = _db.NewContext();
        var query = new ObjectExplorerService(read, NullLogger<ObjectExplorerService>.Instance);
        var hits = await query.SearchTranslationsInReleaseAsync(
            releaseId, query: "Aktivér montageordrer", language: "da-DK",
            kindFilter: new HashSet<string> { "caption", "label" },
            moduleNamePattern: null, maxResults: 50);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.ObjectName == "AppSetup"
                                && h.SubName == "Activate Assembly On Service"
                                && h.Kind == "caption");
    }
}
