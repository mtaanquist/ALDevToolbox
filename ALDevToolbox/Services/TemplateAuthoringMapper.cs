using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Services;

/// <summary>
/// Pure mappers between the template authoring shape (the records the admin
/// form / TOML editor produce) and the persisted EF entities, both ways.
/// Extracted from <see cref="TemplateService"/> so the create/update path and
/// the authoring-load path share one home for the entity↔authoring conversion
/// and the recursion is easy to follow on its own.
/// </summary>
internal static class TemplateAuthoringMapper
{
    // ===== Entity → authoring (for the edit form / TOML export) =====

    public static ExtensionAuthoring BuildExtensionAuthoring(WorkspaceExtension ext) => new(
        Path: ext.Path,
        NameTemplate: ext.NameTemplate,
        Required: ext.Required,
        Application: ext.Application,
        Runtime: ext.Runtime,
        IdRangeFrom: ext.IdRangeFrom,
        IdRangeTo: ext.IdRangeTo,
        Folders: ext.Folders
            .OrderBy(f => f.Ordering)
            .Select(BuildFolderAuthoring)
            .ToList(),
        Dependencies: ext.Dependencies
            .OrderBy(d => d.Ordering)
            .Select(d => new DependencyAuthoring(
                RefExtensionPath: d.RefExtensionPath,
                RefModuleKey: d.RefModuleKey,
                LitId: d.LitId,
                LitName: d.LitName,
                LitPublisher: d.LitPublisher,
                LitVersion: d.LitVersion))
            .ToList());

    public static FolderAuthoring BuildFolderAuthoring(WorkspaceExtensionFolder folder) => new(
        Path: folder.Path,
        Folders: folder.Folders
            .OrderBy(f => f.Ordering)
            .Select(BuildFolderAuthoring)
            .ToList(),
        Files: folder.Files
            .OrderBy(f => f.Ordering)
            .Select(f => new FileAuthoring(f.Path, f.Content, f.IsExample))
            .ToList());

    // ===== Authoring → entity (for create / update) =====

    public static WorkspaceExtension BuildExtension(ExtensionAuthoring src, int orgId, int ordering) => new()
    {
        OrganizationId = orgId,
        Ordering = ordering,
        Path = src.Path.Trim(),
        NameTemplate = src.NameTemplate.Trim(),
        Required = src.Required,
        Application = string.IsNullOrEmpty(src.Application) ? null : src.Application.Trim(),
        Runtime = string.IsNullOrEmpty(src.Runtime) ? null : src.Runtime.Trim(),
        IdRangeFrom = src.IdRangeFrom,
        IdRangeTo = src.IdRangeTo,
        Folders = src.Folders
            .Select((f, i) => BuildFolder(f, orgId, ordering: i))
            .ToList(),
        Dependencies = src.Dependencies
            .Select((d, i) => BuildDependency(d, orgId, ordering: i))
            .ToList(),
    };

    public static WorkspaceExtensionFolder BuildFolder(FolderAuthoring src, int orgId, int ordering) => new()
    {
        OrganizationId = orgId,
        Ordering = ordering,
        Path = src.Path.Trim(),
        Files = src.Files
            .Select((f, i) => new WorkspaceExtensionFile
            {
                OrganizationId = orgId,
                Ordering = i,
                Path = f.Path.Trim(),
                Content = f.Content ?? string.Empty,
                IsExample = f.IsExample,
            })
            .ToList(),
        Folders = src.Folders
            .Select((f, i) => BuildFolder(f, orgId, ordering: i))
            .ToList(),
    };

    public static WorkspaceExtensionDependency BuildDependency(DependencyAuthoring src, int orgId, int ordering) => new()
    {
        OrganizationId = orgId,
        Ordering = ordering,
        RefExtensionPath = src.RefExtensionPath?.Trim(),
        RefModuleKey = src.RefModuleKey?.Trim(),
        LitId = src.LitId?.Trim(),
        LitName = src.LitName?.Trim(),
        LitPublisher = src.LitPublisher?.Trim(),
        LitVersion = src.LitVersion?.Trim(),
    };

    /// <summary>
    /// Maps the admin form's input into the storage shape for
    /// <see cref="RuntimeTemplate.CodeWorkspaceJson"/>: empty / whitespace
    /// becomes <c>null</c> so "no override" round-trips cleanly through the
    /// DB and the audit log, otherwise the input is trimmed verbatim. The
    /// validator already proved any non-empty value parses to a JSON object.
    /// </summary>
    public static string? NormaliseCodeWorkspaceJson(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
