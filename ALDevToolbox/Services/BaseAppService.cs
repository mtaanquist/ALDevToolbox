using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Al;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read-side service for the Object Explorer (browse, view, search, diff).
/// All queries use <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{T}"/>
/// and rely on the multi-tenant query filter on <c>AppDbContext</c> to scope
/// to the current org.
/// </summary>
public class BaseAppService
{
    /// <summary>Cap on a single search query length; longer inputs are truncated.</summary>
    public const int MaxSearchQueryLength = 200;

    /// <summary>Default page size for the version-browser file list.</summary>
    public const int DefaultPageSize = 200;

    /// <summary>Cap on rows returned per search request.</summary>
    public const int MaxSearchResults = 500;

    private readonly AppDbContext _db;
    private readonly ILogger<BaseAppService> _logger;

    public BaseAppService(AppDbContext db, ILogger<BaseAppService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Returns all imported versions for the current org, newest release first.</summary>
    public Task<List<BaseAppVersion>> ListVersionsAsync(CancellationToken ct = default)
    {
        return _db.BaseAppVersions
            .AsNoTracking()
            .Where(v => v.DeletedAt == null)
            .OrderByDescending(v => v.Major)
            .ThenByDescending(v => v.CumulativeUpdate)
            .ToListAsync(ct);
    }

    /// <summary>Returns a single version row by id (with the linked catalogue row included), or <c>null</c>.</summary>
    public Task<BaseAppVersion?> GetVersionAsync(int id, CancellationToken ct = default)
    {
        return _db.BaseAppVersions
            .AsNoTracking()
            .Include(v => v.ApplicationVersion)
            .FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
    }

    /// <summary>
    /// Returns one page of files for the browser table, applying optional
    /// filter and search terms. Search hits content, object name, and object
    /// id. Content search is excluded so the browser stays focused on
    /// "find an object"; the file viewer is where full-text search will
    /// eventually live.
    /// </summary>
    public async Task<BaseAppFilePage> ListFilesAsync(
        int versionId, BaseAppFileFilter filter, int skip, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxSearchResults);
        skip = Math.Max(0, skip);

        var query = _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == versionId);

        if (!string.IsNullOrWhiteSpace(filter.ObjectType))
        {
            var type = filter.ObjectType.Trim().ToLowerInvariant();
            query = query.Where(f => f.ObjectType == type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Module))
        {
            var module = filter.Module.Trim();
            query = query.Where(f => f.Module == module);
        }

        if (!string.IsNullOrWhiteSpace(filter.Namespace))
        {
            var ns = filter.Namespace.Trim();
            query = query.Where(f => f.Namespace == ns);
        }

        if (filter.ExtensionId is { } extId)
        {
            query = query.Where(f => f.ExtensionId == extId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var trimmed = filter.Search.Trim();
            if (trimmed.Length > MaxSearchQueryLength)
            {
                trimmed = trimmed.Substring(0, MaxSearchQueryLength);
            }
            var pattern = "%" + trimmed + "%";
            int.TryParse(trimmed, out var idCandidate);
            query = query.Where(f =>
                EF.Functions.ILike(f.ObjectName, pattern)
                || (idCandidate != 0 && f.ObjectId == idCandidate));
        }

        var total = await query.CountAsync(ct);

        // Sort key chosen by the page; ascending or descending picked by the
        // header click. Falls back to a stable secondary by ObjectName so the
        // grid doesn't shuffle within ties between page loads.
        query = (filter.SortBy, filter.SortDescending) switch
        {
            (BaseAppFileSort.ObjectName, true) => query.OrderByDescending(f => f.ObjectName),
            (BaseAppFileSort.ObjectName, _) => query.OrderBy(f => f.ObjectName),
            (BaseAppFileSort.ObjectId, true) => query.OrderByDescending(f => f.ObjectId).ThenBy(f => f.ObjectName),
            (BaseAppFileSort.ObjectId, _) => query.OrderBy(f => f.ObjectId).ThenBy(f => f.ObjectName),
            (BaseAppFileSort.Module, true) => query.OrderByDescending(f => f.Module).ThenBy(f => f.ObjectName),
            (BaseAppFileSort.Module, _) => query.OrderBy(f => f.Module).ThenBy(f => f.ObjectName),
            (BaseAppFileSort.Namespace, true) => query.OrderByDescending(f => f.Namespace).ThenBy(f => f.ObjectName),
            (BaseAppFileSort.Namespace, _) => query.OrderBy(f => f.Namespace).ThenBy(f => f.ObjectName),
            (BaseAppFileSort.LineCount, true) => query.OrderByDescending(f => f.LineCount).ThenBy(f => f.ObjectName),
            (BaseAppFileSort.LineCount, _) => query.OrderBy(f => f.LineCount).ThenBy(f => f.ObjectName),
            (_, true) => query.OrderByDescending(f => f.ObjectType).ThenByDescending(f => f.ObjectName),
            _ => query.OrderBy(f => f.ObjectType).ThenBy(f => f.ObjectName),
        };

        var rows = await query
            .Skip(skip)
            .Take(take)
            // Don't haul Content over the wire on list views — it's only
            // needed in FileViewer. Project to a lightweight DTO.
            .Select(f => new BaseAppFileListRow(
                f.Id,
                f.VersionId,
                f.Path,
                f.FileName,
                f.Module,
                f.ObjectType,
                f.ObjectId,
                f.ObjectName,
                f.Namespace,
                f.LineCount,
                f.ExtensionId,
                f.Extension == null ? null : f.Extension.Name,
                f.Extension == null ? null : f.Extension.Publisher))
            .ToListAsync(ct);

        return new BaseAppFilePage(rows, total);
    }

    /// <summary>
    /// Substring-search procedure / event symbols in a version by name.
    /// Powers the "Procedures" mode of the version-browser search box.
    /// Uses the <c>ix_base_app_symbols_version_name_lower</c> functional
    /// index for the case-insensitive prefix, then ranks by name for
    /// stable pagination. Field rows and object-header rows are
    /// excluded — they have their own browse path (objects mode) and
    /// would clutter the results. Triggers are excluded for the same
    /// reason <see cref="FindReferencesAsync"/> excludes them: nothing
    /// calls them by name.
    /// </summary>
    public async Task<BaseAppProcedureSearchPage> SearchProceduresAsync(
        int versionId, string term, int skip, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxSearchResults);
        skip = Math.Max(0, skip);
        var trimmed = (term ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new BaseAppProcedureSearchPage(Array.Empty<BaseAppProcedureHit>(), 0);
        }
        if (trimmed.Length > MaxSearchQueryLength)
        {
            trimmed = trimmed.Substring(0, MaxSearchQueryLength);
        }
        var pattern = "%" + trimmed + "%";

