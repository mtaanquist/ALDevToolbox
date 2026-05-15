namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// One row in the Releases picker — minimal projection so the dropdown
/// renders without loading modules eagerly.
/// </summary>
public sealed record ReleaseListItem(
    int Id,
    string Label,
    string Kind,
    string Status,
    string? BcVersion,
    int? ParentReleaseId,
    DateTime ImportedAt);

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
