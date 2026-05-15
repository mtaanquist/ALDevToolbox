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
}
