namespace ALDevToolbox.Services;

/// <summary>
/// Structured authoring payload for a runtime template under the
/// unified-extensions model. Produced by
/// <see cref="TemplateTomlMapper.FromToml(string, bool)"/>; consumed (in a
/// follow-up slice) by the rewritten <see cref="TemplateService.CreateAsync"/>
/// / <see cref="TemplateService.UpdateAsync"/> bridge into
/// <see cref="Domain.Entities.WorkspaceExtension"/> / its folder tree / its
/// dependencies.
/// </summary>
/// <remarks>
/// Distinct from the legacy <see cref="TemplateInput"/>, which mirrors the
/// pre-unified admin form. Both coexist during the Issue #54 transition; the
/// form's <c>FormState.ToInput()</c> still builds <see cref="TemplateInput"/>,
/// the TOML editor builds this. They converge when the admin form is
/// rewritten around the new shape.
/// </remarks>
public record TemplateAuthoring(
    string Key,
    string Runtime,
    string Name,
    string? Description,
    string DefaultsJson,
    string AppSourceCopJson,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    int ModuleIdRangeStart,
    int ModuleIdRangeSize,
    bool Deprecated,
    bool IsDefault,
    string? DefaultApplicationVersionKey,
    IReadOnlyList<string> DefaultModuleKeys,
    IReadOnlyList<ExtensionAuthoring> Extensions);

/// <summary>One declared extension in the authoring payload.</summary>
public record ExtensionAuthoring(
    string Path,
    string NameTemplate,
    bool Required,
    string? Application,
    string? Runtime,
    int? IdRangeFrom,
    int? IdRangeTo,
    IReadOnlyList<FolderAuthoring> Folders,
    IReadOnlyList<DependencyAuthoring> Dependencies);

/// <summary>
/// One folder in the recursive tree. <see cref="Folders"/> nests; files attach
/// at any depth via <see cref="Files"/>.
/// </summary>
public record FolderAuthoring(
    string Path,
    IReadOnlyList<FolderAuthoring> Folders,
    IReadOnlyList<FileAuthoring> Files);

/// <summary>One file attached to a folder. <see cref="IsExample"/> controls "include examples" gating.</summary>
public record FileAuthoring(string Path, string Content, bool IsExample);

/// <summary>
/// One <c>[[extensions.dependencies]]</c> entry. Exactly one of the three
/// reference groups is populated:
/// <list type="bullet">
///   <item><see cref="RefExtensionPath"/>: intra-template reference by <see cref="ExtensionAuthoring.Path"/>.</item>
///   <item><see cref="RefModuleKey"/>: catalogue reference by <see cref="Domain.Entities.Module.Key"/>.</item>
///   <item><see cref="LitId"/> + <see cref="LitName"/> + <see cref="LitPublisher"/> + <see cref="LitVersion"/>: literal dependency.</item>
/// </list>
/// The mapper validates the one-of constraint before returning; downstream
/// code can rely on exactly one being non-null.
/// </summary>
public record DependencyAuthoring(
    string? RefExtensionPath,
    string? RefModuleKey,
    string? LitId,
    string? LitName,
    string? LitPublisher,
    string? LitVersion);
