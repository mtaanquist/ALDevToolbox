namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One AL object inside a <see cref="Module"/> — a codeunit, table, page, report, xmlport,
/// query, controladdin, enum, interface, permissionset, or any of the *extension variants
/// (pageextension, tableextension, etc.). Mirrors the entries in <c>SymbolReference.json</c>
/// 's namespace tree; populated at ingest time.
///
/// Children: <see cref="ModuleSymbol"/> (procedures, fields, triggers, events) and
/// <see cref="ModuleVariable"/> (object-scoped globals). Outbound references from this object
/// live in <see cref="ModuleReference"/> rows with <c>source_object_id</c> pointing here.
/// </summary>
public class ModuleObject
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public long ModuleId { get; set; }
    public Module? Module { get; set; }

    /// <summary>Lower-cased AL declaration keyword (<c>codeunit</c>, <c>table</c>, …).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Object id from the declaration. Null for interfaces and some extensions.</summary>
    public int? ObjectId { get; set; }

    /// <summary>Unquoted object name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>From <c>namespace X.Y.Z;</c> at the top of the file, when present.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// For extension objects (<c>tableextension</c>, <c>pageextension</c>, etc.): the AppId of
    /// the module declaring the base object being extended. Null when the object isn't an
    /// extension. Used by the resolver to walk a tableextension's field references back to
    /// the base table.
    /// </summary>
    public Guid? ExtendsAppId { get; set; }

    /// <summary>For extension objects: the name of the base object being extended. Null otherwise.</summary>
    public string? ExtendsObjectName { get; set; }

    /// <summary>Source file this object is declared in. Null when source wasn't available.</summary>
    public long? SourceFileId { get; set; }
    public ModuleFile? SourceFile { get; set; }

    /// <summary>1-based line where the object declaration appears in the source file.</summary>
    public int LineNumber { get; set; }

    public ICollection<ModuleSymbol> Symbols { get; set; } = new List<ModuleSymbol>();
    public ICollection<ModuleVariable> Variables { get; set; } = new List<ModuleVariable>();
}
