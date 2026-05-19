namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One declaration inside a <see cref="ModuleObject"/> — a procedure, trigger, event publisher,
/// event subscriber, or table field. Public/internal methods come from the symbol package's
/// <c>Methods</c> array; locals come from source extraction. Fields come from the table's
/// <c>Fields</c> array. Overloads are separate rows distinguished by line number and
/// signature; the find-references query merges them by name.
/// </summary>
public class ModuleSymbol
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Denormalised from the parent object so the find-references query doesn't join through Object.</summary>
    public long ModuleId { get; set; }
    public Module? Module { get; set; }

    public long ObjectId { get; set; }
    public ModuleObject? Object { get; set; }

    /// <summary>
    /// One of: <c>procedure</c>, <c>local_procedure</c>, <c>internal_procedure</c>,
    /// <c>protected_procedure</c>, <c>trigger</c>, <c>event_publisher</c>,
    /// <c>event_subscriber</c>, <c>field</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Unquoted name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Procedures: parameter list as written, including parens.
    /// Fields: AL data type (e.g. <c>Code[20]</c> or <c>Enum "Sales Document Type"</c>).
    /// Null for triggers without captured params and for kinds where signature isn't meaningful.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>Procedure return type as written, or null when absent.</summary>
    public string? ReturnType { get; set; }

    /// <summary>AL field number for <c>field</c> rows; null otherwise.</summary>
    public int? FieldId { get; set; }

    /// <summary>1-based line where the declaration starts in the source file.</summary>
    public int LineNumber { get; set; }

    /// <summary>1-based column where the name token starts.</summary>
    public int ColumnStart { get; set; }

    /// <summary>1-based column past the last character of the name token.</summary>
    public int ColumnEnd { get; set; }

    /// <summary>
    /// 1-based line where the body's matching <c>end;</c> sits. Populated
    /// for body-bearing kinds (<c>procedure</c>, <c>local_procedure</c>,
    /// <c>internal_procedure</c>, <c>protected_procedure</c>, <c>trigger</c>,
    /// <c>event_publisher</c>, <c>event_subscriber</c>) when the source
    /// extractor was able to track the matching close; null for kinds with
    /// no body (fields, page fields, actions, query columns) and for legacy
    /// rows imported before the column existed. Lets the forward-edge MCP
    /// tools slice a procedure body and bound a "calls from this procedure"
    /// reference query with a single indexed predicate instead of inferring
    /// the close from the next-symbol line.
    /// </summary>
    public int? EndLine { get; set; }

    /// <summary>1-based column past the closing <c>end</c> keyword. Same population rules as <see cref="EndLine"/>.</summary>
    public int? EndColumn { get; set; }
}
