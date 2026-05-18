namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One object-scoped global variable on a <see cref="ModuleObject"/>, with its declared type
/// fully resolved by the symbol package. Procedure-local variables are NOT in this table —
/// they're not in the symbol package and stay in the source-scan fallback path.
///
/// When the variable is typed to another AL object (<c>Codeunit "Sales-Post"</c>,
/// <c>Record "Customer"</c>, etc.), <see cref="TargetAppId"/> + <see cref="TargetObjectKind"/>
/// + <see cref="TargetObjectId"/> / <see cref="TargetObjectName"/> identify the target across
/// the entire ecosystem. When typed to a non-AL system type (<c>HttpClient</c>,
/// <c>JsonObject</c>, <c>ErrorInfo</c>, …) only <see cref="TypeName"/> is populated.
/// </summary>
public class ModuleVariable
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public long ModuleId { get; set; }
    public Module? Module { get; set; }

    public long ObjectId { get; set; }
    public ModuleObject? Object { get; set; }

    /// <summary>Variable identifier as declared.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// AL type keyword when the type is one of the BC object keywords (<c>Codeunit</c>,
    /// <c>Record</c>, <c>Page</c>, <c>Report</c>, …). Null when the variable is typed to a
    /// system type without a keyword (<c>HttpClient: HttpClient</c>) or a primitive.
    /// </summary>
    public string? TypeKeyword { get; set; }

    /// <summary>The type name as written — e.g. <c>"Sales-Post"</c>, <c>"HttpClient"</c>, <c>"Code[20]"</c>.</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>AppId of the module declaring this variable's type; null when the type is non-AL or unresolved.</summary>
    public Guid? TargetAppId { get; set; }

    /// <summary>Lower-cased AL kind of the target object; null when not applicable.</summary>
    public string? TargetObjectKind { get; set; }

    /// <summary>Object ID of the target (when known); null for interfaces and unresolved types.</summary>
    public int? TargetObjectId { get; set; }

    /// <summary>Unquoted name of the target object; mirrors <see cref="TypeName"/> for AL-keyworded vars.</summary>
    public string? TargetObjectName { get; set; }

    /// <summary>
    /// 1-based source line of the variable's declaration on
    /// <see cref="Object"/>. Populated from <c>AlSymbolExtractor</c>'s
    /// <c>var_declaration</c> rows during import; <c>0</c> when the
    /// extractor didn't see a matching declaration (multi-name
    /// declarations, generated source, etc.). Used by the source
    /// viewer's right-click resolver to land Go-to-definition on the
    /// declaration line. See
    /// <c>.design/al-reference-extractor-refactor.md</c> step 2.
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// 1-based start column of the variable's name token, paired with
    /// <see cref="LineNumber"/>.
    /// </summary>
    public int ColumnStart { get; set; }

    /// <summary>
    /// 1-based end column (exclusive) of the variable's name token,
    /// paired with <see cref="LineNumber"/>.
    /// </summary>
    public int ColumnEnd { get; set; }
}
