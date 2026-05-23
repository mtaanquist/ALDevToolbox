using System.IO.Compression;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translations;

/// <summary>
/// End-to-end coverage for <see cref="TranslationImportService"/> against a
/// real Microsoft OIOUBL XLIFF: the single-file admin path, the per-release
/// ZIP path, and the clobber-on-re-upload semantics the user requested. The
/// OIOUBL <c>.app</c> fixture and the OIOUBL <c>.xlf</c> fixture share a
/// module name ("OIOUBL"), so the ZIP path's <c>&lt;file original&gt;</c> →
/// <c>Module.Name</c> match works without any test-only renames.
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
    /// Seeds a release containing the Microsoft OIOUBL <c>.app</c> fixture
    /// (along with its DK Core parent so reference resolution stays
    /// happy). Returns both the release id and the OIOUBL module id so
    /// follow-up assertions don't re-query.
    /// </summary>
    private async Task<(int ReleaseId, long OioublModuleId)> SeedOioublReleaseAsync()
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
                Label: "Translation fixture release",
                Kind: "first_party",
                ParentReleaseId: null, ApplicationVersionId: null,
                Uploads: new[]
                {
                    new AppFileUpload("Microsoft_DK_Core.app", s1, null),
                    new AppFileUpload("Microsoft_OIOUBL.app", s2, null),
                }));
            releaseId = summary.ReleaseId;
        }

        long moduleId;
        await using (var ctx = _db.NewContext())
        {
            moduleId = await ctx.OeModules
                .Where(m => m.ReleaseId == releaseId && m.Name == "OIOUBL")
                .Select(m => m.Id)
                .SingleAsync();
        }

        return (releaseId, moduleId);
    }

    [Fact]
    public async Task ImportSingleAsync_persists_trans_units_with_normalised_language()
    {
        var (releaseId, moduleId) = await SeedOioublReleaseAsync();

        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
            var summary = await svc.ImportSingleAsync(releaseId, moduleId, s, "OIOUBL.daDK.xlf");

            summary.LanguageCode.Should().Be("da-DK");
            summary.ModuleName.Should().Be("OIOUBL");
            summary.Inserted.Should().Be(436,
                because: "OIOUBL.daDK.xlf ships 436 trans-units; a different count means the parser or de-dup skipped something");
        }

        await using (var read = _db.NewContext())
        {
            // Spot-check a NamedType error label — modern Microsoft format
            // puts the LookupHint inside the Developer note; the importer
            // must extract that and persist structured object/sub-element
            // info instead of dropping the row to "unknown".
            var label = await read.OeModuleTranslations
                .FirstAsync(t => t.ModuleId == moduleId
                    && t.LanguageCode == "da-DK"
                    && t.SubName == "DiscountAmountNegativeErr");
            label.ObjectKind.Should().Be("codeunit");
            label.ObjectName.Should().Be("OIOUBL-Check Sales Header");
            label.SubKind.Should().Be("namedtype");
            label.Kind.Should().Be("label");
            label.TargetText.Should().Contain("linjerabatbeløb");
        }
    }

    [Fact]
    public async Task ImportSingleAsync_re_upload_clobbers_previous_rows()
    {
        var (releaseId, moduleId) = await SeedOioublReleaseAsync();

        int firstCount;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
            firstCount = (await svc.ImportSingleAsync(releaseId, moduleId, s, "OIOUBL.daDK.xlf")).Inserted;
        }

        int secondCount;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
            secondCount = (await svc.ImportSingleAsync(releaseId, moduleId, s, "OIOUBL.daDK.xlf")).Inserted;
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
        var (releaseId, _) = await SeedOioublReleaseAsync();
        const long missingModuleId = 99999;

        await using var ctx = _db.NewContext();
        var svc = NewTranslator(ctx);
        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));

        Func<Task> act = () => svc.ImportSingleAsync(releaseId, missingModuleId, s, "x.xlf");
        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task ImportZipAsync_matches_modules_by_file_original_attribute()
    {
        var (releaseId, moduleId) = await SeedOioublReleaseAsync();

        // Pack the sample XLIFF into a ZIP plus a deliberately-unmatched
        // entry so the test covers both the matched count and the
        // UnmatchedFiles list.
        await using var zipBuffer = new MemoryStream();
        using (var archive = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var matched = archive.CreateEntry("OIOUBL.daDK.xlf");
            await using (var entryStream = matched.Open())
            await using (var fs = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf")))
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
        summary.TotalInserted.Should().Be(436);

        var moduleCount = await ctx.OeModuleTranslations
            .CountAsync(t => t.ModuleId == moduleId && t.LanguageCode == "da-DK");
        moduleCount.Should().Be(436);
    }

    [Fact]
    public async Task ListTranslationLanguagesAsync_surfaces_uploaded_languages()
    {
        var (releaseId, moduleId) = await SeedOioublReleaseAsync();

        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
            await svc.ImportSingleAsync(releaseId, moduleId, s, "OIOUBL.daDK.xlf");
        }

        await using var read = _db.NewContext();
        var query = new TranslationQueryService(read);
        var langs = await query.ListTranslationLanguagesAsync(releaseId);
        langs.Should().ContainSingle()
            .Which.LanguageCode.Should().Be("da-DK");
    }

    [Fact]
    public async Task SearchTranslationsInReleaseAsync_finds_error_label_by_translated_substring()
    {
        var (releaseId, moduleId) = await SeedOioublReleaseAsync();

        await using (var ctx = _db.NewContext())
        {
            var svc = NewTranslator(ctx);
            await using var s = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
            await svc.ImportSingleAsync(releaseId, moduleId, s, "OIOUBL.daDK.xlf");
        }

        await using var read = _db.NewContext();
        var query = new TranslationQueryService(read);
        // The Danish translation of "The total Line Discount Amount cannot
        // be negative." — pinned because it's the user's headline scenario:
        // a customer pastes a Danish error message into a ticket and the
        // developer needs to find the AL label that produced it.
        var hits = await query.SearchTranslationsInReleaseAsync(
            releaseId, query: "linjerabatbeløb", language: "da-DK",
            kindFilter: new HashSet<string> { "caption", "label" },
            moduleNamePattern: null, maxResults: 50);

        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.ObjectName == "OIOUBL-Check Sales Header"
                                && h.SubName == "DiscountAmountNegativeErr"
                                && h.Kind == "label");
    }
}
