namespace ALDevToolbox.Services;

/// <summary>
/// Structured authoring payload for a runtime template under the
/// unified-extensions model. Produced by
/// <see cref="TemplateTomlMapper.FromToml(string, bool)"/> on the TOML pane and
/// by the structured admin form's <c>FormState.ToAuthoring()</c>; consumed by
/// <see cref="TemplateService.CreateAsync"/> /
/// <see cref="TemplateService.UpdateAsync"/> into
/// <see cref="Domain.Entities.WorkspaceExtension"/> / its folder tree / its
/// dependencies.
/// </summary>
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
    IReadOnlyList<ExtensionAuthoring> Extensions,
    /// <summary>
    /// Optional per-template addition to the workspace's .code-workspace
    /// JSON. Null / empty means "inherit the org base"; otherwise a JSON
    /// object that the generator deep-merges onto the org base (template
    /// keys win on `settings`, replace wholesale elsewhere). See
    /// <see cref="GenerationService"/>.
    /// </summary>
    string? CodeWorkspaceJson = null,
    /// <summary>
    /// Workspace-relative paths of organisation-level always-included files
    /// the template opts into. References by <see cref="Domain.Entities.OrganizationFile.Path"/>
    /// rather than database id so the TOML round-trip stays portable across
    /// orgs / exports. The service resolves them to ids on save.
    /// </summary>
    IReadOnlyList<string>? IncludedFilePaths = null);

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
