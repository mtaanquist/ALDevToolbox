using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
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
}