        var query = _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.VersionId == versionId
                && s.Kind != "field"
                && s.Kind != "object_declaration"
                && s.Kind != "trigger"
                && EF.Functions.ILike(s.Name, pattern));

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Skip(skip)
            .Take(take)
            .Select(s => new BaseAppProcedureHit(
                s.Id,
                s.FileId,
                s.VersionId,
                s.File!.ObjectType,
                s.File.ObjectId,
                s.File.ObjectName,
                s.Kind,
                s.Name,
                s.LineNumber,
                s.Signature))
            .ToListAsync(ct);

        return new BaseAppProcedureSearchPage(rows, total);
    }

    /// <summary>
    /// Substring-search file content in a version. Powers the "Content"
    /// mode of the version-browser search box. Returns one hit per file
    /// (the first line that matches) plus a trimmed snippet so the page
    /// can show "X files, click for first match." Backed by the
    /// trigram GIN on <c>lower(content)</c>, so ILIKE %term% is fast
    /// even on a 60k-file BaseApp.
    /// </summary>
    public async Task<BaseAppContentSearchPage> SearchContentAsync(
        int versionId, string term, int skip, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxSearchResults);
        skip = Math.Max(0, skip);
        var trimmed = (term ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new BaseAppContentSearchPage(Array.Empty<BaseAppContentHit>(), 0);
        }
        if (trimmed.Length > MaxSearchQueryLength)
        {
            trimmed = trimmed.Substring(0, MaxSearchQueryLength);
        }
        var pattern = "%" + trimmed + "%";

        var query = _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == versionId
                && EF.Functions.ILike(f.Content, pattern));

        var total = await query.CountAsync(ct);

        // Pull the file rows; resolve first-match line + snippet in C#
        // because PG doesn't have a cheap "give me the first line that
        // matches" expression that's index-friendly. The trigram pass
        // already narrowed the set, so the per-file scan is bounded.
        var pageRows = await query
            .OrderBy(f => f.ObjectType).ThenBy(f => f.ObjectName).ThenBy(f => f.Id)
            .Skip(skip)
            .Take(take)
            .Select(f => new
            {
                f.Id,
                f.ObjectType,
                f.ObjectId,
                f.ObjectName,
                f.Path,
                f.Content,
            })
            .ToListAsync(ct);

        var hits = new List<BaseAppContentHit>(pageRows.Count);
        foreach (var f in pageRows)
        {
            var (lineNo, colStart, snippet) = FindFirstContentMatch(f.Content, trimmed);
            hits.Add(new BaseAppContentHit(
                FileId: f.Id,
                ObjectType: f.ObjectType,
                ObjectId: f.ObjectId,
                ObjectName: f.ObjectName,
                Path: f.Path,
                LineNumber: lineNo,
                ColumnStart: colStart,
                Snippet: snippet));
        }
        return new BaseAppContentSearchPage(hits, total);
    }

    /// <summary>
    /// Linear scan of <paramref name="content"/> for the first
    /// (case-insensitive) occurrence of <paramref name="term"/>. Returns
    /// the 1-based line / column of the match and a trimmed snippet of
    /// the surrounding line. Falls back to line 1 / column 1 with the
    /// content's first line if the term isn't found (shouldn't happen
    /// because ILIKE already pre-filtered, but it keeps the call safe).
    /// </summary>
    private static (int Line, int ColumnStart, string Snippet) FindFirstContentMatch(
        string content, string term)
    {
        if (string.IsNullOrEmpty(content)) return (1, 1, string.Empty);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                return (i + 1, idx + 1, TrimSnippet(lines[i]));
            }
        }
        return (1, 1, TrimSnippet(lines.Length > 0 ? lines[0] : string.Empty));
    }

    /// <summary>
    /// Returns the object-header symbol for a file (the one
    /// <c>AlSymbolExtractor</c> emits as <c>object_declaration</c>) so the
    /// version-browser row action can route to <see cref="FindReferencesAsync"/>
    /// without scrolling the user into the file first.
    /// </summary>
    public Task<BaseAppSymbol?> GetObjectDeclarationSymbolAsync(
        long fileId, CancellationToken ct = default)
    {
        return _db.BaseAppSymbols
            .AsNoTracking()
            .Include(s => s.File)
            .Include(s => s.Version)
            .Where(s => s.FileId == fileId && s.Kind == "object_declaration")
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Distinct namespaces in a version, sorted alphabetically.</summary>
    public Task<List<string>> ListNamespacesAsync(int versionId, CancellationToken ct = default)
    {
        return _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == versionId && f.Namespace != null && f.Namespace != string.Empty)
            .Select(f => f.Namespace!)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Extensions imported into a version, ordered by publisher then
    /// name. Drives the "Extension" filter dropdown on the version
    /// browser. Returns lightweight rows only (no Files navigation).
    /// </summary>
    public Task<List<BaseAppExtensionRow>> ListExtensionsAsync(
        int versionId, CancellationToken ct = default)
    {
        return _db.BaseAppExtensions
            .AsNoTracking()
            .Where(x => x.VersionId == versionId)
            .OrderBy(x => x.Publisher).ThenBy(x => x.Name)
            .Select(x => new BaseAppExtensionRow(
                x.Id, x.AppId, x.Name, x.Publisher, x.AppVersion))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Compares two imported versions and returns the object-level diff
    /// — files present in one side only (Added / Removed), plus files
    /// present in both whose <see cref="BaseAppFile.ContentHash"/>
    /// differs (Changed). Pairs match by <c>(ObjectType, ObjectId)</c>
    /// when both have an id, falling back to <c>(ObjectType, ObjectName)</c>
    /// for id-less objects (interfaces, some extensions).
    ///
    /// Falls back to a content compare when either side has no hash
    /// stamped — legacy imports predate the column. We pull
    /// <c>Content</c> only for those pairs to keep the payload bounded.
    /// </summary>
    public async Task<BaseAppVersionDiffResult> CompareVersionsAsync(
        int versionAId, int versionBId, CancellationToken ct = default)
    {
        if (versionAId == versionBId)
        {
            return BaseAppVersionDiffResult.Empty;
        }

        // Pull lightweight rows from both sides — Content stays in the DB
        // until we need it for a hash-missing fallback pair below.
        var rowsA = await ListFilesForCompareAsync(versionAId, ct);
        var rowsB = await ListFilesForCompareAsync(versionBId, ct);

        // Match key: (ObjectType, ObjectId) when id present, else
        // (ObjectType, ObjectName, "")
        var byKeyA = rowsA.ToDictionary(MatchKey, r => r, StringComparer.OrdinalIgnoreCase);
        var byKeyB = rowsB.ToDictionary(MatchKey, r => r, StringComparer.OrdinalIgnoreCase);

        var added = new List<BaseAppFileListRow>();
        var removed = new List<BaseAppFileListRow>();
        var changed = new List<BaseAppVersionDiffPair>();
        var fallbackPairs = new List<(BaseAppFileListRow Left, BaseAppFileListRow Right)>();

        foreach (var (key, b) in byKeyB)
        {
            if (!byKeyA.TryGetValue(key, out var a))
            {
                added.Add(b);
                continue;
            }
            // Both sides have hashes — cheap compare.
            if (a.ContentHash is { } ha && b.ContentHash is { } hb)
            {
                if (!string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase))
                {
                    changed.Add(new BaseAppVersionDiffPair(a, b));
                }
                continue;
            }
            // Either side missing a hash — fall back to a content
            // compare below. Defer the per-pair Content fetch so we
            // only pay for the pairs that need it.
            fallbackPairs.Add((a, b));
        }

        foreach (var (key, a) in byKeyA)
        {
            if (!byKeyB.ContainsKey(key))
            {
                removed.Add(a);
            }
        }

        if (fallbackPairs.Count > 0)
        {
            var fileIds = fallbackPairs
                .SelectMany(p => new[] { p.Left.Id, p.Right.Id })
                .Distinct()
                .ToList();
            var contents = await _db.BaseAppFiles
                .AsNoTracking()
                .Where(f => fileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Content })
                .ToDictionaryAsync(x => x.Id, x => x.Content, ct);
            foreach (var (a, b) in fallbackPairs)
            {
                var ca = contents.GetValueOrDefault(a.Id, string.Empty);
                var cb = contents.GetValueOrDefault(b.Id, string.Empty);
                if (!string.Equals(ca, cb, StringComparison.Ordinal))
                {
                    changed.Add(new BaseAppVersionDiffPair(a, b));
                }
            }
        }

        added = added.OrderBy(r => r.ObjectType).ThenBy(r => r.ObjectName).ToList();
        removed = removed.OrderBy(r => r.ObjectType).ThenBy(r => r.ObjectName).ToList();
        changed = changed.OrderBy(p => p.Right.ObjectType).ThenBy(p => p.Right.ObjectName).ToList();

        return new BaseAppVersionDiffResult(added, removed, changed);
    }

    private async Task<List<BaseAppFileListRow>> ListFilesForCompareAsync(
        int versionId, CancellationToken ct)
    {
        // Projection mirrors BaseAppFileListRow but adds ContentHash so
        // CompareVersionsAsync can drive the diff off the column. Kept
        // private — callers that don't need the hash go through
        // ListFilesAsync which projects the public DTO instead.
        return await _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == versionId)
            .Select(f => new BaseAppFileListRow(
                f.Id,
                f.VersionId,
                f.Path,
                f.FileName,
                f.Module,
                f.ObjectType,
                f.ObjectId,
                f.ObjectName,
                f.Namespace,
                f.LineCount,
                f.ExtensionId,
                f.Extension == null ? null : f.Extension.Name,
                f.Extension == null ? null : f.Extension.Publisher)
            {
                ContentHash = f.ContentHash,
            })
            .ToListAsync(ct);
    }

    private static string MatchKey(BaseAppFileListRow row)
    {
        // Prefer (Type, Id) — stable across renames. Falls back to
        // (Type, Name) for id-less objects.
        return row.ObjectId is { } id
            ? $"{row.ObjectType}|#{id}"
            : $"{row.ObjectType}|@{row.ObjectName}";
    }

    /// <summary>
    /// Distinct object types found in a version. Backs the type filter
    /// dropdown so the UI only shows types that actually exist in the data.
    /// </summary>
    public Task<List<string>> ListObjectTypesAsync(int versionId, CancellationToken ct = default)
    {
        return _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == versionId)
            .Select(f => f.ObjectType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Distinct module folders found in a version. Backs the module filter
    /// dropdown. Null/empty modules are filtered out.
    /// </summary>
    public Task<List<string>> ListModulesAsync(int versionId, CancellationToken ct = default)
    {
        return _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == versionId && f.Module != null && f.Module != string.Empty)
            .Select(f => f.Module!)
            .Distinct()
            .OrderBy(m => m)
            .ToListAsync(ct);
    }

    /// <summary>Returns a single file with content (for the read-only viewer), or <c>null</c>.</summary>
    public Task<BaseAppFile?> GetFileAsync(long id, CancellationToken ct = default)
    {
        return _db.BaseAppFiles
            .AsNoTracking()
            .Include(f => f.Version)
            .FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    /// <summary>
    /// Finds the same object in a different version. Matches by (Type, Id)
    /// first, falling back to (Type, Name). Returns <c>null</c> if neither
    /// match exists in the other version.
    /// </summary>
    public async Task<BaseAppFile?> FindCounterpartAsync(
        int otherVersionId, BaseAppFile file, CancellationToken ct = default)
    {
        if (file.ObjectId is { } objectId)
        {
            var byId = await _db.BaseAppFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.VersionId == otherVersionId
                    && f.ObjectType == file.ObjectType
                    && f.ObjectId == objectId, ct);
            if (byId is not null) return byId;
        }

        return await _db.BaseAppFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.VersionId == otherVersionId
                && f.ObjectType == file.ObjectType
                && f.ObjectName == file.ObjectName, ct);
    }

    /// <summary>
    /// Computes a side-by-side line diff between two file contents using
    /// DiffPlex. <paramref name="leftContent"/> is the "before" side,
    /// <paramref name="rightContent"/> the "after".
    /// </summary>
    public BaseAppDiffResult ComputeDiff(string leftContent, string rightContent)
    {
        var diff = SideBySideDiffBuilder.Diff(
            leftContent ?? string.Empty,
            rightContent ?? string.Empty,
            ignoreWhiteSpace: false,
            ignoreCase: false);

        return new BaseAppDiffResult(
            Map(diff.OldText.Lines),
            Map(diff.NewText.Lines));

        static IReadOnlyList<BaseAppDiffLine> Map(IReadOnlyList<DiffPiece> lines)
            => lines.Select(l => new BaseAppDiffLine(
                l.Position,
                l.Type switch
                {
                    ChangeType.Inserted => BaseAppDiffChange.Inserted,
                    ChangeType.Deleted => BaseAppDiffChange.Deleted,
                    ChangeType.Modified => BaseAppDiffChange.Modified,
                    ChangeType.Imaginary => BaseAppDiffChange.Imaginary,
                    _ => BaseAppDiffChange.Unchanged,
                },
                l.Text ?? string.Empty)).ToList();
    }

    /// <summary>
    /// Returns all symbols declared in the given file ordered by line, so
    /// the file viewer's CodeMirror mount can decorate each declaration's
    /// name token with a click affordance.
    /// </summary>
    public Task<List<BaseAppSymbol>> ListSymbolsInFileAsync(long fileId, CancellationToken ct = default)
    {
        return _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.FileId == fileId)
            .OrderBy(s => s.LineNumber)
            .ThenBy(s => s.ColumnStart)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns ranges in a file whose token text matches a name the symbol
    /// index can resolve to a definition — object names anywhere in the
    /// version plus procedure/trigger/event symbols callable from this file.
    /// Used by the file viewer to draw the "this is jumpable" underline on
    /// reference sites; the actual <see cref="GoToDefinitionAsync"/> still
    /// runs server-side when the user clicks.
    /// </summary>
    public async Task<List<ResolvableTokenRange>> ListResolvableTokensInFileAsync(
        long fileId, CancellationToken ct = default)
    {
        var file = await _db.BaseAppFiles
            .AsNoTracking()
            .Select(f => new { f.Id, f.VersionId, f.Content })
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return new List<ResolvableTokenRange>();

        var objectNames = await _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == file.VersionId)
            .Select(f => f.ObjectName)
            .Distinct()
            .ToListAsync(ct);

        // Cross-file callable symbols. Skip `local_procedure` (only callable
        // in their own file), `trigger` (fired by the framework, never
        // called by name), `field` (resolved separately via the per-table
        // vocabulary) and `object_declaration` (already covered by the
        // ObjectNames bucket) so we don't paint underlines on tokens that
        // wouldn't actually navigate anywhere.
        var publicSymbolNames = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.VersionId == file.VersionId
                && s.Kind != "local_procedure"
                && s.Kind != "trigger"
                && s.Kind != "field"
                && s.Kind != "object_declaration")
            .Select(s => s.Name)
            .Distinct()
            .ToListAsync(ct);

        var localSymbolNames = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.FileId == fileId
                && s.Kind != "trigger"
                && s.Kind != "field"
                && s.Kind != "object_declaration")
            .Select(s => s.Name)
            .Distinct()
            .ToListAsync(ct);

        // Fields of this file's own table (table / tableextension files only).
        // Quoted occurrences of these names anywhere in the file resolve as
        // intra-table field references; `Rec.` / `xRec.` access does too.
        var ownFieldNames = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.FileId == fileId && s.Kind == "field")
            .Select(s => s.Name)
            .Distinct()
            .ToListAsync(ct);

        // For every `Var: Record "Table"` in the file, pre-resolve the
        // table's field-name set so `Var."FieldName"` can be underlined in
        // a single pass.
        var fieldsByVariable = await BuildFieldsByVariableAsync(
            file.Content ?? string.Empty, file.VersionId, ct);

        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in objectNames) if (!string.IsNullOrEmpty(n)) objects.Add(n);

        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in publicSymbolNames) if (!string.IsNullOrEmpty(n)) symbols.Add(n);
        foreach (var n in localSymbolNames) if (!string.IsNullOrEmpty(n)) symbols.Add(n);

        var ownFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in ownFieldNames) if (!string.IsNullOrEmpty(n)) ownFields.Add(n);

        var vocab = new ResolvableVocabulary(objects, symbols, ownFields, fieldsByVariable);
        return AlResolvableTokenScanner.Scan(file.Content ?? string.Empty, vocab).ToList();
    }

    /// <summary>
    /// Walks the file for <c>VarName: Record "Table"</c> declarations and
    /// returns a map from <c>VarName</c> to the field-name set of the
    /// referenced table (looked up once per distinct table name in the
    /// current version). Empty when the file has no Record variables.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> BuildFieldsByVariableAsync(
        string fileContent, int versionId, CancellationToken ct)
    {
        var varToTable = AlGoToDefinitionLocator.ResolveAllRecordVariableTypes(fileContent);
        if (varToTable.Count == 0)
        {
            return new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var distinctTables = varToTable.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One query: every field symbol whose declaring file's object name
        // is one of the tables we care about. Filtering on ObjectType lets
        // a table extension's fields contribute too — but they live in a
        // different file, so we'd need to union. For v1 just match by
        // object name; tableextension-only fields will appear once we add
        // a second pass.
        var rows = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.VersionId == versionId
                && s.Kind == "field"
                && s.File != null
                && distinctTables.Contains(s.File.ObjectName))
            .Select(s => new { TableName = s.File!.ObjectName, FieldName = s.Name })
            .ToListAsync(ct);

        var fieldsByTable = rows
            .GroupBy(r => r.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<string>)new HashSet<string>(
                    g.Select(r => r.FieldName), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (varName, tableName) in varToTable)
        {
            if (fieldsByTable.TryGetValue(tableName, out var fields))
            {
                result[varName] = fields;
            }
        }
        return result;
    }

    /// <summary>
    /// Finds every line in <paramref name="fileId"/> whose source contains the
    /// supplied <paramref name="word"/> with word-boundary delimiters.
    /// Backs the inspector panel's "Find in this file" gesture — useful for
    /// variables, fields, and labels that <c>BaseAppSymbol</c> doesn't index.
    /// Quoted-identifier matching falls out of the same regex because AL
    /// quotes are treated as boundary characters by <c>\b</c>.
    /// </summary>
    public async Task<FileSearchResult> FindInFileAsync(
        long fileId, string word, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return new FileSearchResult(word ?? string.Empty, Array.Empty<FileSearchHit>());
        }

        var file = await _db.BaseAppFiles
            .AsNoTracking()
            .Select(f => new { f.Id, f.Content })
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return new FileSearchResult(word, Array.Empty<FileSearchHit>());

        var escaped = System.Text.RegularExpressions.Regex.Escape(word);
        // Quoted AL identifiers can contain spaces/hyphens, so anchor on
        // either word boundaries or an opening/closing quote.
        var pattern = $@"(?<![A-Za-z0-9_]){escaped}(?![A-Za-z0-9_])";
        var rx = new System.Text.RegularExpressions.Regex(
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

        var hits = new List<FileSearchHit>();
        var lines = (file.Content ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = rx.Match(lines[i]);
            if (!match.Success) continue;
            hits.Add(new FileSearchHit(
                LineNumber: i + 1,
                ColumnStart: match.Index + 1,
                Snippet: TrimSnippet(lines[i])));
        }
        return new FileSearchResult(word, hits);
    }

    /// <summary>Returns one symbol by id (with its file + version loaded) or <c>null</c>.</summary>
    public Task<BaseAppSymbol?> GetSymbolAsync(long id, CancellationToken ct = default)
    {
        return _db.BaseAppSymbols
            .AsNoTracking()
            .Include(s => s.File)
            .Include(s => s.Version)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// Finds every place in the symbol's version that references
    /// <see cref="BaseAppSymbol.Name"/>. The exact shape of "reference"
    /// depends on the symbol kind — procedures match <c>name(</c>, fields
    /// match <c>"name"</c> and <c>.name</c>, object declarations match the
    /// quoted-or-bare object name in any context. Hits are classified by
    /// confidence so the UI can split likely matches from possibly-related
    /// ones (different overloads / different table's same-name field /
    /// unrelated identifier collision).
    /// </summary>
    public async Task<BaseAppReferenceResult> FindReferencesAsync(long symbolId, CancellationToken ct = default)
    {
        var symbol = await GetSymbolAsync(symbolId, ct);
        if (symbol is null || symbol.File is null)
        {
            return BaseAppReferenceResult.Empty;
        }

        var declaringObjectName = symbol.File.ObjectName;
        // Other overloads on the same object — the UI surfaces "N overloads"
        // and the references query covers all of them via the shared name.
        var overloadCount = await _db.BaseAppSymbols
            .AsNoTracking()
            .CountAsync(s => s.FileId == symbol.FileId
                && s.Kind == symbol.Kind
                && s.Name == symbol.Name, ct);

        var escapedName = System.Text.RegularExpressions.Regex.Escape(symbol.Name);
        var quotedName = "\"" + symbol.Name + "\"";

        // Kind-specific search shapes. Procedures match call sites,
        // fields match quoted occurrences and bare dot-qualified access,
        // object declarations match quoted references in any context.
        // local_procedure is scoped to its declaring file because it can
        // never be called from elsewhere.
        // Fields and object declarations are referenced by their *quoted*
        // form virtually everywhere — `Validate("No.")`, `Rec."No."`,
        // `Codeunit "Sales-Post"`, `tabledata "Sales Header"`. Matching the
        // bare identifier would drag in every variable named after a field
        // (`No`, `Description`, …) without a way to filter false positives.
        var quotedRegex = new System.Text.RegularExpressions.Regex(
            "\"" + escapedName + "\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);
        var procRegex = new System.Text.RegularExpressions.Regex(
            $@"\b{escapedName}\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

        var (ilikePattern, lineRegex, scopeToFileOnly) = symbol.Kind switch
        {
            "field" or "object_declaration" => ("%" + quotedName + "%", quotedRegex, false),
            "local_procedure" => ("%" + symbol.Name + "(%", procRegex, true),
            _ => ("%" + symbol.Name + "(%", procRegex, false),
        };

        // Coarse candidate pass via ILIKE: trigram GIN on lower(content) is
        // already in place, so substring matches are fast even on a real
        // Base Application. Fine-grained word-boundary check happens in C#
        // afterwards so a procedure called "Post" doesn't drag in "Posting"
        // or "Reposted". `local_procedure` short-circuits to the declaring
        // file — by definition the only caller is the same file.
        var candidateFiles = await _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => scopeToFileOnly
                ? f.Id == symbol.FileId
                : f.VersionId == symbol.VersionId)
            .Where(f => EF.Functions.ILike(f.Content, ilikePattern))
            .Select(f => new
            {
                f.Id,
                f.ObjectType,
                f.ObjectId,
                f.ObjectName,
                f.Path,
                f.Content,
            })
            .ToListAsync(ct);

        var likely = new List<BaseAppReferenceHit>();
        var possiblyRelated = new List<BaseAppReferenceHit>();

        // Declaration lines in the declaring file (so we can drop them
        // without an inner-loop DB round-trip). Same kind + name covers all
        // overloads on this file.
        var declarationLines = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.FileId == symbol.FileId
                && s.Kind == symbol.Kind
                && s.Name == symbol.Name)
            .Select(s => s.LineNumber)
            .ToListAsync(ct);
        var declarationLineSet = new HashSet<int>(declarationLines);

        foreach (var file in candidateFiles)
        {
            var lines = file.Content.Replace("\r\n", "\n").Split('\n');
            // Compute the file's var-type map once and reuse for every hit
            // — receiver-typed classification is what suppresses
            // `ErrorInfo.Create(...)` etc. when searching for an unrelated
            // codeunit's `Create` procedure.
            var objectVarMap = AlGoToDefinitionLocator.ResolveAllObjectVariableTypes(file.Content);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = lineRegex.Match(lines[i]);
                if (!match.Success) continue;

                // Skip the declaration line itself (file is the declaring
                // file and the line number matches the overload / field row).
                if (file.Id == symbol.FileId && declarationLineSet.Contains(i + 1))
                {
                    continue;
                }

                var confidence = ClassifyHitConfidence(
                    symbol.Kind, file.ObjectName, declaringObjectName, file.Content,
                    lines[i], match.Index, objectVarMap);
                if (confidence is null) continue;

                var hit = new BaseAppReferenceHit(
                    FileId: file.Id,
                    ObjectType: file.ObjectType,
                    ObjectId: file.ObjectId,
                    ObjectName: file.ObjectName,
                    Path: file.Path,
                    LineNumber: i + 1,
                    ColumnStart: match.Index + 1,
                    Snippet: TrimSnippet(lines[i]),
                    Confidence: confidence.Value);

                if (confidence == BaseAppReferenceConfidence.PossiblyRelated)
                {
                    possiblyRelated.Add(hit);
                }
                else
                {
                    likely.Add(hit);
                }
            }
        }

        // Order: same-object calls first, then qualified, then by file +
        // line. Possibly-related at the end of their bucket.
        static int Rank(BaseAppReferenceConfidence c) => c switch
        {
            BaseAppReferenceConfidence.SameObject => 0,
            BaseAppReferenceConfidence.Qualified => 1,
            _ => 2,
        };
        likely = likely
            .OrderBy(h => Rank(h.Confidence))
            .ThenBy(h => h.ObjectName)
            .ThenBy(h => h.LineNumber)
            .ToList();
        possiblyRelated = possiblyRelated
            .OrderBy(h => h.ObjectName)
            .ThenBy(h => h.LineNumber)
            .ToList();

        return new BaseAppReferenceResult(
            Symbol: symbol,
            OverloadCount: overloadCount,
            Likely: likely,
            PossiblyRelated: possiblyRelated);
    }

    private static readonly System.Text.RegularExpressions.Regex FieldDeclarationLineRegex = new(
        @"^\s*field\s*\(\s*\d+\s*;\s*""(?<name>[^""]+)""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ProcedureDeclarationLineRegex = new(
        @"^\s*(?:local\s+|internal\s+|protected\s+|business\s+)?(?:procedure|trigger)\s+""?(?<name>[^""\s\(]+)""?\s*\(",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Whole-file fallback confidence used only when the matched call site
    /// has no usable left-context qualifier (bare <c>Name(</c> with no
    /// receiver). Looks at the file as a whole because AL's typed-var
    /// declaration (<c>Codeunit "Sales-Post"</c>) usually lives away from
    /// the call site.
    /// </summary>
    private static BaseAppReferenceConfidence ClassifyConfidence(
        string callerObjectName, string declaringObjectName, string fileContent)
    {
        if (string.Equals(callerObjectName, declaringObjectName, StringComparison.Ordinal))
        {
            return BaseAppReferenceConfidence.SameObject;
        }

        if (fileContent.Contains('"' + declaringObjectName + '"', StringComparison.Ordinal))
        {
            return BaseAppReferenceConfidence.Qualified;
        }

        if (!declaringObjectName.Contains(' ') && !declaringObjectName.Contains('-')
            && fileContent.Contains(declaringObjectName, StringComparison.Ordinal))
        {
            return BaseAppReferenceConfidence.Qualified;
        }

        return BaseAppReferenceConfidence.PossiblyRelated;
    }

    /// <summary>
    /// Decides whether a single regex hit at <paramref name="matchIndex"/>
    /// on <paramref name="lineText"/> is really a reference to the declared
    /// symbol, and at what confidence. Returns <c>null</c> to drop the hit
    /// entirely — used when the call-site qualifier resolves to a different
    /// object, when the qualifier is a system-type-named variable
    /// (<c>HttpClient: HttpClient</c>) that shares a name with the declaring
    /// codeunit, or when the matched line is another object's declaration
    /// of the same identifier.
    ///
    /// Known limitations: method chaining <c>A.B().C()</c> reads as a bare
    /// call (no left-context operator on <c>C</c>) and falls back to the
    /// whole-file heuristic. Dynamic dispatch through interface variables
    /// is dropped unless the interface type name matches the declaring
    /// codeunit name — without a real type checker we can't follow it.
    /// </summary>
    private static BaseAppReferenceConfidence? ClassifyHitConfidence(
        string symbolKind, string callerObjectName, string declaringObjectName,
        string fileContent, string lineText, int matchIndex,
        IReadOnlyDictionary<string, ResolvedVariableType> objectVarMap)
    {
        var isSameFile = string.Equals(callerObjectName, declaringObjectName, StringComparison.Ordinal);

        if (symbolKind == "field" || symbolKind == "object_declaration")
        {
            return ClassifyFieldOrObjectHit(
                symbolKind, isSameFile, declaringObjectName, fileContent,
                lineText, matchIndex, objectVarMap);
        }

        // Procedure / trigger / event kinds.
        // Drop another object's declaration of a same-named procedure.
        if (!isSameFile)
        {
            var procDecl = ProcedureDeclarationLineRegex.Match(lineText);
            if (procDecl.Success
                && string.Equals(procDecl.Groups["name"].Value, ExtractProcedureName(lineText, matchIndex),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var leftContext = AlGoToDefinitionLocator.ReadLeftContextAt(lineText, matchIndex);

        if (leftContext.Operator == "::" || leftContext.Operator == "keyword")
        {
            // `Codeunit::"Foo"` / `Codeunit Foo` — that's an object reference,
            // not a call to a procedure named Foo.
            return null;
        }

        if (leftContext.Operator == ".")
        {
            return ClassifyDotQualifiedProcedureHit(
                isSameFile, declaringObjectName, leftContext.Qualifier, objectVarMap);
        }

        // Bare `Name(` with no qualifier — could be a same-object self-call
        // (SameObject), a `with` block, a parameter, or just a different
        // procedure that happens to share a name. Fall back to the whole-file
        // heuristic so we keep current behaviour for the ambiguous case.
        return ClassifyConfidence(callerObjectName, declaringObjectName, fileContent);
    }

    private static BaseAppReferenceConfidence? ClassifyDotQualifiedProcedureHit(
        bool isSameFile, string declaringObjectName, string? qualifier,
        IReadOnlyDictionary<string, ResolvedVariableType> objectVarMap)
    {
        if (string.IsNullOrEmpty(qualifier)) return BaseAppReferenceConfidence.PossiblyRelated;

        // Quoted qualifier `"Sales-Post".Post(...)` — literal object name.
        // We can match it exactly against the declaring object.
        if (qualifier.Contains(' ') || qualifier.Contains('-'))
        {
            return string.Equals(qualifier, declaringObjectName, StringComparison.Ordinal)
                ? BaseAppReferenceConfidence.Qualified
                : null;
        }

        if (objectVarMap.TryGetValue(qualifier, out var resolved))
        {
            // The qualifier IS a declared variable. If it's typed to an AL
            // object whose name matches the declaring object, we have a
            // real reference. Anything else is a confirmed different type
            // — drop it. `var HttpClient: HttpClient` falls into the "no
            // keyword" branch and is also dropped, even when the type name
            // happens to match the declaring codeunit (the var is a
            // runtime/system type, not the codeunit with the same name).
            if (resolved.Keyword is not null
                && string.Equals(resolved.TypeName, declaringObjectName, StringComparison.Ordinal))
            {
                return isSameFile
                    ? BaseAppReferenceConfidence.SameObject
                    : BaseAppReferenceConfidence.Qualified;
            }
            return null;
        }

        // Unresolved bare identifier qualifier. Could be a parameter, a
        // local var the regex missed, or a static call on a same-named
        // system type. When it doesn't even share a name with the
        // declaring object, it's almost certainly unrelated — drop.
        if (!string.Equals(qualifier, declaringObjectName, StringComparison.Ordinal))
        {
            return null;
        }

        // Name matches the declaring object but no var declaration backs it
        // up — possibly a procedure-parameter typed to the codeunit, or a
        // static call on a system type that shares the name. Keep it
        // visible but in the lower-confidence bucket.
        return BaseAppReferenceConfidence.PossiblyRelated;
    }

    private static BaseAppReferenceConfidence? ClassifyFieldOrObjectHit(
        string symbolKind, bool isSameFile, string declaringObjectName, string fileContent,
        string lineText, int matchIndex,
        IReadOnlyDictionary<string, ResolvedVariableType> objectVarMap)
    {
        if (symbolKind == "object_declaration")
        {
            // Object-name references are quoted-or-keyword everywhere
            // (`Codeunit "X"`, `Codeunit::"X"`, `"X".Method(...)`). The
            // existing whole-file heuristic already handles them well.
            return isSameFile
                ? BaseAppReferenceConfidence.SameObject
                : ClassifyConfidence(declaringObjectName, declaringObjectName, fileContent);
        }

        // Field hits. Strip out another table's `field(N; "Name"; ...)`
        // declarations — those are *declarations*, not references.
        if (!isSameFile)
        {
            var fieldDecl = FieldDeclarationLineRegex.Match(lineText);
            if (fieldDecl.Success
                && string.Equals(fieldDecl.Groups["name"].Value,
                    ExtractQuotedFieldName(lineText, matchIndex), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var leftContext = AlGoToDefinitionLocator.ReadLeftContextAt(lineText, matchIndex);

        if (leftContext.Operator == ".")
        {
            var qualifier = leftContext.Qualifier;
            if (string.IsNullOrEmpty(qualifier))
            {
                return BaseAppReferenceConfidence.PossiblyRelated;
            }

            // Quoted qualifier `"Sales Header"."No." := ''`.
            if (qualifier.Contains(' ') || qualifier.Contains('-'))
            {
                return string.Equals(qualifier, declaringObjectName, StringComparison.Ordinal)
                    ? BaseAppReferenceConfidence.Qualified
                    : null;
            }

            if (objectVarMap.TryGetValue(qualifier, out var resolved))
            {
                if (string.Equals(resolved.TypeName, declaringObjectName, StringComparison.Ordinal)
                    && (resolved.Keyword is null
                        || string.Equals(resolved.Keyword, "Record", StringComparison.OrdinalIgnoreCase)))
                {
                    return isSameFile
                        ? BaseAppReferenceConfidence.SameObject
                        : BaseAppReferenceConfidence.Qualified;
                }
                return null;
            }

            // Unresolved qualifier. Same logic as procedures: drop unless
            // the bare name happens to match the declaring table.
            if (!string.Equals(qualifier, declaringObjectName, StringComparison.Ordinal))
            {
                return null;
            }
            return BaseAppReferenceConfidence.PossiblyRelated;
        }

        // No `.` qualifier on this hit: intra-table `key(Key1; "Code")` and
        // `fieldgroup(Brick; "Code", …)` references, message captions
        // (`'"Code" is required'`), or report `RequestFilterFields = "Code"`.
        if (isSameFile)
        {
            return BaseAppReferenceConfidence.SameObject;
        }

        if (fileContent.Contains('"' + declaringObjectName + '"', StringComparison.Ordinal))
        {
            return BaseAppReferenceConfidence.Qualified;
        }

        return BaseAppReferenceConfidence.PossiblyRelated;
    }

    /// <summary>
    /// Returns the procedure name that the regex matched at
    /// <paramref name="matchIndex"/> by reading forward from the match
    /// position until a non-identifier char. Used to verify a procedure
    /// declaration line on the same line as the regex match isn't being
    /// mistaken for a call (when both the matcher and the declaration regex
    /// could match the same line).
    /// </summary>
    private static string ExtractProcedureName(string lineText, int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= lineText.Length) return string.Empty;
        var end = matchIndex;
        while (end < lineText.Length
            && (char.IsLetterOrDigit(lineText[end]) || lineText[end] == '_'))
        {
            end++;
        }
        return lineText.Substring(matchIndex, end - matchIndex);
    }

    /// <summary>
    /// Extracts the field name inside the quotes that the regex matched at
    /// <paramref name="matchIndex"/> (which points at the opening quote).
    /// </summary>
    private static string ExtractQuotedFieldName(string lineText, int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= lineText.Length) return string.Empty;
        if (lineText[matchIndex] != '"') return string.Empty;
        var close = lineText.IndexOf('"', matchIndex + 1);
        if (close < 0) return string.Empty;
        return lineText.Substring(matchIndex + 1, close - matchIndex - 1);
    }

    private const int SnippetMaxLength = 240;
    private static string TrimSnippet(string line)
    {
        // Trim both sides — leading whitespace on indented call sites adds
        // nothing readable to the snippet column and wastes horizontal space
        // in the inspector panel.
        var trimmed = line.Trim();
        if (trimmed.Length <= SnippetMaxLength) return trimmed;
        return trimmed.Substring(0, SnippetMaxLength) + "…";
    }

    /// <summary>
    /// Resolves a "Go to definition" click in the file viewer. Walks the
    /// click position via <see cref="AlGoToDefinitionLocator"/>, then asks
    /// the symbol / file tables for the matching declaration. Returns
    /// <c>null</c> when nothing resolves; callers treat that as a no-op so
    /// the user doesn't get a jarring redirect after a stray Ctrl-click.
    /// </summary>
    public async Task<GoToDefinitionTarget?> GoToDefinitionAsync(
        long fileId, int line, int column, CancellationToken ct = default)
    {
        var file = await _db.BaseAppFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return null;

        var click = AlGoToDefinitionLocator.Inspect(file.Content, line, column);
        if (click is null) return null;

        // ── Case A: the click sits in an object-reference context.
        //
        //   * `Codeunit "Sales-Post"`            (LeftContext = keyword Codeunit)
        //   * `Codeunit::"Sales-Post"`           (LeftContext = ::)
        //   * `"Sales-Post".Post(...)` — Word is "Sales-Post" with no left ctx,
        //     but the right side is a method access. We catch this via a
        //     fallback check below.
        var isObjectKeywordContext = click.LeftContext.Operator == "keyword"
            || click.LeftContext.Operator == "::";
        if (isObjectKeywordContext)
        {
            return await ResolveObjectByNameAsync(file.VersionId, click.Word, ct);
        }

        // ── Case B: qualified call `X.Word(...)` — Word is the procedure
        // name, Qualifier is the type or variable identifier we need to
        // resolve to a declaring object.
        if (click.LeftContext.Operator == "." && click.LeftContext.Qualifier is { } qualifier)
        {
            // The qualifier may itself be an object name (`"Sales-Post".Post()`)
            // or a variable typed as one (`SalesPostCu.Post()`).
            string declaringObjectName = qualifier;
            var asVar = AlGoToDefinitionLocator.ResolveVariableType(file.Content, qualifier);
            if (asVar is not null) declaringObjectName = asVar;

            var declaringFile = await _db.BaseAppFiles
                .AsNoTracking()
                .Where(f => f.VersionId == file.VersionId && f.ObjectName == declaringObjectName)
                .Select(f => new { f.Id, f.ObjectType, f.ObjectName })
                .FirstOrDefaultAsync(ct);

            if (declaringFile is not null)
            {
                // Prefer a field match over a procedure with the same name —
                // `Rec."No."` and `SalesHdr."No."` are field accesses, and
                // BaseApp does happen to have procedures called `No` too.
                var fieldSymbol = await _db.BaseAppSymbols
                    .AsNoTracking()
                    .Where(s => s.FileId == declaringFile.Id
                        && s.Kind == "field"
                        && s.Name == click.Word)
                    .OrderBy(s => s.LineNumber)
                    .FirstOrDefaultAsync(ct);
                if (fieldSymbol is not null)
                {
                    return new GoToDefinitionTarget(
                        declaringFile.Id, fieldSymbol.LineNumber,
                        $"field {fieldSymbol.Name} on \"{declaringObjectName}\"");
                }

                var symbol = await _db.BaseAppSymbols
                    .AsNoTracking()
                    .Where(s => s.FileId == declaringFile.Id
                        && s.Kind != "field"
                        && s.Kind != "object_declaration"
                        && s.Name == click.Word)
                    .OrderBy(s => s.LineNumber)
                    .FirstOrDefaultAsync(ct);
                if (symbol is not null)
                {
                    return new GoToDefinitionTarget(
                        declaringFile.Id, symbol.LineNumber,
                        $"procedure {symbol.Name} on \"{declaringObjectName}\"");
                }
                // Last-resort fallback — send the user to the declaring
                // file's header. Almost never hit now that fields are
                // indexed, but kept for the unusual case of a member that
                // we haven't extracted (e.g. a page action's procedure).
                return new GoToDefinitionTarget(
                    declaringFile.Id, 1,
                    $"{declaringFile.ObjectType} \"{declaringFile.ObjectName}\"");
            }
            // Fall through to bare-name search if the qualifier didn't resolve.
        }

        // ── Case C: same-line member access where the word is the object,
        // not the procedure (e.g. click landed on `Sales-Post` in
        // `"Sales-Post".Post(...)`). Detect by looking at what's just after
        // the word on the line.
        if (LooksLikeObjectFollowedByMember(click.LineText, click.Word))
        {
            var target = await ResolveObjectByNameAsync(file.VersionId, click.Word, ct);
            if (target is not null) return target;
        }

        // ── Case D: unqualified — search same file first, then version-wide.
        // Field declarations and the file's own object declaration take
        // priority over procedure matches here because in a table file the
        // user is almost always clicking a field name when there's no left
        // context. The Rec / xRec pseudo-variables route through the
        // dot-qualified path above; this branch catches bare `"No."` uses.
        var sameFileFieldSymbol = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.FileId == fileId
                && s.Kind == "field"
                && s.Name == click.Word)
            .OrderBy(s => s.LineNumber)
            .FirstOrDefaultAsync(ct);
        if (sameFileFieldSymbol is not null)
        {
            return new GoToDefinitionTarget(
                fileId, sameFileFieldSymbol.LineNumber, $"field {sameFileFieldSymbol.Name}");
        }

        // Clicking the object name on the declaration line resolves to
        // itself — primarily useful for keyboard navigation symmetry, and
        // for the right-click "Find references" entry on the underlined
        // declaration token to work.
        var sameFileObjectDecl = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.FileId == fileId
                && s.Kind == "object_declaration"
                && s.Name == click.Word)
            .FirstOrDefaultAsync(ct);
        if (sameFileObjectDecl is not null)
        {
            return new GoToDefinitionTarget(
                fileId, sameFileObjectDecl.LineNumber,
                $"\"{sameFileObjectDecl.Name}\"");
        }

        var sameFileSymbol = await _db.BaseAppSymbols
            .AsNoTracking()
            .Where(s => s.FileId == fileId
                && s.Kind != "field"
                && s.Kind != "object_declaration"
                && s.Name == click.Word)
            .OrderBy(s => s.LineNumber)
            .FirstOrDefaultAsync(ct);
        if (sameFileSymbol is not null)
        {
            return new GoToDefinitionTarget(
                fileId, sameFileSymbol.LineNumber, $"procedure {sameFileSymbol.Name}");
        }

        var anySymbol = await _db.BaseAppSymbols
            .AsNoTracking()
            .Include(s => s.File)
            .Where(s => s.VersionId == file.VersionId
                && s.Kind != "field"
                && s.Kind != "object_declaration"
                && s.Name == click.Word)
            .OrderBy(s => s.FileId)
            .ThenBy(s => s.LineNumber)
            .FirstOrDefaultAsync(ct);
        if (anySymbol is not null)
        {
            return new GoToDefinitionTarget(
                anySymbol.FileId, anySymbol.LineNumber,
                anySymbol.File is null
                    ? $"procedure {anySymbol.Name}"
                    : $"procedure {anySymbol.Name} on \"{anySymbol.File.ObjectName}\"");
        }

        // Word may itself be an object name even without keyword context —
        // covers `"Sales-Post"` standing alone (e.g. inside a comment-link
        // or a string literal we still want to follow).
        return await ResolveObjectByNameAsync(file.VersionId, click.Word, ct);
    }

    private async Task<GoToDefinitionTarget?> ResolveObjectByNameAsync(
        int versionId, string objectName, CancellationToken ct)
    {
        var match = await _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == versionId && f.ObjectName == objectName)
            .Select(f => new { f.Id, f.ObjectType, f.ObjectName })
            .FirstOrDefaultAsync(ct);
        if (match is null) return null;
        return new GoToDefinitionTarget(
            match.Id, 1, $"{match.ObjectType} \"{match.ObjectName}\"");
    }

    private static bool LooksLikeObjectFollowedByMember(string lineText, string word)
    {
        var idx = lineText.IndexOf(word, StringComparison.Ordinal);
        if (idx < 0) return false;
        var after = idx + word.Length;
        // Quoted: skip a trailing `"`.
        if (after < lineText.Length && lineText[after] == '"') after++;
        while (after < lineText.Length && char.IsWhiteSpace(lineText[after])) after++;
        return after < lineText.Length && lineText[after] == '.';
    }
}

/// <summary>Filter inputs from the version-browser table.</summary>
public sealed record BaseAppFileFilter(
    string? ObjectType = null,
    string? Module = null,
    string? Search = null,
    string? Namespace = null,
    long? ExtensionId = null,
    BaseAppFileSort SortBy = BaseAppFileSort.ObjectType,
    bool SortDescending = false)
{
    public static BaseAppFileFilter Empty { get; } = new();
}

/// <summary>Sortable columns on the version-browser file table.</summary>
public enum BaseAppFileSort
{
    ObjectType,
    ObjectId,
    ObjectName,
    Module,
    Namespace,
    LineCount,
}

/// <summary>One page of file rows plus the unfiltered total count.</summary>
public sealed record BaseAppFilePage(IReadOnlyList<BaseAppFileListRow> Rows, int TotalCount);

/// <summary>
/// Object-level diff between two imported versions. Rows in
/// <see cref="Added"/> exist in version B only; rows in
/// <see cref="Removed"/> exist in version A only. Pairs in
/// <see cref="Changed"/> exist in both and have differing content
/// hashes (or differing content when either hash is missing).
/// </summary>
public sealed record BaseAppVersionDiffResult(
    IReadOnlyList<BaseAppFileListRow> Added,
    IReadOnlyList<BaseAppFileListRow> Removed,
    IReadOnlyList<BaseAppVersionDiffPair> Changed)
{
    public static BaseAppVersionDiffResult Empty { get; } = new(
        Array.Empty<BaseAppFileListRow>(),
        Array.Empty<BaseAppFileListRow>(),
        Array.Empty<BaseAppVersionDiffPair>());

    public int TotalCount => Added.Count + Removed.Count + Changed.Count;
}

/// <summary>
/// Matched pair for a Changed row in <see cref="BaseAppVersionDiffResult"/>.
/// <see cref="Left"/> is the file in version A; <see cref="Right"/> is
/// its counterpart in version B. The side-by-side file diff page is
/// the natural destination when the user picks a row.
/// </summary>
public sealed record BaseAppVersionDiffPair(
    BaseAppFileListRow Left,
    BaseAppFileListRow Right);

/// <summary>
/// Lightweight projection of <c>base_app_extensions</c> for the filter
/// dropdown — no <c>Files</c> nav, no version FK, just what the UI
/// renders. <see cref="AppId"/> is included so admin tooling can
/// disambiguate apps with the same display name from different
/// publishers (rare in BaseApp but allowed by AL).
/// </summary>
public sealed record BaseAppExtensionRow(
    long Id,
    Guid AppId,
    string Name,
    string Publisher,
    string AppVersion);

/// <summary>
/// Search modes for the version-browser search box. The page picks one
/// at a time — never an omnisearch — so each result table has its own
/// shape and the user always knows what they're looking at.
/// </summary>
public enum BaseAppSearchScope
{
    /// <summary>Match object name or ID — the default "find an object" path.</summary>
    Objects,
    /// <summary>Match procedure / event symbol name across all files in the version.</summary>
    Procedures,
    /// <summary>Match a substring inside file content — full-text search.</summary>
    Content,
}

/// <summary>One page of procedure-search hits plus the unfiltered total count.</summary>
public sealed record BaseAppProcedureSearchPage(
    IReadOnlyList<BaseAppProcedureHit> Rows, int TotalCount);

/// <summary>
/// A procedure / event match returned by <see cref="BaseAppService.SearchProceduresAsync"/>.
/// Carries enough about the owning file to render the result row inline
/// (no second query) and the symbol id so a row action can route through
/// <see cref="BaseAppService.FindReferencesAsync"/>.
/// </summary>
public sealed record BaseAppProcedureHit(
    long SymbolId,
    long FileId,
    int VersionId,
    string ObjectType,
    int? ObjectId,
    string ObjectName,
    string SymbolKind,
    string SymbolName,
    int LineNumber,
    string? Signature);

/// <summary>One page of content-search hits plus the unfiltered total count.</summary>
public sealed record BaseAppContentSearchPage(
    IReadOnlyList<BaseAppContentHit> Rows, int TotalCount);

/// <summary>
/// A content-substring match returned by <see cref="BaseAppService.SearchContentAsync"/>.
/// One row per file (the first matching line); clicking deep-links to
/// the file viewer's <c>#L&lt;n&gt;</c> anchor.
/// </summary>
public sealed record BaseAppContentHit(
    long FileId,
    string ObjectType,
    int? ObjectId,
    string ObjectName,
    string Path,
    int LineNumber,
    int ColumnStart,
    string Snippet);

/// <summary>
/// Lightweight projection used for the file list — omits the heavyweight
/// <c>Content</c> column so a 10K-row page doesn't ship megabytes over the wire.
/// <see cref="ContentHash"/> is optional — present on rows used for
/// version comparison, absent on the default list projection.
/// </summary>
public sealed record BaseAppFileListRow(
    long Id,
    int VersionId,
    string Path,
    string FileName,
    string? Module,
    string ObjectType,
    int? ObjectId,
    string ObjectName,
    string? Namespace,
    int LineCount,
    long? ExtensionId,
    string? ExtensionName,
    string? ExtensionPublisher)
{
    public string? ContentHash { get; init; }
}

/// <summary>
/// Per-line diff result returned to the side-by-side viewer. <c>Number</c> is
/// the 1-based line number in the corresponding side (null for imaginary
/// padding rows). <c>Change</c> is the diff classification.
/// </summary>
public sealed record BaseAppDiffLine(int? Number, BaseAppDiffChange Change, string Text);

/// <summary>Per-side line lists; same length, indexes correspond left↔right.</summary>
public sealed record BaseAppDiffResult(
    IReadOnlyList<BaseAppDiffLine> Left,
    IReadOnlyList<BaseAppDiffLine> Right);

public enum BaseAppDiffChange
{
    Unchanged,
    Inserted,
    Deleted,
    Modified,
    /// <summary>Padding row inserted to align the two sides; nothing actually exists here.</summary>
    Imaginary,
}

/// <summary>Result of <see cref="BaseAppService.FindReferencesAsync"/>.</summary>
public sealed record BaseAppReferenceResult(
    BaseAppSymbol Symbol,
    int OverloadCount,
    IReadOnlyList<BaseAppReferenceHit> Likely,
    IReadOnlyList<BaseAppReferenceHit> PossiblyRelated)
{
    public static BaseAppReferenceResult Empty { get; } = new(
        Symbol: new BaseAppSymbol(),
        OverloadCount: 0,
        Likely: Array.Empty<BaseAppReferenceHit>(),
        PossiblyRelated: Array.Empty<BaseAppReferenceHit>());
}

/// <summary>One call-site hit found by the references search.</summary>
public sealed record BaseAppReferenceHit(
    long FileId,
    string ObjectType,
    int? ObjectId,
    string ObjectName,
    string Path,
    int LineNumber,
    int ColumnStart,
    string Snippet,
    BaseAppReferenceConfidence Confidence);

/// <summary>
/// How sure we are that a textual match is a real call to the declared
/// symbol. Classification is heuristic — we don't have a full AL parser —
/// but separating "Likely" from "PossiblyRelated" keeps false positives
/// from drowning out real hits.
/// </summary>
public enum BaseAppReferenceConfidence
{
    /// <summary>Match lives in the same object as the declaration — almost certainly a self-call.</summary>
    SameObject,
    /// <summary>Match line mentions the declaring object's name (quoted or bare).</summary>
    Qualified,
    /// <summary>Bare <c>Name(</c> with no qualifier — could be a different overload on another object.</summary>
    PossiblyRelated,
}

/// <summary>
/// Where a "Go to definition" click should navigate. <see cref="Description"/>
/// is a short human label used in toasts and accessibility text (e.g.
/// "procedure Post on Sales-Post").
/// </summary>
public sealed record GoToDefinitionTarget(long FileId, int LineNumber, string Description);

/// <summary>Result of <see cref="BaseAppService.FindInFileAsync"/>.</summary>
public sealed record FileSearchResult(string Word, IReadOnlyList<FileSearchHit> Hits);

/// <summary>One occurrence of the search word inside the file content.</summary>
public sealed record FileSearchHit(int LineNumber, int ColumnStart, string Snippet);
