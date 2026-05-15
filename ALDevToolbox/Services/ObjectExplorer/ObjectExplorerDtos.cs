namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// One row in the Releases picker. Carries denormalised file count and
/// content-size totals so admins can spot which Release is dragging on
/// storage without a per-row drill-down. The sizes are <em>character</em>
/// counts (Postgres <c>LENGTH(content)</c>) rather than byte counts — close
/// enough for ASCII-dominant AL source, off by 1–4× for the rare multi-byte
/// run, which is fine for a "how much room is this taking" overview.
/// </summary>
public sealed record ReleaseListItem(
    int Id,
    string Label,
    string Kind,
    string Status,
    string? BcVersion,
    int? ParentReleaseId,
    DateTime ImportedAt,
    int SourceFileCount,
    long SourceContentLength,
    DateTime? DeletedAt);

/// <summary>
/// Release detail surface for the header bar — adds module count and the
/// parent label so the breadcrumb can show "Customer X on BC 25.18".
/// </summary>
public sealed record ReleaseDetail(
    int Id,
    string Label,
    string Kind,
    string Status,
    string? BcVersion,
    int? ParentReleaseId,
    string? ParentLabel,
    DateTime ImportedAt,
    int ModuleCount);

/// <summary>Filter for <c>ListModulesAsync</c> — applies a substring search and the test/internal toggles.</summary>
public sealed record ModuleListFilter(
    string? Search = null,
    bool IncludeTest = false,
    bool IncludeInternal = false,
    bool IncludeLanguagePack = false);

/// <summary>One row in the Modules list — name, publisher, version, counts.</summary>
public sealed record ModuleListItem(
    long Id,
    Guid AppId,
    string Name,
    string Publisher,
    string Version,
    string? Target,
    bool IsTest,
    bool IsInternal,
    bool IsLanguagePack,
    int ObjectCount);

/// <summary>
/// Filter for the per-module Objects browser. <see cref="Kind"/> narrows to
/// codeunit/table/etc.; <see cref="Search"/> matches against object name or
/// (when numeric) object id.
/// </summary>
public sealed record ObjectListFilter(
    string? Kind = null,
    string? Search = null);

/// <summary>One row in the per-module Objects list.</summary>
public sealed record ObjectListItem(
    long Id,
    string Kind,
    int? ObjectId,
    string Name,
    string? Namespace,
    Guid? ExtendsAppId,
    string? ExtendsObjectName,
    long? SourceFileId,
    int LineNumber);

/// <summary>Page-shaped wrapper for the object browser.</summary>
public sealed record ObjectListPage(IReadOnlyList<ObjectListItem> Rows, int TotalCount);

/// <summary>
/// Detail surface for one ModuleObject. Joins related symbols and variables
/// so the inspector panel can render in a single round-trip.
/// </summary>
public sealed record ObjectDetail(
    long Id,
    string Kind,
    int? ObjectId,
    string Name,
    string? Namespace,
    long ModuleId,
    string ModuleName,
    Guid? ExtendsAppId,
    string? ExtendsObjectName,
    long? SourceFileId,
    string? SourceFilePath,
    int LineNumber,
    IReadOnlyList<ObjectSymbolRow> Symbols,
    IReadOnlyList<ObjectVariableRow> Variables);

public sealed record ObjectSymbolRow(
    long Id,
    string Kind,
    string Name,
    string? Signature,
    string? ReturnType,
    int? FieldId,
    int LineNumber);

public sealed record ObjectVariableRow(
    long Id,
    string Name,
    string? TypeKeyword,
    string TypeName,
    Guid? TargetAppId,
    string? TargetObjectKind,
    int? TargetObjectId,
    string? TargetObjectName);

/// <summary>
/// Query envelope for <c>FindReferencesAsync</c>. Either a (kind, id) pair or
/// a (kind, name) pair identifies the target. ID is preferred when present;
/// name is the fallback for kinds the symbol package doesn't number
/// (interfaces, some extensions).
/// </summary>
public sealed record FindReferencesQuery(
    Guid TargetAppId,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName);

