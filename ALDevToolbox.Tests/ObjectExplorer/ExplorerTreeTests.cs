using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The Object Explorer's left-hand tree (PR 14b). Seeded from the real
/// <c>.app</c> fixtures rather than hand-written rows, because the shape the
/// tree walks — module-relative paths with a <c>src/</c> prefix, one object per
/// file, some files with no object at all — is exactly the shape a hand-rolled
/// fixture gets to choose, and a fixture that agrees with the assumption cannot
/// falsify it.
///
/// The property worth guarding hardest is the <em>bounded</em> one: expanding a
/// folder must return that folder's own children and nothing deeper. A Base
/// Application module runs to thousands of files, and the difference between
/// "the immediate children" and "every descendant" is invisible on a fixture
/// with twenty.
/// </summary>
public sealed class ExplorerTreeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext,
                new ALDevToolbox.Services.Translation.TranslationMemoryService(
                    ctx, _db.OrgContext,
                    NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
                NullLogger<TranslationImportService>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    private SourceViewerService NewViewer(Data.AppDbContext ctx) =>
        new(ctx, new ReferenceQueryService(ctx, NullLogger<ReferenceQueryService>.Instance));

    /// <summary>Two modules in one release, so the tree has siblings to close.</summary>
    private async Task<int> SeedAsync()
    {
        await using var ctx = _db.NewContext();
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await NewImporter(ctx).ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[]
            {
                new AppFileUpload("Microsoft_DK_Core.app", s1, null),
                new AppFileUpload("Microsoft_OIOUBL.app", s2, null),
            }));
        return summary.ReleaseId;
    }

    private static async Task<(long FileId, string Path, long ModuleId)> AFileAsync(Data.AppDbContext ctx) =>
        await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.Path.Contains("DKCoreEventSubscribers"))
            .Select(f => new ValueTuple<long, string, long>(f.Id, f.Path, f.ModuleId))
            .FirstAsync();

    // ── The opened branch ──────────────────────────────────────────────

    [Fact]
    public async Task The_tree_lists_every_module_in_the_release_and_opens_only_the_files_own()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, _, moduleId) = await AFileAsync(ctx);

        var tree = await NewViewer(ctx).GetExplorerTreeAsync(fileId);

        var modules = tree.Where(n => n.Kind == "module").ToList();
        modules.Should().HaveCount(2, because: "the release holds DK Core and OIOUBL");
        modules.Should().OnlyContain(m => m.HasChildren);
        modules.Where(m => m.IsOpen).Should().ContainSingle()
            .Which.ModuleId.Should().Be(moduleId);
        modules.Select(m => m.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Every_folder_on_the_way_to_the_open_file_is_open_and_the_file_is_active()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, path, _) = await AFileAsync(ctx);

        var tree = await NewViewer(ctx).GetExplorerTreeAsync(fileId);

        // "src/Codeunits/DKCoreEventSubscribers.Codeunit.al" → src, Codeunits
        var folders = path.Split('/')[..^1];
        foreach (var (name, i) in folders.Select((n, i) => (n, i)))
        {
            tree.Should().ContainSingle(
                    n => n.Kind == "folder" && n.Name == name && n.IsOpen && n.Depth == i + 1,
                    because: $"'{name}' is on the path to the open file")
                .Which.Path.Should().EndWith("/", because: "a folder path is usable as a prefix");
        }

        var active = tree.Where(n => n.IsActive).ToList();
        active.Should().ContainSingle().Which.FileId.Should().Be(fileId);
        active[0].Depth.Should().Be(folders.Length + 1);
    }

    /// <summary>
    /// The list is flat but reads as a tree, so a folder's children have to sit
    /// immediately beneath it — not after its siblings. Verified by walking the
    /// rows and checking depth never jumps by more than one.
    /// </summary>
    [Fact]
    public async Task The_flat_list_reads_as_a_pre_order_walk()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, path, _) = await AFileAsync(ctx);

        var tree = await NewViewer(ctx).GetExplorerTreeAsync(fileId);

        for (var i = 1; i < tree.Count; i++)
        {
            (tree[i].Depth - tree[i - 1].Depth).Should().BeLessOrEqualTo(1,
                because: $"row {i} ({tree[i].Name}) cannot be deeper than one below its predecessor");
        }

        // And the deepest row, the open file, follows its own folder directly.
        var fileIndex = tree.FindIndex(n => n.FileId == fileId);
        var parentName = path.Split('/')[^2];
        tree.Take(fileIndex).Last(n => n.Depth == tree[fileIndex].Depth - 1)
            .Name.Should().Be(parentName);
    }

    [Fact]
    public async Task A_file_that_does_not_exist_produces_no_tree()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();

        (await NewViewer(ctx).GetExplorerTreeAsync(-1)).Should().BeEmpty();
    }

    // ── Lazy children ──────────────────────────────────────────────────

    /// <summary>
    /// The reason the tree is lazy at all. Expanding the module root must
    /// return <c>src</c> once, not every path that starts with it.
    /// </summary>
    [Fact]
    public async Task Expanding_a_folder_returns_its_own_children_and_nothing_deeper()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);
        var viewer = NewViewer(ctx);

        var root = await viewer.GetTreeChildrenAsync(moduleId, "");
        root.Should().NotBeEmpty();
        root.Should().OnlyContain(n => n.Depth == 0, because: "the caller assigns depth");
        root.Where(n => n.Kind == "folder").Select(n => n.Name)
            .Should().OnlyHaveUniqueItems().And.Contain("src");
        root.Should().NotContain(n => n.Name.Contains('/'),
            because: "a child is one segment, never a path");

        var src = await viewer.GetTreeChildrenAsync(moduleId, "src");
        src.Should().NotBeEmpty();
        src.Where(n => n.Kind == "folder").Should().OnlyContain(n => n.Path.StartsWith("src/"));
        src.Should().NotContain(n => n.Name == "src", because: "src is the parent, not its own child");

        var everyFile = await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId).CountAsync();
        src.Count.Should().BeLessThan(everyFile,
            because: "expanding one folder must not return the whole module");
    }

    [Fact]
    public async Task A_trailing_slash_on_the_prefix_makes_no_difference()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);
        var viewer = NewViewer(ctx);

        var withSlash = await viewer.GetTreeChildrenAsync(moduleId, "src/");
        var without = await viewer.GetTreeChildrenAsync(moduleId, "src");

        withSlash.Should().BeEquivalentTo(without);
    }

    [Fact]
    public async Task Folders_come_before_files_and_both_are_alphabetical()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, path, moduleId) = await AFileAsync(ctx);
        var parent = path[..(path.LastIndexOf('/') + 1)];

        var children = await NewViewer(ctx).GetTreeChildrenAsync(moduleId, parent);

        var kinds = children.Select(c => c.Kind).ToList();
        kinds.LastIndexOf("folder").Should().BeLessThan(
            kinds.IndexOf("file") < 0 ? int.MaxValue : kinds.IndexOf("file") + 1);
        children.Where(c => c.Kind == "file").Select(c => c.Name)
            .Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_reads_as_its_object_and_keeps_its_file_name()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (fileId, path, moduleId) = await AFileAsync(ctx);
        var parent = path[..(path.LastIndexOf('/') + 1)];
        var expected = await ctx.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Name, o.Kind, o.ObjectId })
            .FirstAsync();

        var row = (await NewViewer(ctx).GetTreeChildrenAsync(moduleId, parent))
            .Single(c => c.FileId == fileId);

        row.Name.Should().Be(expected.Name);
        row.FileName.Should().Be(path[(path.LastIndexOf('/') + 1)..]);
        row.ObjectKind.Should().Be(expected.Kind);
        row.Badge.Should().Be(expected.ObjectId?.ToString());
        row.HasChildren.Should().BeFalse();
        row.Path.Should().Be(path);
    }

    [Fact]
    public async Task Every_file_in_the_module_is_reachable_exactly_once()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);
        var viewer = NewViewer(ctx);

        var seen = new List<long>();
        var queue = new Queue<string>([""]);
        while (queue.Count > 0)
        {
            foreach (var child in await viewer.GetTreeChildrenAsync(moduleId, queue.Dequeue()))
            {
                if (child.Kind == "folder") queue.Enqueue(child.Path);
                else seen.Add(child.FileId!.Value);
            }
        }

        var all = await ctx.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId).Select(f => f.Id).ToListAsync();
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeEquivalentTo(all,
            because: "walking the tree must reach every file the module holds, and no file twice");
    }

    [Fact]
    public async Task An_unknown_module_yields_nothing_rather_than_throwing()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();

        (await NewViewer(ctx).GetTreeChildrenAsync(-1, "")).Should().BeEmpty();
    }

    /// <summary>
    /// The tree endpoint takes a module id straight off the URL, so the query
    /// filter is the only thing standing between one org and another's source
    /// layout. Same guard the rest of the viewer's reads get.
    /// </summary>
    [Fact]
    public async Task Another_orgs_module_is_invisible_even_by_id()
    {
        await SeedAsync();
        long moduleId;
        long fileId;
        await using (var ctx = _db.NewContext())
        {
            (fileId, _, moduleId) = await AFileAsync(ctx);
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        try
        {
            await using var other = _db.NewContext();
            (await NewViewer(other).GetTreeChildrenAsync(moduleId, "")).Should().BeEmpty();
            (await NewViewer(other).GetExplorerTreeAsync(fileId)).Should().BeEmpty();
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
    }

    [Fact]
    public async Task The_object_count_is_the_modules_own()
    {
        await SeedAsync();
        await using var ctx = _db.NewContext();
        var (_, _, moduleId) = await AFileAsync(ctx);

        var expected = await ctx.OeModuleObjects.AsNoTracking()
            .CountAsync(o => o.ModuleId == moduleId);

        (await NewViewer(ctx).CountModuleObjectsAsync(moduleId)).Should().Be(expected).And.BePositive();
    }
}
