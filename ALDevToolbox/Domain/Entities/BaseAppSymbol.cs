namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One declaration extracted from a Microsoft Base Application file by the
/// <c>AlSymbolExtractor</c>. Procedures, triggers, integration-event
/// publishers, and event subscribers all live in this table. Powers the
/// Object Explorer "Find references" gesture and (later) "Go to definition".
///
/// Each overload is a separate row (line and signature distinguish them);
/// the references query merges overloads because call sites are textually
/// identical regardless of which overload binds at compile time.
/// </summary>
public class BaseAppSymbol
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Denormalised from the file so the references query doesn't join.</summary>
    public int VersionId { get; set; }
    public BaseAppVersion? Version { get; set; }

    public long FileId { get; set; }
    public BaseAppFile? File { get; set; }

    /// <summary>
    /// One of: <c>procedure</c>, <c>local_procedure</c>,
    /// <c>internal_procedure</c>, <c>protected_procedure</c>, <c>trigger</c>,
    /// <c>event_publisher</c>, <c>event_subscriber</c>, <c>field</c>,
    /// <c>object_declaration</c>. Stored as a string so new AL declaration
    /// kinds don't need a migration.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Unquoted procedure / trigger / field / object name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Procedures: parameter list as written, including the surrounding
    /// parentheses (e.g. <c>(var SalesHeader: Record "Sales Header"; Commit:
    /// Boolean)</c>). Fields: the AL data type of the field (e.g. <c>Code[20]</c>
    /// or <c>Enum "Sales Document Type"</c>). <c>null</c> for triggers without
    /// a parameter list captured and for <c>object_declaration</c> rows.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>
    /// AL field number for <c>field</c> rows (1, 2, 3 …); <c>null</c> for
    /// every other kind. Stored separately from <see cref="Signature"/> so
    /// the inspector can show "(1) No." rather than parsing the field number
    /// back out of a free-form string.
    /// </summary>
    public int? FieldId { get; set; }

    /// <summary>1-based line where the declaration appears.</summary>
    public int LineNumber { get; set; }

    /// <summary>1-based column where the name token starts. Used by CodeMirror to position the click affordance.</summary>
    public int ColumnStart { get; set; }

    /// <summary>1-based column past the last character of the name token.</summary>
    public int ColumnEnd { get; set; }
}
