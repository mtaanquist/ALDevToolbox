using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Module- and file-level comparison between two BC releases, plus the
/// compare-target discovery that powers the "compare this file against…"
/// picker. Split out of <see cref="ObjectExplorerService"/> so the diff
/// surface stands on its own. All reads are <c>AsNoTracking</c> and respect
/// the tenant query filter on <see cref="AppDbContext"/>.
/// </summary>
public sealed class ReleaseComparisonService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ReleaseComparisonService> _logger;

    public ReleaseComparisonService(AppDbContext db, ILogger<ReleaseComparisonService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Module-and-file-level diff between two Releases, keyed by
    /// <c>AppId</c> for modules and canonical <c>Path</c> for files inside the
    /// Changed bucket. Read-only — see <c>.design/object-explorer.md</c> for
    /// why <c>ModuleFile.Path</c> is canonicalised at ingest, which is what
    /// makes the path-based file join trustworthy across releases.
    ///
    /// Returns null when either release id doesn't exist (or is soft-deleted).
    /// </summary>
    public async Task<ReleaseCompareSummary?> CompareReleasesAsync(
        int leftReleaseId, int rightReleaseId, CancellationToken ct = default)
    {
        var releases = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == leftReleaseId || r.Id == rightReleaseId)
            .Where(r => r.DeletedAt == null)
            .Select(r => new { r.Id, r.Label })
            .ToListAsync(ct);

        var left = releases.FirstOrDefault(r => r.Id == leftReleaseId);
        var right = releases.FirstOrDefault(r => r.Id == rightReleaseId);
        if (left is null || right is null) return null;

        var leftModules = await LoadModuleCompareRowsAsync(leftReleaseId, ct);
        var rightModules = await LoadModuleCompareRowsAsync(rightReleaseId, ct);

        var leftByApp = leftModules.ToDictionary(m => m.AppId);
        var rightByApp = rightModules.ToDictionary(m => m.AppId);

        var added = new List<ModuleCompareEntry>();
        var removed = new List<ModuleCompareEntry>();
        var changed = new List<ModuleCompareEntry>();

        foreach (var appId in rightByApp.Keys.Except(leftByApp.Keys))
        {
            var m = rightByApp[appId];
            added.Add(new ModuleCompareEntry(
                appId, m.Name, m.Publisher,
                LeftModuleId: null, LeftVersion: null,
                RightModuleId: m.ModuleId, RightVersion: m.Version,
                AddedFileCount: 0, RemovedFileCount: 0, ChangedFileCount: 0));
        }
        foreach (var appId in leftByApp.Keys.Except(rightByApp.Keys))
        {
            var m = leftByApp[appId];
            removed.Add(new ModuleCompareEntry(
                appId, m.Name, m.Publisher,
                LeftModuleId: m.ModuleId, LeftVersion: m.Version,
                RightModuleId: null, RightVersion: null,
                AddedFileCount: 0, RemovedFileCount: 0, ChangedFileCount: 0));
        }

        var intersection = leftByApp.Keys.Intersect(rightByApp.Keys).ToList();

        // For the Changed bucket compute per-module file diff counts in one
        // pass — load (ModuleId, Path, ContentHash) for both sides of every
        // intersection module, key into a dictionary by (ModuleId, Path),
        // walk per AppId.
        if (intersection.Count > 0)
        {
            var leftModIds = intersection.Select(a => leftByApp[a].ModuleId).ToList();
            var rightModIds = intersection.Select(a => rightByApp[a].ModuleId).ToList();

            var leftFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => leftModIds.Contains(f.ModuleId))
                .Select(f => new { f.ModuleId, f.Path, f.ContentHash })
                .ToListAsync(ct);
            var rightFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => rightModIds.Contains(f.ModuleId))
                .Select(f => new { f.ModuleId, f.Path, f.ContentHash })
                .ToListAsync(ct);

            var leftByModule = leftFiles.GroupBy(f => f.ModuleId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Path, x => x.ContentHash));
            var rightByModule = rightFiles.GroupBy(f => f.ModuleId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Path, x => x.ContentHash));

            foreach (var appId in intersection)
            {
                var lm = leftByApp[appId];
                var rm = rightByApp[appId];
                var lf = leftByModule.GetValueOrDefault(lm.ModuleId, new Dictionary<string, string>());
                var rf = rightByModule.GetValueOrDefault(rm.ModuleId, new Dictionary<string, string>());

                var addedCount = rf.Keys.Count(p => !lf.ContainsKey(p));
                var removedCount = lf.Keys.Count(p => !rf.ContainsKey(p));
                var changedCount = lf.Count(kv => rf.TryGetValue(kv.Key, out var rh) && rh != kv.Value);

                if (addedCount == 0 && removedCount == 0 && changedCount == 0)
                {
                    continue; // module unchanged — drop from Changed bucket
                }
                changed.Add(new ModuleCompareEntry(
                    appId, lm.Name, lm.Publisher,
                    LeftModuleId: lm.ModuleId, LeftVersion: lm.Version,
                    RightModuleId: rm.ModuleId, RightVersion: rm.Version,
                    AddedFileCount: addedCount,
                    RemovedFileCount: removedCount,
                    ChangedFileCount: changedCount));
            }
        }

        added = added.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        removed = removed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        changed = changed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        _logger.LogInformation(
            "CompareReleases Left={Left} Right={Right} Added={Added} Removed={Removed} Changed={Changed}",
            leftReleaseId, rightReleaseId, added.Count, removed.Count, changed.Count);

        return new ReleaseCompareSummary(
            left.Id, left.Label, right.Id, right.Label, added, removed, changed);
    }

    private record ModuleCompareRow(long ModuleId, Guid AppId, string Name, string Publisher, string Version);

    private Task<List<ModuleCompareRow>> LoadModuleCompareRowsAsync(int releaseId, CancellationToken ct)
        => _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new ModuleCompareRow(m.Id, m.AppId, m.Name, m.Publisher, m.Version))
            .ToListAsync(ct);

    /// <summary>
    /// File-pair diff for one Changed module. Files are joined on canonical
    /// <c>Path</c>. Returns null when either module id is missing.
    /// </summary>
    public async Task<ModuleFileCompareResult?> CompareModuleFilesAsync(
        long leftModuleId, long rightModuleId, CancellationToken ct = default)
    {
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == leftModuleId || m.Id == rightModuleId)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);

        if (modules.Count < 2) return null;

        var leftFiles = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == leftModuleId)
            .Select(f => new { f.Id, f.Path, f.LineCount, f.ContentHash })
            .ToListAsync(ct);
        var rightFiles = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == rightModuleId)
            .Select(f => new { f.Id, f.Path, f.LineCount, f.ContentHash })
            .ToListAsync(ct);

        var leftByPath = leftFiles.ToDictionary(f => f.Path);
        var rightByPath = rightFiles.ToDictionary(f => f.Path);

        var added = new List<FileCompareEntry>();
        var removed = new List<FileCompareEntry>();
        var changed = new List<FileCompareEntry>();

        foreach (var path in rightByPath.Keys.Except(leftByPath.Keys).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var r = rightByPath[path];
            added.Add(new FileCompareEntry(path, null, r.Id, 0, r.LineCount));
        }
        foreach (var path in leftByPath.Keys.Except(rightByPath.Keys).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var l = leftByPath[path];
            removed.Add(new FileCompareEntry(path, l.Id, null, l.LineCount, 0));
        }
        foreach (var kv in leftByPath.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!rightByPath.TryGetValue(kv.Key, out var r)) continue;
            if (string.Equals(kv.Value.ContentHash, r.ContentHash, StringComparison.Ordinal)) continue;
            changed.Add(new FileCompareEntry(kv.Key, kv.Value.Id, r.Id, kv.Value.LineCount, r.LineCount));
        }

        var moduleName = modules.FirstOrDefault(m => m.Id == leftModuleId)?.Name
                         ?? modules.First().Name;

        return new ModuleFileCompareResult(
            leftModuleId, rightModuleId, moduleName, added, removed, changed);
    }

    /// <summary>
    /// Flat per-file rows for every Added / Removed / Modified pair across all
    /// modules in the two releases — the shape the Release-page Compare scope
    /// renders directly into its result table. Empty list when either release
    /// is missing.
    /// </summary>
    public async Task<List<ReleaseCompareFileRow>> CompareReleaseFilesFlatAsync(
        int leftReleaseId, int rightReleaseId, CancellationToken ct = default)
    {
        var summary = await CompareReleasesAsync(leftReleaseId, rightReleaseId, ct);
        if (summary is null) return new();

        var rows = new List<ReleaseCompareFileRow>();

        // Added / Removed modules: every file in that module is added/removed.
        var addedRightModuleIds = summary.Added.Where(m => m.RightModuleId.HasValue)
            .Select(m => m.RightModuleId!.Value).ToList();
        var removedLeftModuleIds = summary.Removed.Where(m => m.LeftModuleId.HasValue)
            .Select(m => m.LeftModuleId!.Value).ToList();

        if (addedRightModuleIds.Count > 0)
        {
            var addedFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => addedRightModuleIds.Contains(f.ModuleId))
                .Select(f => new { f.Id, f.Path, f.ModuleId, ModuleAppId = f.Module!.AppId, ModuleName = f.Module!.Name })
                .ToListAsync(ct);
            rows.AddRange(addedFiles.Select(f => new ReleaseCompareFileRow(
                f.ModuleAppId, f.ModuleName, f.Path, "added",
                LeftFileId: null, RightFileId: f.Id)));
        }
        if (removedLeftModuleIds.Count > 0)
        {
            var removedFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => removedLeftModuleIds.Contains(f.ModuleId))
                .Select(f => new { f.Id, f.Path, f.ModuleId, ModuleAppId = f.Module!.AppId, ModuleName = f.Module!.Name })
                .ToListAsync(ct);
            rows.AddRange(removedFiles.Select(f => new ReleaseCompareFileRow(
                f.ModuleAppId, f.ModuleName, f.Path, "removed",
                LeftFileId: f.Id, RightFileId: null)));
        }

        // Changed modules: pair files by path.
        foreach (var m in summary.Changed)
        {
            if (m.LeftModuleId is not { } lm || m.RightModuleId is not { } rm) continue;
            var pairs = await CompareModuleFilesAsync(lm, rm, ct);
            if (pairs is null) continue;

            foreach (var f in pairs.Added)
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "added",
                    LeftFileId: null, RightFileId: f.RightFileId));
            }
            foreach (var f in pairs.Removed)
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "removed",
                    LeftFileId: f.LeftFileId, RightFileId: null));
            }
            foreach (var f in pairs.Changed)
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "modified",
                    LeftFileId: f.LeftFileId, RightFileId: f.RightFileId));
            }
        }

        return rows
            .OrderBy(r => r.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Object-level diff between two releases, matched by <c>(Kind, ObjectId)</c>
    /// (or <c>(Kind, Name)</c> when an object has no id). Each object's
    /// source-slice <c>content_hash</c> decides added / removed / modified /
    /// unchanged. This is the matcher the legacy C/AL Base-vs-Customer compare
    /// uses — the module/AppId-keyed <see cref="CompareReleasesAsync"/> can't
    /// line two independent releases up because each carries a distinct AppId.
    /// </summary>
    public async Task<List<ObjectCompareRow>> CompareReleaseObjectsAsync(
        int leftReleaseId, int rightReleaseId, CancellationToken ct = default)
    {
        var left = await LoadCompareObjectsAsync(leftReleaseId, ct).ConfigureAwait(false);
        var right = await LoadCompareObjectsAsync(rightReleaseId, ct).ConfigureAwait(false);

        var leftByKey = ToKeyedMap(left);
        var rightByKey = ToKeyedMap(right);

        var rows = new List<ObjectCompareRow>();

        foreach (var (key, l) in leftByKey)
        {
            if (rightByKey.TryGetValue(key, out var r))
            {
                var status = l.Hash is not null && l.Hash == r.Hash ? "unchanged" : "modified";
                rows.Add(new ObjectCompareRow(l.Kind, l.ObjectId, l.Name, status, l.FileId, r.FileId));
            }
            else
            {
                rows.Add(new ObjectCompareRow(l.Kind, l.ObjectId, l.Name, "removed", l.FileId, null));
            }
        }
        foreach (var (key, r) in rightByKey)
        {
            if (!leftByKey.ContainsKey(key))
                rows.Add(new ObjectCompareRow(r.Kind, r.ObjectId, r.Name, "added", null, r.FileId));
        }

        return rows
            .OrderBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ObjectId ?? int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<CompareObject>> LoadCompareObjectsAsync(int releaseId, CancellationToken ct)
        => await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId)
            .Select(o => new CompareObject(
                o.Kind, o.ObjectId, o.Name, o.SourceFileId,
                o.SourceFile != null ? o.SourceFile.ContentHash : null))
            .ToListAsync(ct).ConfigureAwait(false);

    private static Dictionary<string, CompareObject> ToKeyedMap(IEnumerable<CompareObject> objects)
    {
        var map = new Dictionary<string, CompareObject>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            // (kind, object id) is unique within a release; fall back to name for
            // id-less objects (AL interfaces). First wins on the rare collision.
            var key = o.ObjectId is int id
                ? $"{o.Kind}#{id}"
                : $"{o.Kind}#name:{o.Name.ToLowerInvariant()}";
            map.TryAdd(key, o);
        }
        return map;
    }

    private sealed record CompareObject(string Kind, int? ObjectId, string Name, long? FileId, string? Hash);

    /// <summary>
    /// Releases other than the file's own that contain a file at the same
    /// <c>(AppId, Path)</c> — populates the "Compare with release" picker on
    /// the source-file viewer. Only ready Releases that actually carry a
    /// matching file are returned, keeping the dropdown dead-link-free.
    /// </summary>
    public async Task<List<CompareTargetOption>> GetCompareTargetsAsync(
        long fileId, CancellationToken ct = default)
    {
        var anchor = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.Path,
                AppId = f.Module!.AppId,
                ReleaseId = f.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (anchor is null) return new();

        return await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Path == anchor.Path
                && f.Module!.AppId == anchor.AppId
                && f.Module!.ReleaseId != anchor.ReleaseId
                && f.Module!.Release!.Status == "ready"
                && f.Module!.Release!.DeletedAt == null)
            .OrderBy(f => f.Module!.Release!.Label)
            .Select(f => new CompareTargetOption(
                f.Module!.ReleaseId,
                f.Module!.Release!.Label,
                f.Id))
            .ToListAsync(ct);
    }
}