/// <summary>
/// One reference matched by <c>FindReferencesAsync</c>. Carries enough
/// joined context (source module, source object, reference kind, line)
/// for the UI to link back to the calling object's file viewer without a
/// follow-up query.
/// </summary>
public sealed record ReferenceMatch(
    long Id,
    long SourceModuleId,
    string SourceModuleName,
    long SourceObjectId,
    string SourceObjectKind,
    string SourceObjectName,
    string ReferenceKind,
    int? LineNumber);

/// <summary>
/// Read-only projection of an <c>oe_module_files</c> row for the source viewer.
/// Content is loaded lazily — only fetched via <c>GetFileAsync</c> so the
/// list-files page doesn't drag every body into memory.
/// </summary>
public sealed record SourceFileDetail(
    long Id,
    long ModuleId,
    string Path,
    string Content,
    int LineCount);

/// <summary>
/// One hit from a Release-wide object search. Carries the owning module so
/// the result table can render "Module / Object" without a follow-up query,
/// plus the source-file pointer + line number so the row can deep-link
/// straight into the source viewer at the right line.
/// </summary>
public sealed record ReleaseObjectMatch(
    long Id,
    string Kind,
    int? ObjectId,
    string Name,
    string? Namespace,
    long ModuleId,
    string ModuleName,
    long? SourceFileId,
    int LineNumber,
    int FileLineCount);

/// <summary>
/// Header info for the source-file viewer's breadcrumb — module + release
/// names so the page renders the full path without two extra round trips.
/// </summary>
public sealed record SourceFileHeader(
    long Id,
    long ModuleId,
    string ModuleName,
    int ReleaseId,
    string ReleaseLabel,
    string Path,
    int LineCount);

/// <summary>
/// One procedure-search hit on the Release search page. Carries the source
/// file pointer so the row can deep-link straight to the declaration line.
/// </summary>
public sealed record ReleaseProcedureMatch(
    long Id,
    long ObjectId,
    string ObjectKind,
    string ObjectName,
    string ModuleName,
    string ProcedureKind,
    string Name,
    string? Signature,
    string? ReturnType,
    long? SourceFileId,
    int ObjectLineNumber);

/// <summary>
/// One content-search hit on the Release search page. The snippet is the
/// matched line's text trimmed to a reasonable display length; the file
/// pointer + line number deep-link straight into the source viewer with the
/// hit row highlighted.
/// </summary>
public sealed record ReleaseContentMatch(
    long FileId,
    string FilePath,
    long ModuleId,
    string ModuleName,
    int LineNumber,
    string Snippet);

/// <summary>
/// One row in the outline pane on the source-file viewer. Objects and their
/// symbols are flattened so the panel can render them as a single ordered
/// list keyed by line number — kind tells the renderer which icon to use.
/// </summary>
public sealed record SourceFileOutlineItem(
    string Kind,
    string Name,
    string? Signature,
    int LineNumber,
    long? ObjectId);

/// <summary>
/// Minimal Module summary used by the search-filter dropdown. Lighter than
/// <see cref="ModuleListItem"/> — none of the row counts or test/internal
/// flags matter for the filter widget.
/// </summary>
public sealed record ReleaseModuleSummary(
    long Id,
    string Name,
    string Publisher);

/// <summary>
/// Resolved go-to-definition target — file + 1-based line. The source
/// viewer navigates there with <c>?line=N</c> so the CodeMirror scroll
/// lands on the declaration row.
/// </summary>
public sealed record GoToDefinitionTarget(long FileId, int LineNumber);

/// <summary>
/// One occurrence of a word inside a single file — returned by
/// <c>FindInFileAsync</c> so the page can show a navigable list of every
/// line that matches the clicked identifier.
/// </summary>
public sealed record FileWordOccurrence(int Line, string LineText);

/// <summary>
/// Result envelope for the "Find in this file" gesture. <see cref="Word"/>
/// is what was extracted from the click position; <see cref="Occurrences"/>
/// is every line in the same file that contains it.
/// </summary>
public sealed record FileWordSearch(string Word, IReadOnlyList<FileWordOccurrence> Occurrences);
