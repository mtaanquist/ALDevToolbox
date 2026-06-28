using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Services;

/// <summary>
/// Pure mappers between the shared folder/file authoring records and a module's
/// persisted <see cref="ModuleExtensionFolder"/> / <see cref="ModuleExtensionFile"/>
/// tree, both ways. Mirrors <see cref="TemplateAuthoringMapper.BuildFolder"/> /
/// <see cref="TemplateAuthoringMapper.BuildFolderAuthoring"/>: modules reuse the
/// same <see cref="FolderAuthoring"/> / <see cref="FileAuthoring"/> shapes (and so
/// the same <c>RecursiveFolderEditor</c> and the same <see cref="TemplateValidation"/>
/// folder-tree rules) — only the entity types differ.
/// </summary>
/// <remarks>
/// The denormalised <c>module_id</c> on nested rows is stamped at save time by
/// <see cref="Data.AppDbContext"/>'s folder-id propagation, exactly as for the
/// workspace side — the mapper only wires the parent/child navigations.
/// </remarks>
internal static class ModuleAuthoringMapper
{
    // ===== entity → authoring (edit-form load / TOML export) =====

    public static FolderAuthoring BuildFolderAuthoring(ModuleExtensionFolder folder) => new(
        Path: folder.Path,
        Folders: folder.Folders
            .OrderBy(f => f.Ordering)
            .Select(BuildFolderAuthoring)
            .ToList(),
        Files: folder.Files
            .OrderBy(f => f.Ordering)
            .Select(f => new FileAuthoring(f.Path, f.Content, f.IsExample))
            .ToList());

    // ===== authoring → entity (create / update) =====

    public static ModuleExtensionFolder BuildFolder(FolderAuthoring src, int orgId, int ordering) => new()
    {
        OrganizationId = orgId,
        Ordering = ordering,
        Path = src.Path.Trim(),
        Files = src.Files
            .Select((f, i) => new ModuleExtensionFile
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
}
