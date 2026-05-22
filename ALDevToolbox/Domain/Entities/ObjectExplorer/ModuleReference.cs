namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One outbound reference from a <see cref="ModuleObject"/> to some target AL object,
/// expressed as the qualified <c>(target_app_id, target_object_kind, target_object_id,
/// target_object_name)</c> triplet pulled from the symbol package — NOT as a resolved
/// <see cref="ModuleObject"/> foreign key. The actual target row is resolved at query time
/// via the recursive CTE over <c>Release.parent_release_id</c>. This is what makes
/// retargeting a one-line <c>UPDATE</c>: change the parent, every reference re-resolves.
///
/// Reference kinds (string-valued so new ones don't require a migration):
///   - <c>variable_type</c>      — object-scoped variable typed to an AL object
///   - <c>extends_target</c>     — tableextension/pageextension/... target
///   - <c>implements_interface</c> — codeunit declared in its header as
///                                 implementing the named interface
///   - <c>table_no</c>           — codeunit <c>TableNo</c> property
///   - <c>return_type</c>        — procedure return type
///   - <c>parameter_type</c>     — procedure parameter type
///   - <c>event_publisher</c>    — event-subscriber binding to a publisher
///   - <c>data_item</c>          — report data item or xmlport table source
/// </summary>
public class ModuleReference
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Denormalised from the source object so the resolution query doesn't join twice.</summary>
    public long ModuleId { get; set; }
    public Module? Module { get; set; }

    public long SourceObjectId { get; set; }
    public ModuleObject? SourceObject { get; set; }

    /// <summary>AppId of the module that declares the target. Same-module refs stamped with the importing module's AppId.</summary>
    public Guid TargetAppId { get; set; }

    /// <summary>Lower-cased AL kind of the target object (<c>codeunit</c>, <c>table</c>, …).</summary>
    public string TargetObjectKind { get; set; } = string.Empty;

    /// <summary>Object ID of the target when the symbol package carries it; null for interfaces, etc.</summary>
    public int? TargetObjectId { get; set; }

    /// <summary>Unquoted target name. Always populated — the resolver falls back to this when ID is null.</summary>
    public string TargetObjectName { get; set; } = string.Empty;

    /// <summary>See class doc-comment for the allowed values.</summary>
    public string ReferenceKind { get; set; } = string.Empty;

    /// <summary>1-based line in the source object's file, when the reference came from source extraction. Null for symbol-package-only refs.</summary>
    public int? LineNumber { get; set; }

    /// <summary>
    /// 1-based column of the FIRST character of the referenced token on
    /// <see cref="LineNumber"/>. Stamped at Phase-2 extraction time from
    /// <c>ExtractedReference.Column</c>. Lets the source viewer skip the
    /// text-search fallback in <c>ListResolvablesInFileAsync</c> — which
    /// otherwise lands on the leftmost occurrence and mis-underlines the
    /// page-field DECLARATION name when the same identifier appears twice
    /// on the line (e.g. <c>field("No."; Rec."No.")</c>). Null for legacy
    /// rows imported before this column existed; the resolvable lookup
    /// falls back to text-search in that case.
    /// </summary>
    public int? ColumnNumber { get; set; }

    /// <summary>
    /// Member symbol name when the reference is scoped to a procedure / field
    /// / trigger inside the owner object (e.g. a future <c>method_call</c> or
    /// <c>field_access</c> reference_kind). Null for the existing
    /// object-level reference kinds.
    ///
    /// This is the primary cross-release matching key for member-scoped
    /// finds: <c>(TargetAppId, TargetObjectKind, TargetObjectId | Name,
    /// TargetMemberName, TargetMemberKind)</c>. Mirrors how the existing
    /// object-level resolution works — the row is module-instance-agnostic,
    /// re-resolved by the recursive-CTE chain walk at query time.
    /// </summary>
    public string? TargetMemberName { get; set; }

    /// <summary>
    /// Member symbol kind when <see cref="TargetMemberName"/> is set
    /// (<c>procedure</c>, <c>local_procedure</c>, <c>internal_procedure</c>,
    /// <c>protected_procedure</c>, <c>trigger</c>, <c>field</c>). Distinguishes
    /// a procedure call from a same-named field access on the same owner.
    /// </summary>
    public string? TargetMemberKind { get; set; }

    /// <summary>
    /// Optional direct FK to the resolved <see cref="ModuleSymbol"/>, stamped
    /// at import time when the referenced member is known to live in the
    /// same imported release. Auxiliary — query-side matching still uses the
    /// stable (TargetAppId, TargetObjectKind, …, TargetMemberName) tuple so
    /// references survive cross-release shadowing — but this FK lets the UI
    /// jump to the specific declaration row without a follow-up name lookup.
    /// </summary>
    public long? TargetSymbolId { get; set; }

    public ModuleSymbol? TargetSymbol { get; set; }

    /// <summary>
    /// Direct FK to a <see cref="ModuleVariable"/> when the reference
    /// is a <c>variable_use</c> — a bare identifier in a procedure
    /// body that resolves to an object-scope global on the file's
    /// owner. Stamped at import time from the
    /// <c>(SourceObject, TargetMemberName)</c> pair. Null for every
    /// other reference kind. Indexed so right-click "Find references"
    /// on a global variable returns its uses in a single seek. See
    /// <c>.design/al-reference-extractor-refactor.md</c> step 6.
    /// </summary>
    public long? TargetVariableId { get; set; }

    public ModuleVariable? TargetVariable { get; set; }

    /// <summary>
    /// Optional FK to the <see cref="ModuleSymbol"/> whose body emitted
    /// this reference — the calling procedure / trigger. Stamped at
    /// import time when the extractor was inside a procedure scope.
    /// Null for object-scope references (<c>extends_target</c>,
    /// <c>table_no</c>, top-level property refs) and for legacy rows
    /// imported before the column existed. Lets the forward-edge MCP
    /// tools answer "what does this procedure call?" via a single
    /// indexed seek instead of a line-range scan over every reference
    /// on the source object. See issue #181.
    /// </summary>
    public long? SourceSymbolId { get; set; }

    public ModuleSymbol? SourceSymbol { get; set; }
}
