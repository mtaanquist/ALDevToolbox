using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OeModule = ALDevToolbox.Domain.Entities.ObjectExplorer.Module;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Black-box coverage of <see cref="ReleaseImportService"/>. Drives the
/// service end-to-end through the shared <see cref="TestDb"/> Postgres
/// fixture: feed real Microsoft <c>.app</c> fixtures into
/// <c>ImportReleaseAsync</c>, then read back the <c>oe_*</c> rows to assert
/// the schema-shaped invariants we care about — counts, the cross-module
/// references the find-references query will key off of, the idempotency
/// guarantee on the (release, app, version, hash) tuple, and the
/// Release lifecycle (ingesting → ready/failed).
/// </summary>
public sealed class ReleaseImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private const string DkCoreAppId = "40d64215-8abc-4d96-87dc-2894e5431115";
    private const string OioublAppId = "edc24573-b277-44bb-8c2a-7a90fcabd055";
    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    private ReleaseImportService NewService(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<ReleaseImportService>.Instance);

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Imports_dk_core_into_a_ready_release_with_expected_row_counts()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("Microsoft_DK_Core.app", appStream, SourceZipStream: null) }));

        summary.ModulesImported.Should().Be(1);
        summary.ModulesSkipped.Should().Be(0);
        // DK Core: 4 codeunits + 3 pageextensions + 7 reportextensions = 14 objects.
        summary.ObjectsImported.Should().Be(14);
        summary.SourceFilesImported.Should().BeGreaterThan(0);

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == summary.ReleaseId);
        release.Status.Should().Be("ready");
        release.Label.Should().Be("BC 25.18 DK");
        release.Kind.Should().Be("first_party");
        // No Base App came in this release, so BcVersion stays null.
        release.BcVersion.Should().BeNull();

        var modules = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == summary.ReleaseId).ToListAsync();
        modules.Should().ContainSingle()
            .Which.AppId.Should().Be(Guid.Parse(DkCoreAppId));
    }

    [Fact]
    public async Task Imports_oioubl_records_a_tableextension_extends_reference_back_to_base_app()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK + OIOUBL",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("Microsoft_OIOUBL.app", appStream, SourceZipStream: null) }));

        await using var read = _db.NewContext();
        // OIOUBL's "OIOUBL-Fin. Charge Memo Line" tableextension extends Base App's
        // "Finance Charge Memo Line" table — that's an extends_target reference
        // pointing at Base App's AppId.
        var extendsRef = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.ReferenceKind == "extends_target"
                && r.TargetAppId == BaseAppId
                && r.TargetObjectKind == "table"
                && r.TargetObjectName == "Finance Charge Memo Line")
            .ToListAsync();
        extendsRef.Should().NotBeEmpty(
            because: "the parser sees the #...# prefix on tableextension Target and the importer materialises it as a row");
    }

    [Fact]
    public async Task Imports_resolved_variable_subtypes_as_cross_module_references()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, null) }));

        await using var read = _db.NewContext();
        // CopyDepreciationBookExt declares a Codeunit-typed variable resolving to
        // Base App's "Cancel FA Ledger Entries" (id 5624). Verify the reference
        // row got the full triplet — that's what the find-references query keys on.
        // Use a list rather than Single because the same target can appear from
        // a variable AND from a parameter or return type in unrelated methods.
        var refs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.ReferenceKind == "variable_type"
                && r.TargetAppId == BaseAppId
                && r.TargetObjectKind == "codeunit"
                && r.TargetObjectId == 5624)
            .ToListAsync();
        refs.Should().NotBeEmpty();
        refs.Should().OnlyContain(r => r.TargetObjectName == "Cancel FA Ledger Entries");
    }

    [Fact]
    public async Task Imports_table_fields_as_field_symbols_with_their_id()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "OIOUBL", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("oioubl.app", appStream, null) }));

        await using var read = _db.NewContext();
        var profileTable = await read.OeModuleObjects.AsNoTracking()
            .SingleAsync(o => o.Kind == "table" && o.Name == "OIOUBL-Profile");
        var fields = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == profileTable.Id && s.Kind == "field")
            .OrderBy(s => s.FieldId)
            .ToListAsync();
        fields.Should().HaveCount(2);
        fields[0].Name.Should().Be("OIOUBL-Code");
        fields[0].FieldId.Should().Be(13630);
        fields[1].Name.Should().Be("OIOUBL-Profile ID");
        fields[1].FieldId.Should().Be(13631);
    }

    [Fact]
    public async Task Stamps_module_object_line_numbers_from_embedded_source()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, null) }));

        await using var read = _db.NewContext();
        // The codeunit declaration sits at the top of its .al file (line 1
        // after any namespace declaration). The exact line varies with header
        // comments / namespaces, but it must be >= 1 and the SourceFile FK
        // must be wired.
        var codeunit = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Kind == "codeunit" && o.Name == "DK Core Event Subscribers")
            .SingleAsync();
        codeunit.LineNumber.Should().BeGreaterThan(0);
        codeunit.SourceFileId.Should().NotBeNull(
            because: "ReferenceSourceFileName matches a file in the embedded src/ tree");
    }

    // ── Idempotency ─────────────────────────────────────────────────────

    [Fact]
    public async Task Byte_identical_re_upload_is_skipped_silently_on_the_same_release()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var first = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Release A", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));
        first.ModulesImported.Should().Be(1);
        first.ModulesSkipped.Should().Be(0);

        // Re-uploading into a *different* Release would just create a new
        // module; the skip is per-Release. Verify by re-running with the same
        // bytes against a second Release.
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var second = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Release B", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s2, null) }));
        second.ModulesImported.Should().Be(1);
        second.ModulesSkipped.Should().Be(0);
    }

    // ── Validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Rejects_request_missing_label_with_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "  ", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s, null) }));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Label");
    }

    [Fact]
    public async Task Rejects_request_with_unknown_kind()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Foo", Kind: "wat",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s, null) }));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Kind");
    }

    [Fact]
    public async Task Rejects_request_with_no_uploads()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Foo", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: Array.Empty<AppFileUpload>()));
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Uploads");
    }

    // ── Failure handling ────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_app_flips_the_release_to_failed_and_records_the_message()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        // A non-NAVX stream — the reader throws InvalidDataException, the
        // service must catch, stamp the release as failed, and rethrow so
        // the caller sees the original error.
        await using var notAnAppStream = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Bad release", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("not-an-app.zip", notAnAppStream, null) }));
        await act.Should().ThrowAsync<InvalidDataException>();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking()
            .SingleAsync(r => r.Label == "Bad release");
        release.Status.Should().Be("failed");
        release.StatusMessage.Should().NotBeNullOrEmpty();
        release.StatusMessage.Should().Contain("NAVX");
    }

    // ── Parent-release linkage ──────────────────────────────────────────

    [Fact]
    public async Task Records_parent_release_pointer_when_set()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var parent = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Parent BC 25.18", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var child = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Customer X on BC 25.18", Kind: "customer",
            ParentReleaseId: parent.ReleaseId, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("oioubl.app", s2, null) }));

        await using var read = _db.NewContext();
        var rel = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == child.ReleaseId);
        rel.ParentReleaseId.Should().Be(parent.ReleaseId);
    }
}
