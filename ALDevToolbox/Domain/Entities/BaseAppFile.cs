namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One AL source file imported from a Microsoft Base Application ZIP. Parsed
/// metadata (<see cref="ObjectType"/>, <see cref="ObjectId"/>,
/// <see cref="ObjectName"/>, <see cref="Namespace"/>) populated by
/// <c>AlDeclarationParser</c> at import; <see cref="Content"/> stored verbatim
/// so the file viewer renders the same bytes Microsoft shipped.
///
/// Id is <c>long</c> because 10K files × N versions × M orgs grows past int.
/// </summary>
public class BaseAppFile
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int VersionId { get; set; }
    public BaseAppVersion? Version { get; set; }

    /// <summary>Relative path inside the imported ZIP.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Last path segment (e.g. <c>SalesPost.Codeunit.al</c>).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Top-level folder inside the ZIP, used to label the row's source
    /// module — typically <c>Base Application</c>, <c>System Application</c>,
    /// etc. Null when the ZIP has no recognisable top folder.
    /// </summary>
    public string? Module { get; set; }

    /// <summary>
    /// Lower-cased AL declaration keyword (<c>codeunit</c>, <c>table</c>,
    /// <c>page</c>, …). Stored as a string rather than an enum so new AL
    /// object kinds don't need a migration.
    /// </summary>
    public string ObjectType { get; set; } = string.Empty;

    /// <summary>Object id from the declaration. Null for objects without an id (interfaces, some extensions).</summary>
    public int? ObjectId { get; set; }

    /// <summary>Unquoted object name from the declaration.</summary>
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>From <c>namespace X.Y.Z;</c> if present.</summary>
    public string? Namespace { get; set; }

    /// <summary>UTF-8 source text. Matches the codebase's text-only persistence stance.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Denormalised line count for display in the browser table.</summary>
    public int LineCount { get; set; }

    public ICollection<BaseAppSymbol> Symbols { get; set; } = new List<BaseAppSymbol>();
}
