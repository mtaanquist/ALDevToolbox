using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
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
            .ThenByDescending(v => v.Minor)
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
    /// id (cast to text), reusing the trigram GIN indexes.
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

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var trimmed = filter.Search.Trim();
            if (trimmed.Length > MaxSearchQueryLength)
            {
                trimmed = trimmed.Substring(0, MaxSearchQueryLength);
            }
            var pattern = "%" + trimmed + "%";
            // Try matching the raw search string as an object id too — when
            // the user types "80" we want codeunit 80 to land top of the list.
            int.TryParse(trimmed, out var idCandidate);
            query = query.Where(f =>
                EF.Functions.ILike(f.ObjectName, pattern)
                || EF.Functions.ILike(f.Content, pattern)
                || (idCandidate != 0 && f.ObjectId == idCandidate));
        }

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderBy(f => f.ObjectType)
            .ThenBy(f => f.ObjectName)
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
                f.LineCount))
            .ToListAsync(ct);

        return new BaseAppFilePage(rows, total);
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
    /// Finds every place in the symbol's version that textually calls
    /// <c>name(</c>. Hits are classified by confidence so the UI can split
    /// likely matches from possibly-related ones (different overloads on
    /// other objects with the same name).
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
            .CountAsync(s => s.FileId == symbol.FileId && s.Name == symbol.Name, ct);

        // Coarse candidate pass via ILIKE: trigram GIN on lower(content) is
        // already in place, so substring matches are fast even on a real
        // Base Application. Fine-grained word-boundary check happens in C#
        // afterwards so a procedure called "Post" doesn't drag in "Posting"
        // or "Reposted".
        var ilikePattern = "%" + symbol.Name + "(%";

        var candidateFiles = await _db.BaseAppFiles
            .AsNoTracking()
            .Where(f => f.VersionId == symbol.VersionId)
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

        var escapedName = System.Text.RegularExpressions.Regex.Escape(symbol.Name);
        var lineRegex = new System.Text.RegularExpressions.Regex(
            $@"\b{escapedName}\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

        var likely = new List<BaseAppReferenceHit>();
        var possiblyRelated = new List<BaseAppReferenceHit>();

        foreach (var file in candidateFiles)
        {
            var lines = file.Content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var match = lineRegex.Match(lines[i]);
                if (!match.Success) continue;

                // Skip the declaration line itself (file is the declaring
                // file and the line number matches an overload row).
                if (file.Id == symbol.FileId)
                {
                    var declHere = await _db.BaseAppSymbols
                        .AsNoTracking()
                        .AnyAsync(s => s.FileId == symbol.FileId
                            && s.Name == symbol.Name
                            && s.LineNumber == i + 1, ct);
                    if (declHere) continue;
                }

                var confidence = ClassifyConfidence(
                    file.ObjectName, declaringObjectName, file.Content);

                var hit = new BaseAppReferenceHit(
                    FileId: file.Id,
                    ObjectType: file.ObjectType,
                    ObjectId: file.ObjectId,
                    ObjectName: file.ObjectName,
                    Path: file.Path,
                    LineNumber: i + 1,
                    ColumnStart: match.Index + 1,
                    Snippet: TrimSnippet(lines[i]),
                    Confidence: confidence);

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

    /// <summary>
    /// Decides how confident we are that a textual <c>Name(</c> in
    /// <paramref name="fileContent"/> is really a call to the declared
    /// symbol. We look at the whole file rather than just the matched line
    /// because AL's call-site syntax (<c>varname.Proc(</c>) uses the
    /// variable name, while the type-qualifier (<c>Codeunit "Sales-Post"</c>)
    /// usually lives in a <c>var</c> block elsewhere in the file.
    /// </summary>
    private static BaseAppReferenceConfidence ClassifyConfidence(
        string callerObjectName, string declaringObjectName, string fileContent)
    {
        if (string.Equals(callerObjectName, declaringObjectName, StringComparison.Ordinal))
        {
            return BaseAppReferenceConfidence.SameObject;
        }

        // Quoted reference anywhere in the file — `Codeunit "Sales-Post"`,
        // `Codeunit::"Sales-Post"`, or just `"Sales-Post"` itself.
        if (fileContent.Contains('"' + declaringObjectName + '"', StringComparison.Ordinal))
        {
            return BaseAppReferenceConfidence.Qualified;
        }

        // Bare identifier reference for single-word object names (AL allows
        // hyphens and spaces inside quotes only, so multi-word names won't
        // appear unquoted). Covers `Codeunit ItemMgt` and similar.
        if (!declaringObjectName.Contains(' ') && !declaringObjectName.Contains('-')
            && fileContent.Contains(declaringObjectName, StringComparison.Ordinal))
        {
            return BaseAppReferenceConfidence.Qualified;
        }

        return BaseAppReferenceConfidence.PossiblyRelated;
    }

    private const int SnippetMaxLength = 240;
    private static string TrimSnippet(string line)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.Length <= SnippetMaxLength) return trimmed;
        return trimmed.Substring(0, SnippetMaxLength) + "…";
    }
}

/// <summary>Filter inputs from the version-browser table.</summary>
public sealed record BaseAppFileFilter(string? ObjectType, string? Module, string? Search)
{
    public static BaseAppFileFilter Empty { get; } = new(null, null, null);
}

/// <summary>One page of file rows plus the unfiltered total count.</summary>
public sealed record BaseAppFilePage(IReadOnlyList<BaseAppFileListRow> Rows, int TotalCount);

/// <summary>
/// Lightweight projection used for the file list — omits the heavyweight
/// <c>Content</c> column so a 10K-row page doesn't ship megabytes over the wire.
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
    int LineCount);

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
