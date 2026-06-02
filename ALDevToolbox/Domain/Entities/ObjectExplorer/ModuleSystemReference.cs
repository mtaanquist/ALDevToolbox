namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One call to a built-in / system method (<c>Insert</c>, <c>Modify</c>,
/// <c>SetRange</c>, <c>Validate</c>, …) on a resolved AL object — the rows the
/// normal reference extractor deliberately <em>drops</em> (see
/// <c>AlBuiltinMethods.IsBuiltin</c>). They live in their own table, separate
/// from <see cref="ModuleReference"/>, because they're an order of magnitude
/// more numerous: keeping them out of <c>oe_module_references</c> keeps the
/// main find-references query and its indexes lean. Surfaced only through the
/// dedicated "Find System References" action / <c>find_system_references</c>
/// MCP tool. See issue #279.
///
/// Like <see cref="ModuleReference"/>, the target is the qualified
/// <c>(target_app_id, target_object_kind, target_object_id, target_object_name)</c>
/// receiver triplet — resolved at query time via the recursive release-chain CTE,
/// not a hard FK — so retargeting a release re-resolves every row.
/// </summary>
public class ModuleSystemReference
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Denormalised from the source object so the resolution query doesn't join twice.</summary>
    public long ModuleId { get; set; }
    public Module? Module { get; set; }

    /// <summary>The object whose body makes the system call.</summary>
    public long SourceObjectId { get; set; }
    public ModuleObject? SourceObject { get; set; }

    /// <summary>AppId of the module declaring the receiver object.</summary>
    public Guid TargetAppId { get; set; }

    /// <summary>Lower-cased AL kind of the receiver (<c>table</c>, <c>record</c>, …).</summary>
    public string TargetObjectKind { get; set; } = string.Empty;

    /// <summary>Receiver object id when known; null falls back to name matching.</summary>
    public int? TargetObjectId { get; set; }

    /// <summary>Unquoted receiver object name. Always populated.</summary>
    public string TargetObjectName { get; set; } = string.Empty;

    /// <summary>The built-in method invoked (<c>Insert</c>, <c>Modify</c>, <c>SetRange</c>, …).</summary>
    public string SystemMethodName { get; set; } = string.Empty;

    /// <summary><c>method_call</c> (followed by parens) or <c>field_access</c>.</summary>
    public string ReferenceKind { get; set; } = string.Empty;

    /// <summary>1-based line of the call in the source object's file.</summary>
    public int? LineNumber { get; set; }

    /// <summary>1-based column of the method token, when captured.</summary>
    public int? ColumnNumber { get; set; }

    /// <summary>
    /// Optional FK to the enclosing procedure / trigger
    /// <see cref="ModuleSymbol"/>, so the panel can show which member each
    /// system call sits in. Null for object-scope contexts.
    /// </summary>
    public long? SourceSymbolId { get; set; }
    public ModuleSymbol? SourceSymbol { get; set; }
}
