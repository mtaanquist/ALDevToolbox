namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One <c>dependencies[]</c> entry on a <see cref="WorkspaceExtension"/>. The
/// row is a discriminated union over three reference shapes — exactly one of
/// the three reference groups is non-null on any row:
/// <list type="bullet">
///   <item><see cref="RefExtensionPath"/>: intra-template reference to another <see cref="WorkspaceExtension"/> by its stable <see cref="WorkspaceExtension.Path"/>.</item>
///   <item><see cref="RefModuleKey"/>: catalogue reference to a <see cref="Module"/> by its <see cref="Module.Key"/>.</item>
///   <item><see cref="LitId"/> + the literal name/publisher/version fields: a fixed, hand-typed dependency that lives outside any catalogue.</item>
/// </list>
/// The exclusivity is enforced by a CHECK constraint in the migration (see
/// <c>UnifyExtensions</c>); the service layer also validates the shape so
/// errors surface as field-keyed messages rather than DB constraint failures.
/// </summary>
public class WorkspaceExtensionDependency
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int WorkspaceExtensionId { get; set; }
    public WorkspaceExtension? Extension { get; set; }

    /// <summary>Display order within the parent extension's dependency list.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// When set, this row references another extension declared by the same
    /// template by its <see cref="WorkspaceExtension.Path"/>. The generator
    /// resolves the freshly-generated GUID + substituted name + workspace
    /// publisher at emission time.
    /// </summary>
    public string? RefExtensionPath { get; set; }

    /// <summary>
    /// When set, this row references a catalogue <see cref="Module"/> by its
    /// <see cref="Module.Key"/>. The generator resolves to either the cloned
    /// extension's identity (if the workspace selected the module) or to the
    /// module catalogue's stored dependency identifiers.
    /// </summary>
    public string? RefModuleKey { get; set; }

    /// <summary>The literal dependency's GUID (e.g. System Application).</summary>
    public string? LitId { get; set; }
    public string? LitName { get; set; }
    public string? LitPublisher { get; set; }
    public string? LitVersion { get; set; }
}
