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

    /// <summary>
    /// For pages and pageextensions: the unquoted name of the table the
    /// page binds <c>Rec</c> to. Populated from the page's
    /// <c>SourceTable</c> property at import time; for pageextensions
    /// copied from the base page after both have been imported. Null for
    /// kinds that don't have a source table (codeunits, tables themselves,
    /// reports, …) and for pages without an explicit SourceTable.
    ///
    /// Why we denormalise it onto the object row: the AL reference extractor
    /// needs to know what <c>Rec</c> is when walking a page's triggers
    /// (e.g. <c>Rec."No."</c> on page 42 "Sales Order" must resolve to a
    /// field on table "Sales Header"). The page's own type catalog row
    /// doesn't carry that — it lives in the symbol package's property
    /// list. Resolving at extract time would require either re-parsing
    /// the symbol package or scanning the source for the property; a
    /// single nullable column on the owner is cheaper.
    /// </summary>
    public string? SourceTableName { get; set; }

    /// <summary>Source file this object is declared in. Null when source wasn't available.</summary>
    public long? SourceFileId { get; set; }
    public ModuleFile? SourceFile { get; set; }

    /// <summary>1-based line where the object declaration appears in the source file.</summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// AL <c>ObsoleteState</c> property value, when set on the object
    /// (<c>Pending</c> / <c>Removed</c>). Used as a tiebreaker by the
    /// catalog resolver: when two visible apps declare a same-named
    /// object (e.g. Base Application's legacy <c>No. Series Line</c>
    /// shim alongside Business Foundation's canonical version),
    /// non-obsolete candidates win. Null when the property isn't
    /// declared on the object — the default <c>No</c> state.
    /// </summary>
    public string? ObsoleteState { get; set; }

    public ICollection<ModuleSymbol> Symbols { get; set; } = new List<ModuleSymbol>();
    public ICollection<ModuleVariable> Variables { get; set; } = new List<ModuleVariable>();
}
