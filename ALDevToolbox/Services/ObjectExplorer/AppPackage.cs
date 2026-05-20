namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// In-memory representation of one parsed BC <c>.app</c> file. Produced by
/// <see cref="AppPackageReader"/>; consumed by the import service (PR 3) to
/// emit <c>oe_*</c> rows. Records are immutable so the consumer can fan-out
/// safely across threads without copying.
/// </summary>
public sealed record AppPackage(
    AppManifest Manifest,
    SymbolPackage Symbols,
    IReadOnlyList<AppSourceFile> SourceFiles,
    IReadOnlyList<AppXliffFile> XliffFiles,
    string AppFileHash);

/// <summary>
/// One <c>.xlf</c> translation file pulled out of the archive's
/// <c>Translations/</c> folder, already parsed into an
/// <see cref="XliffDocument"/>. The parser runs inline during
/// <see cref="AppPackageReader.ReadAsync"/> so the raw decompressed
/// bytes (which can be 50–100&#160;MB per language for the BC base
/// app) are released as soon as parsing finishes, rather than being
/// held alive on the <see cref="AppPackage"/> until the import
/// service processes them. Holding the parsed document instead
/// keeps memory proportional to the trans-units we'll persist
/// (much smaller), not to the raw XML.
/// </summary>
public sealed record AppXliffFile(string Path, XliffDocument Document);

/// <summary>
/// Parsed <c>NavxManifest.xml</c>. App identity (AppId / Name / Publisher /
/// Version) plus the policy flags the importer needs.
/// </summary>
public sealed record AppManifest(
    Guid AppId,
    string Name,
    string Publisher,
    string Version,
    string? Target,
    string? Runtime,
    bool IncludeSourceInSymbolFile,
    bool AllowDebugging,
    bool AllowDownloadingSource,
    IReadOnlyList<AppDependency> Dependencies);

/// <summary>One <c>&lt;Dependency&gt;</c> entry from the manifest.</summary>
public sealed record AppDependency(
    Guid AppId,
    string Name,
    string Publisher,
    string Version);

/// <summary>
/// Flattened symbol tree from <c>SymbolReference.json</c>. The on-disk shape
/// is a recursive <c>Namespaces</c> tree; this list holds every object at any
/// nesting depth, with its full dotted namespace path on <see cref="SymbolObject.Namespace"/>.
/// </summary>
public sealed record SymbolPackage(
    string? RuntimeVersion,
    IReadOnlyList<SymbolObject> Objects);

/// <summary>
/// One AL object inside a symbol package — codeunit, table, page, report,
/// xmlport, query, controladdin, enum, interface, permissionset, or any of
/// the extension variants.
/// </summary>
public sealed record SymbolObject(
    string Kind,
    int? ObjectId,
    string Name,
    string Namespace,
    string? ReferenceSourceFileName,
    Guid? ExtendsAppId,
    string? ExtendsObjectName,
    IReadOnlyList<SymbolProperty> Properties,
    IReadOnlyList<SymbolMethod> Methods,
    IReadOnlyList<SymbolVariable> Variables,
    IReadOnlyList<SymbolField> Fields);

/// <summary>A property line on an object — raw <c>(name, value)</c> from the symbol package.</summary>
public sealed record SymbolProperty(string Name, string Value);

/// <summary>
/// A public/internal procedure or trigger declared on an object. Local
/// procedures are stripped from the symbol package by the compiler — those
/// come from source extraction in a later layer.
/// </summary>
public sealed record SymbolMethod(
    string Name,
    SymbolTypeRef? ReturnType,
    IReadOnlyList<SymbolParameter> Parameters,
    bool IsInternal);

/// <summary>One parameter of a <see cref="SymbolMethod"/>.</summary>
public sealed record SymbolParameter(
    string Name,
    SymbolTypeRef Type);

/// <summary>
/// One object-scoped global variable. Procedure-locals aren't in the symbol
/// package and stay in the source-scan fallback path.
/// </summary>
public sealed record SymbolVariable(
    string Name,
    SymbolTypeRef Type);

/// <summary>One table field (or table-extension field).</summary>
public sealed record SymbolField(
    int Id,
    string Name,
    SymbolTypeRef Type,
    IReadOnlyList<SymbolProperty> Properties);

/// <summary>
/// A type reference, fully qualified when the type is an AL object in some
/// (possibly other) module. <see cref="ModuleId"/> identifies the declaring
/// app across the ecosystem — this is the cross-module link that makes
/// reference resolution exact instead of a name-only guess.
///
/// For non-AL types (system types like <c>HttpClient</c>, primitives like
/// <c>Code[20]</c>) only <see cref="Name"/> is populated.
/// For same-module references the <see cref="ModuleId"/> may be omitted in
/// the symbol package; the caller is responsible for stamping the importing
/// module's AppId when that's the case.
/// </summary>
public sealed record SymbolTypeRef(
    string Name,
    Guid? ModuleId,
    int? ObjectId,
    string? ObjectName);

/// <summary>
/// One <c>.al</c> source file extracted from the <c>.app</c> archive's
/// embedded <c>src/</c> tree (when <c>IncludeSourceInSymbolFile="true"</c>)
/// or from a paired <c>.Source.zip</c> in a later layer.
/// </summary>
public sealed record AppSourceFile(string Path, string Content);
