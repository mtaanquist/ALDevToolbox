using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Builds the per-extension <see cref="PreviewNode"/> contents shown on the
/// New Workspace, New Extension, and Template Detail pages. Mirrors what
/// <c>GenerationService</c> emits for a single extension folder: <c>app.json</c>,
/// any per-extension-scoped organisation files the template opts into, and
/// the recursive folder/file tree. Empty folders pick up a <c>.gitkeep</c>
/// the same way the generator does. No fallback folders — what the template
/// declares is what the ZIP contains.
/// </summary>
/// <remarks>
/// AppSourceCop.json used to be added unconditionally on a per-template
/// flag; it now arrives through <paramref name="perExtensionFilePaths"/>
/// when an admin opted into an <c>AppSourceCop.json</c>
/// <see cref="OrganizationFile"/> with
/// <see cref="OrganizationFileScope.EveryExtension"/>. The flag is gone so
/// the preview never lies about an AppSourceCop.json that won't actually
/// land in the ZIP.
/// </remarks>
public static class ExtensionPreviewBuilder
{
    public static IReadOnlyList<PreviewNode> BuildContents(
        IEnumerable<WorkspaceExtensionFolder> roots,
        bool includeExamples,
        IReadOnlyList<string> perExtensionFilePaths)
    {
        var contents = StartingContents(perExtensionFilePaths);
        foreach (var folder in roots.OrderBy(f => f.Ordering))
        {
            contents.Add(BuildFolderNode(folder, includeExamples));
        }
        return contents;
    }

    public static IReadOnlyList<PreviewNode> BuildContents(
        IEnumerable<ModuleExtensionFolder> roots,
        bool includeExamples,
        IReadOnlyList<string> perExtensionFilePaths)
    {
        var contents = StartingContents(perExtensionFilePaths);
        foreach (var folder in roots.OrderBy(f => f.Ordering))
        {
            contents.Add(BuildFolderNode(folder, includeExamples));
        }
        return contents;
    }

    private static List<PreviewNode> StartingContents(IReadOnlyList<string> perExtensionFilePaths)
    {
        var list = new List<PreviewNode> { PreviewNode.File("app.json") };
        // Per-extension org files (admin-authored, opt-in per template).
        // Paths can be nested (e.g. ".vscode/settings.json") — graft them
        // into the same tree so they share intermediate folder nodes.
        foreach (var path in perExtensionFilePaths)
        {
            PreviewTreeBuilder.GraftFile(list, path);
        }
        return list;
    }

    private static PreviewNode BuildFolderNode(WorkspaceExtensionFolder folder, bool includeExamples)
    {
        var children = new List<PreviewNode>();
        foreach (var sub in folder.Folders.OrderBy(f => f.Ordering))
        {
            children.Add(BuildFolderNode(sub, includeExamples));
        }
        foreach (var file in folder.Files.OrderBy(f => f.Ordering))
        {
            if (!includeExamples && file.IsExample) continue;
            children.Add(PreviewNode.File(file.Path));
        }
        if (children.Count == 0)
        {
            children.Add(PreviewNode.File(".gitkeep"));
        }
        return PreviewNode.Folder(folder.Path, children);
    }

    private static PreviewNode BuildFolderNode(ModuleExtensionFolder folder, bool includeExamples)
    {
        var children = new List<PreviewNode>();
        foreach (var sub in folder.Folders.OrderBy(f => f.Ordering))
        {
            children.Add(BuildFolderNode(sub, includeExamples));
        }
        foreach (var file in folder.Files.OrderBy(f => f.Ordering))
        {
            if (!includeExamples && file.IsExample) continue;
            children.Add(PreviewNode.File(file.Path));
        }
        if (children.Count == 0)
        {
            children.Add(PreviewNode.File(".gitkeep"));
        }
        return PreviewNode.Folder(folder.Path, children);
    }
}
