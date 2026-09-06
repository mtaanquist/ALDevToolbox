using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// The project-visibility fence shared by the two source surfaces,
/// <see cref="SourceViewerService"/> and <see cref="ExplorerTreeService"/>.
///
/// Both are keyed by file, module, and symbol ids rather than by release, so
/// each entry point resolves the owning release and asks the one authority
/// (<see cref="ProjectAccess.IsReleaseVisibleAsync"/>) whether the caller may
/// see it. A denied read returns the same empty / null the caller gets for an
/// id in another org — the JSON endpoints in
/// Endpoints/ObjectExplorerViewerEndpoints.cs turn that into their existing
/// not-found shape, never a distinct refusal. See
/// <c>.design/teams-and-visibility.md</c>.
///
/// <para>Scoped, so the two services rendering one page share the memo below
/// rather than each keeping their own.</para>
/// </summary>
public sealed class SourceVisibility
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;

    public SourceVisibility(AppDbContext db, ProjectAccess access)
    {
        _db = db;
        _access = access;
    }

    /// <summary>
    /// Visibility answers already resolved in this DI scope. Rendering one source
    /// page asks the same question from eight entry points (header, content,
    /// outline, declarations, resolvables, tree, ...), and — for the same reason
    /// <see cref="ProjectAccess.IsReleaseVisibleAsync"/> memoises its own answers —
    /// it cannot change within a scope. Bounded by the files one scope touches.
    /// </summary>
    private readonly Dictionary<long, bool> _fileVisibility = new();

    /// <summary>
    /// True when the caller may see source file <paramref name="fileId"/>, via the
    /// release its module belongs to. A file that doesn't resolve reads as visible —
    /// the caller's own read comes back empty on its own.
    /// </summary>
    public async Task<bool> FileVisibleAsync(long fileId, CancellationToken ct)
    {
        if (_fileVisibility.TryGetValue(fileId, out var cached)) return cached;

        var releaseId = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => (int?)f.Module!.ReleaseId)
            .FirstOrDefaultAsync(ct);
        return _fileVisibility[fileId] =
            releaseId is null || await _access.IsReleaseVisibleAsync(releaseId.Value, ct);
    }

    /// <summary>
    /// True when the caller may see module <paramref name="moduleId"/>, via the release
    /// it belongs to. Not memoised: the tree asks it once per listing, not per row.
    /// </summary>
    public async Task<bool> ModuleVisibleAsync(long moduleId, CancellationToken ct)
    {
        var releaseId = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == moduleId)
            .Select(m => (int?)m.ReleaseId)
            .FirstOrDefaultAsync(ct);
        return releaseId is null || await _access.IsReleaseVisibleAsync(releaseId.Value, ct);
    }

    /// <summary>
    /// True when the caller may see release <paramref name="releaseId"/>. The
    /// release-keyed form of the same fence, for the entry points that already
    /// hold a release id (the explorer's search box) or resolve one themselves
    /// (the symbol hover card).
    /// </summary>
    public Task<bool> ReleaseVisibleAsync(int releaseId, CancellationToken ct)
        => _access.IsReleaseVisibleAsync(releaseId, ct);
}
