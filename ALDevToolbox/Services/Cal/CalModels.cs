namespace ALDevToolbox.Services.Cal;

/// <summary>
/// Models for the legacy C/AL TXT ingest path. A classic NAV "export all
/// objects" produces one large file of <c>OBJECT &lt;Type&gt; &lt;Id&gt;
/// &lt;Name&gt; { … }</c> declarations (Windows-1252 / CRLF). These records
/// are the intermediate shape between the raw text and the <c>oe_*</c> entity
/// rows that <see cref="ALDevToolbox.Services.ObjectExplorer.CalImportService"/>
/// persists — deliberately separate from the AL <c>.app</c> symbol-package
/// shapes, because C/AL and AL share no on-disk format.
///
/// See <c>.design/object-explorer.md</c> for the entity model these feed.
/// </summary>
public static class CalObjectKinds
{
    /// <summary>
    /// Maps the C/AL <c>OBJECT &lt;Type&gt;</c> keyword to the lower-cased
    /// <c>ModuleObject.Kind</c> string the rest of the Object Explorer uses.
    /// Page-era types only (NAV 2013+); classic Form/Dataport are accepted by
    /// the splitter and stored as source but not structurally parsed in v1.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TypeToKind =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Table"] = "table",
            ["Page"] = "page",
            ["Report"] = "report",
            ["Codeunit"] = "codeunit",
            ["Query"] = "query",
            ["XMLport"] = "xmlport",
            ["MenuSuite"] = "menusuite",
            ["Form"] = "form",
            ["Dataport"] = "dataport",
        };

    /// <summary>
    /// C/AL type keywords that name another object by numeric id
    /// (<c>Record 36</c>, <c>Codeunit 80</c>, …). Used to populate the
    /// <c>Target*</c> resolution fields on variables and references.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ObjectTypeKeywordToKind =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Record"] = "table",
            ["Codeunit"] = "codeunit",
            ["Page"] = "page",
            ["Report"] = "report",
            ["XMLport"] = "xmlport",
            ["Query"] = "query",
            ["MenuSuite"] = "menusuite",
            ["Form"] = "form",
            ["Dataport"] = "dataport",
        };
}

/// <summary>One top-level object found by <see cref="CalObjectSplitter"/>.</summary>
/// <param name="TypeKeyword">The C/AL keyword as written (<c>Table</c>, <c>Codeunit</c>, …).</param>
/// <param name="Id">Numeric object id from the header.</param>
/// <param name="Name">Object name (rest of the header line, trimmed).</param>
/// <param name="StartLine">1-based line of the <c>OBJECT</c> header in the whole file.</param>
/// <param name="RawText">The object's full text, header through the closing brace — the unit stored as one source slice.</param>
public sealed record CalObjectBlock(string TypeKeyword, int Id, string Name, int StartLine, string RawText);

/// <summary>A C/AL variable, parameter, or field-typed reference, with object-by-id type info resolved where it applies.</summary>
public sealed record CalVariable(
    string Name,
    string? TypeKeyword,
    int? TargetObjectId,
    string TypeName,
    int LineNumber,
    int ColumnStart,
    int ColumnEnd,
    bool ByRef = false);

/// <summary>A table/field declaration from a FIELDS record. Line numbers are slice-relative.</summary>
public sealed record CalField(
    int Id,
    string Name,
    string DataType,
    int LineNumber);

/// <summary>A page control bound to a record field (<c>SourceExpr</c>) or a named caption.</summary>
public sealed record CalPageField(
    string Name,
    int LineNumber);

/// <summary>A C/AL procedure or local procedure. Body text + scope are captured for the Part-2 reference walker.</summary>
public sealed record CalProcedure(
    string Name,
    bool IsLocal,
    string Signature,
    string? ReturnType,
    int LineNumber,
    int? EndLine,
    string Body,
    int BodyLine,
    IReadOnlyList<CalVariable> Parameters,
    IReadOnlyList<CalVariable> Locals);

/// <summary>An <c>OnXxx=BEGIN…END;</c> trigger property (object-, field-, or control-level).</summary>
public sealed record CalTrigger(
    string Name,
    int LineNumber,
    string Body,
    int BodyLine,
    IReadOnlyList<CalVariable> Locals);

/// <summary>The fully parsed contents of one C/AL object.</summary>
public sealed record CalParsedObject(
    string Kind,
    int ObjectId,
    string Name,
    string? VersionList,
    string? SourceTableId,
    IReadOnlyList<CalField> Fields,
    IReadOnlyList<CalVariable> Globals,
    IReadOnlyList<CalProcedure> Procedures,
    IReadOnlyList<CalTrigger> Triggers,
    IReadOnlyList<CalPageField> PageFields);
