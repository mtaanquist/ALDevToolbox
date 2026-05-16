using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// End-to-end tests for <see cref="ObjectExplorerService"/>. Each test seeds
/// the DB by running real <c>.app</c> fixtures through
/// <see cref="ReleaseImportService"/> first — that keeps the fixture data
/// honest (no hand-rolled rows that drift from what production writes) and
/// also exercises the import-then-query interaction in one go.
///
/// The find-references tests are the headline: they pin the recursive-CTE
/// chain walk + shadowing behaviour that's the whole point of the new schema.
/// </summary>
public sealed class ObjectExplorerServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<ReleaseImportService>.Instance);

    private ObjectExplorerService NewQuery(Data.AppDbContext ctx) =>
        new(ctx, NullLogger<ObjectExplorerService>.Instance);

    /// <summary>
    /// Imports the two fixtures into one Release. Returns the Release id so
    /// follow-up assertions can navigate it without re-seeding.
    /// </summary>
    private async Task<int> SeedSingleReleaseAsync()
    {
        await using var ctx = _db.NewContext();
        var svc = NewImporter(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[]
            {
                new AppFileUpload("Microsoft_DK_Core.app", s1, null),
                new AppFileUpload("Microsoft_OIOUBL.app", s2, null),
            }));
        return summary.ReleaseId;
    }

    // ── List Releases / Module / Object surfaces ────────────────────────

    [Fact]
    public async Task ListReleasesAsync_returns_active_releases_sorted_by_label()
    {
        await SeedSingleReleaseAsync();

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListReleasesAsync();
        rows.Should().ContainSingle(r => r.Label == "BC 25.18 DK")
            .Which.Kind.Should().Be("first_party");
    }

    [Fact]
    public async Task ListReleasesAsync_includes_source_file_count_and_content_length()
    {
        await SeedSingleReleaseAsync();

        await using var read = _db.NewContext();
        var row = (await NewQuery(read).ListReleasesAsync()).Single();
        row.SourceFileCount.Should().BeGreaterThan(0,
            because: "both DK Core and OIOUBL ship .al source embedded in their .apps");
        row.SourceContentLength.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetReleaseAsync_returns_module_count_and_parent_label()
    {
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var sParent = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 DK Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", sParent, null) }));

            await using var sChild = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X on BC 25.18", "customer", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", sChild, null) }));
        }

        await using var read = _db.NewContext();
        var query = NewQuery(read);
        var releases = await query.ListReleasesAsync();
        var child = releases.Single(r => r.Label == "Customer X on BC 25.18");
        var detail = await query.GetReleaseAsync(child.Id);
        detail.Should().NotBeNull();
        detail!.ParentLabel.Should().Be("BC 25.18 DK Parent");
        detail.ModuleCount.Should().Be(1);
    }

    [Fact]
    public async Task ListModulesAsync_filters_by_search_substring()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var modules = await NewQuery(read).ListModulesAsync(releaseId, new ModuleListFilter(Search: "oioubl"));
        modules.Should().ContainSingle().Which.Name.Should().Be("OIOUBL");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_finds_objects_across_modules()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        // OIOUBL declares "OIOUBL-Profile" (table 13630). DK Core has no table
        // named like that. A release-wide search for "OIOUBL-Profile" should
        // return the OIOUBL hit only, with the module name joined inline.
        var hits = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Search: "OIOUBL-Profile"));
        hits.Should().NotBeEmpty();
        hits.Should().Contain(h => h.Name == "OIOUBL-Profile" && h.ModuleName == "OIOUBL");
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_filters_by_kind_across_modules()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        // Both modules contribute codeunits. With kind=codeunit and no name
        // filter, we should get every codeunit in the Release.
        var all = await query.SearchObjectsInReleaseAsync(releaseId,
            new ObjectListFilter(Kind: "codeunit"));
        all.Should().OnlyContain(o => o.Kind == "codeunit");
        all.Select(o => o.ModuleName).Distinct().Should().Contain(new[] { "DK Core", "OIOUBL" });
    }

    [Fact]
    public async Task SearchObjectsInReleaseAsync_orders_results_by_kind_then_id()
    {
        // The legacy VersionBrowser sorted by (Type, ID) so a release-wide
        // browse groups objects by AL kind first, then by object number —
        // the order BC devs read object lists in. Pin that contract:
        // result list must be strictly non-decreasing on Kind, and within
        // a kind block strictly non-decreasing on ObjectId.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var hits = await NewQuery(read).SearchObjectsInReleaseAsync(
            releaseId,
            new ObjectListFilter(),
            moduleId: null,
            namespacePrefix: null,
            take: 500);

        hits.Should().NotBeEmpty();
        for (var i = 1; i < hits.Count; i++)
        {
            var prev = hits[i - 1];
            var cur = hits[i];
            var kindCmp = string.CompareOrdinal(prev.Kind, cur.Kind);
            kindCmp.Should().BeLessThanOrEqualTo(0,
                "results are ordered by Kind first");
            if (kindCmp == 0 && prev.ObjectId is { } pid && cur.ObjectId is { } cid)
            {
                pid.Should().BeLessThanOrEqualTo(cid,
                    "within a Kind block, results are ordered by ObjectId");
            }
        }
    }

    [Fact]
    public async Task GetFileHeaderAsync_returns_release_and_module_context_without_loading_content()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var ctx = read;

        var fileId = ctx.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var header = await NewQuery(ctx).GetFileHeaderAsync(fileId);
        header.Should().NotBeNull();
        header!.ModuleName.Should().Be("DK Core");
        header.ReleaseLabel.Should().Be("BC 25.18 DK");
        header.LineCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SearchProceduresInReleaseAsync_finds_procedures_across_modules()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var hits = await NewQuery(read).SearchProceduresInReleaseAsync(
            releaseId, search: null, moduleId: null, take: 500);

        hits.Should().NotBeEmpty();
        // Every hit's procedure kind is one of the procedure-shaped symbols.
        hits.Should().OnlyContain(h =>
            h.ProcedureKind == "procedure"
            || h.ProcedureKind == "internal_procedure"
            || h.ProcedureKind == "trigger");
    }

    [Fact]
    public async Task SearchContentInReleaseAsync_finds_lines_matching_a_substring()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var hits = await NewQuery(read).SearchContentInReleaseAsync(
            releaseId, search: "codeunit", moduleId: null);
        hits.Should().NotBeEmpty(
            because: "every codeunit's source line starts with the 'codeunit' keyword");
        hits.Should().OnlyContain(h =>
            h.Snippet.Contains("codeunit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListDeclarationsInFileAsync_returns_object_headers_with_column_positions()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var decls = await NewQuery(read).ListDeclarationsInFileAsync(fileId);
        decls.Should().NotBeEmpty();
        // The codeunit name lives on its declaration line; the helper must
        // surface columns matching the quoted-or-bare token.
        decls.Should().Contain(d =>
            d.Name == "DK Core Event Subscribers"
            && d.ColumnStart >= 1
            && d.ColumnEnd > d.ColumnStart);
    }

    [Fact]
    public async Task GoToDefinitionAsync_resolves_a_click_on_an_object_name_to_its_file()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        // Find the declarations on DK Core's event-subscribers file, then
        // simulate a click on the codeunit's name token there. The resolver
        // should land us back on the same file + the declaration line.
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;
        var decl = (await query.ListDeclarationsInFileAsync(fileId)).First();

        var target = await query.GoToDefinitionAsync(fileId, decl.Line, decl.ColumnStart);
        target.Should().NotBeNull();
        target!.FileId.Should().Be(fileId);
        target.LineNumber.Should().Be(decl.Line);
    }

    [Fact]
    public async Task FindInFileAsync_returns_every_line_containing_the_clicked_word()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;
        var decl = (await query.ListDeclarationsInFileAsync(fileId)).First();

        var hit = await query.FindInFileAsync(fileId, decl.Line, decl.ColumnStart);
        hit.Should().NotBeNull();
        hit!.Occurrences.Should().NotBeEmpty();
        hit.Occurrences.Should().OnlyContain(o => o.LineText.Contains(hit.Word, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFileOutlineAsync_returns_objects_and_symbols_ordered_by_line()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var outline = await NewQuery(read).GetFileOutlineAsync(fileId);
        outline.Should().NotBeEmpty();
        outline.Select(o => o.LineNumber).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetFileOutlineAsync_includes_procedures_with_real_line_numbers()
    {
        // Regression for the post-PR-130 outline gap: ReleaseImportService
        // used to write every sub-symbol with LineNumber=0, and the query
        // filtered those out, so the outline showed only the object header.
        // The import now runs AlSymbolExtractor over each .al file, so every
        // procedure (and trigger / event subscriber) lands with a 1-based
        // line and a non-zero column range the source viewer can scroll to.
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var fileId = read.OeModuleFiles.AsQueryable()
            .First(f => f.Path.Contains("DKCoreEventSubscribers")).Id;

        var outline = await NewQuery(read).GetFileOutlineAsync(fileId);

        outline.Should().Contain(i =>
                i.Kind == "procedure"
                || i.Kind == "internal_procedure"
                || i.Kind == "local_procedure"
                || i.Kind == "event_subscriber",
            because: "DKCoreEventSubscribers ships procedures the symbol extractor must surface");
        outline.Where(i => i.ObjectId is null).Should()
            .OnlyContain(s => s.LineNumber > 0,
                because: "sub-symbol rows must carry the line they were declared on");
    }

    [Fact]
    public async Task ListObjectsAsync_paginates_and_filters_by_kind()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        var modules = await query.ListModulesAsync(releaseId, new ModuleListFilter());
        var dkCore = modules.Single(m => m.Name == "DK Core");

        // Kind="codeunit" narrows DK Core to its 4 codeunits.
        var page = await query.ListObjectsAsync(dkCore.Id, new ObjectListFilter(Kind: "codeunit"), skip: 0, take: 50);
        page.TotalCount.Should().Be(4);
        page.Rows.Should().OnlyContain(o => o.Kind == "codeunit");

        // Pagination round-trip.
        var page1 = await query.ListObjectsAsync(dkCore.Id, new ObjectListFilter(), skip: 0, take: 5);
        var page2 = await query.ListObjectsAsync(dkCore.Id, new ObjectListFilter(), skip: 5, take: 5);
        page1.TotalCount.Should().Be(14);
        page2.TotalCount.Should().Be(14);
        page1.Rows.Should().HaveCount(5);
        // No overlap between pages.
        page1.Rows.Select(o => o.Id).Should().NotIntersectWith(page2.Rows.Select(o => o.Id));
    }

    [Fact]
    public async Task GetObjectAsync_returns_variables_and_symbols_inline()
    {
        await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();
        var ctx = read;

        var obj = ctx.OeModuleObjects.AsQueryable()
            .Single(o => o.Kind == "table" && o.Name == "OIOUBL-Profile");

        var detail = await NewQuery(ctx).GetObjectAsync(obj.Id);
        detail.Should().NotBeNull();
        detail!.ModuleName.Should().Be("OIOUBL");
        detail.SourceFilePath.Should().NotBeNullOrEmpty();

        // Two fields surface as field symbols.
        detail.Symbols.Where(s => s.Kind == "field").Should().HaveCount(2);
    }

    // ── Find references — same release ─────────────────────────────────

    [Fact]
    public async Task FindReferencesAsync_returns_dk_core_variables_pointing_at_cancel_fa_ledger_entries()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewQuery(read).FindReferencesAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 5624,
            TargetObjectName: "Cancel FA Ledger Entries"));

        matches.Should().NotBeEmpty();
        matches.Should().OnlyContain(m => m.SourceModuleName == "DK Core",
            because: "the only module in this release that references that codeunit is DK Core");
        matches.Should().Contain(m =>
            m.ReferenceKind == "variable_type"
            && m.SourceObjectName == "CopyDepreciationBookExt");
        // SourceFileId is needed for the file-viewer links in
        // OeObjectDetail.razor's references table; every row pointing at an
        // object whose source we have should expose it.
        matches.Should().Contain(m => m.SourceFileId.HasValue,
            because: "DK Core's referencing objects ship with source");
    }

    [Fact]
    public async Task FindReferencesAsync_falls_back_to_name_when_id_is_null()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        // extends_target rows have TargetObjectId=null (the symbol package
        // doesn't carry the base object's id on the Target string — only the
        // AppId + name). Querying by name only must still find them.
        var matches = await NewQuery(read).FindReferencesAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "table",
            TargetObjectId: null,
            TargetObjectName: "Finance Charge Memo Line"));

        matches.Should().NotBeEmpty();
        matches.Should().Contain(m => m.ReferenceKind == "extends_target"
            && m.SourceModuleName == "OIOUBL");
    }

    [Fact]
    public async Task FindReferencesAsync_returns_empty_when_target_not_referenced()
    {
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewQuery(read).FindReferencesAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 99999999,
            TargetObjectName: "Nonexistent Codeunit"));
        matches.Should().BeEmpty();
    }

    // ── Find references — parent-chain walk + shadowing ────────────────

    [Fact]
    public async Task FindReferencesAsync_walks_parent_chain_to_find_references_in_ancestor_releases()
    {
        // Parent release has DK Core; child release adds OIOUBL on top.
        // Querying the CHILD must surface DK Core's references too because
        // the chain walk pulls DK Core's module up into the child's view.
        int parentId, childId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "customer", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
            childId = child.ReleaseId;
        }

        await using var read = _db.NewContext();
        var query = NewQuery(read);

        var fromChild = await query.FindReferencesAsync(childId, new FindReferencesQuery(
            BaseAppId, "codeunit", 5624, "Cancel FA Ledger Entries"));
        fromChild.Should().NotBeEmpty(because: "DK Core lives in the parent and the chain walk reaches it from the child");
        fromChild.Should().OnlyContain(m => m.SourceModuleName == "DK Core");
    }

    [Fact]
    public async Task FindReferencesAsync_root_release_does_not_see_descendant_references()
    {
        // Parent release: DK Core. Child release: OIOUBL.
        // Querying the PARENT must NOT see OIOUBL's references — chain walk
        // is upward only (toward older Releases), never downward into
        // descendant Releases.
        int parentId, childId;
        await using (var ctx = _db.NewContext())
        {
            var imp = NewImporter(ctx);
            await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "BC 25.18 Parent", "first_party", null, null,
                new[] { new AppFileUpload("dk.app", s1, null) }));
            parentId = parent.ReleaseId;

            await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
                "Customer X", "customer", parent.ReleaseId, null,
                new[] { new AppFileUpload("oioubl.app", s2, null) }));
            childId = child.ReleaseId;
        }

        await using var read = _db.NewContext();
        var query = NewQuery(read);

        // OIOUBL's extends_target → Base App's "Finance Charge Memo Line".
        // From the PARENT release (DK Core only) we must NOT see this row.
        var fromParent = await query.FindReferencesAsync(parentId, new FindReferencesQuery(
            BaseAppId, "table", null, "Finance Charge Memo Line"));
        fromParent.Should().BeEmpty(
            because: "OIOUBL is in the child Release; chain walk is parent-ward only");
    }

    [Fact]
    public async Task FindReferencesAsync_shadows_same_appid_with_closest_release_in_chain()
    {
        // This test pins the shadowing rule: same AppId in both parent and child,
        // the child's version wins. We import DK Core into the parent, then
        // import a DIFFERENT-bytes copy of DK Core into the child (we can't
        // easily get one without rebuilding, so we cheat by directly inserting
        // a second module row at the SQL layer to simulate a re-issued .app).
        await using var ctx = _db.NewContext();
        var imp = NewImporter(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var parent = await imp.ImportReleaseAsync(new ReleaseImportRequest(
            "Parent", "first_party", null, null,
            new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var child = await imp.ImportReleaseAsync(new ReleaseImportRequest(
            "Child", "customer", parent.ReleaseId, null,
            new[] { new AppFileUpload("dk.app", s2, null) }));

        // Each Release now has a Module with AppId=DK Core. Both also have an
        // OeModuleReference pointing at "Cancel FA Ledger Entries". Querying
        // the CHILD must return references from the CHILD's DK Core module
        // only (shadowing), so we should get exactly the child's count, not
        // double-counted across parent + child.
        await using var read = _db.NewContext();
        var query = NewQuery(read);

        var matches = await query.FindReferencesAsync(child.ReleaseId, new FindReferencesQuery(
            BaseAppId, "codeunit", 5624, "Cancel FA Ledger Entries"));

        var childModuleIds = read.OeModules
            .Where(m => m.ReleaseId == child.ReleaseId && m.Name == "DK Core")
            .Select(m => m.Id)
            .ToHashSet();

        matches.Should().NotBeEmpty();
        matches.Should().OnlyContain(m => childModuleIds.Contains(m.SourceModuleId),
            because: "the child's same-AppId module shadows the parent's");
    }

    // ── Find references — member-scoped ────────────────────────────────

    [Fact]
    public async Task FindReferencesForSymbolAsync_returns_sibling_declarations_in_the_chain()
    {
        // DK Core's "CopyDepreciationBookExt" declares procedures of its own;
        // the codeunit has multiple symbol rows. A member-scoped find against
        // one of those names should at minimum return the symbol's own
        // declaration row tagged Category=declaration.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var copyExt = await read.OeModuleObjects
            .Where(o => o.Name == "CopyDepreciationBookExt")
            .Select(o => new { o.Id, ModuleAppId = o.Module!.AppId, o.Kind, o.Name, o.ObjectId })
            .SingleAsync();
        var procName = await read.OeModuleSymbols
            .Where(s => s.ObjectId == copyExt.Id && s.Kind == "procedure")
            .Select(s => s.Name)
            .FirstAsync();

        var matches = await NewQuery(read).FindReferencesForSymbolAsync(releaseId, new FindReferencesQuery(
            TargetAppId: copyExt.ModuleAppId,
            TargetObjectKind: copyExt.Kind,
            TargetObjectId: copyExt.ObjectId,
            TargetObjectName: copyExt.Name,
            TargetMemberName: procName,
            TargetMemberKind: "procedure"));

        matches.Should().Contain(m => m.Category == "declaration"
            && m.SourceObjectName == "CopyDepreciationBookExt"
            && m.MemberName == procName);
    }

    [Fact]
    public async Task FindReferencesForSymbolAsync_surfaces_owner_type_indirect_refs()
    {
        // FindReferencesForSymbolAsync's third bucket is owner-type rows —
        // the object-level references already in the DB (variable_type,
        // parameter_type, …) for the owner of the member we're searching.
        // Even though no method-call rows are populated in phase 1, the
        // user should see "indirect references via type" answers.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewQuery(read).FindReferencesForSymbolAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 5624,
            TargetObjectName: "Cancel FA Ledger Entries",
            TargetMemberName: "AnyProcedureName",
            TargetMemberKind: "procedure"));

        // The owner Codeunit 5624 is referenced as a variable_type from
        // DK Core's CopyDepreciationBookExt — those indirect-via-type rows
        // belong in the owner_type bucket so the UI groups them under
        // "Indirect references".
        matches.Should().Contain(m => m.Category == "owner_type"
            && m.ReferenceKind == "variable_type"
            && m.SourceObjectName == "CopyDepreciationBookExt");
    }

    [Fact]
    public async Task FindReferencesForSymbolAsync_call_bucket_is_empty_in_phase_one()
    {
        // Phase 1 leaves target_member_name NULL on every imported row;
        // the call bucket is wired up but should return zero results
        // until the importer starts populating member-scoped references.
        var releaseId = await SeedSingleReleaseAsync();
        await using var read = _db.NewContext();

        var matches = await NewQuery(read).FindReferencesForSymbolAsync(releaseId, new FindReferencesQuery(
            TargetAppId: BaseAppId,
            TargetObjectKind: "codeunit",
            TargetObjectId: 5624,
            TargetObjectName: "Cancel FA Ledger Entries",
            TargetMemberName: "AnyProcedureName",
            TargetMemberKind: "procedure"));

        matches.Should().NotContain(m => m.Category == "call",
            because: "the importer doesn't populate target_member_name yet");
    }
}
