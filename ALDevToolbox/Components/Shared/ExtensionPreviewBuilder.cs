using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Builds the per-extension <see cref="PreviewNode"/> contents shown on the
/// New Workspace, New Extension, and Template Detail pages. Mirrors what
/// <c>GenerationService</c> emits for a single extension folder: <c>app.json</c>,
/// optionally <c>AppSourceCop.json</c> (gated on <see cref="AppSourceCopSettings.Include"/>),
/// and the recursive folder/file tree. Empty folders pick up a <c>.gitkeep</c>
/// the same way the generator does. No fallback folders — what the template
/// declares is what the ZIP contains.
/// </summary>
public static class ExtensionPreviewBuilder
{
    public static IReadOnlyList<PreviewNode> BuildContents(
        IEnumerable<WorkspaceExtensionFolder> roots,
        bool includeExamples,
        bool includeAppSourceCop)
    {
        var contents = StartingContents(includeAppSourceCop);
        foreach (var folder in roots.OrderBy(f => f.Ordering))
        {
            contents.Add(BuildFolderNode(folder, includeExamples));
        }
        return contents;
    }

    public static IReadOnlyList<PreviewNode> BuildContents(
        IEnumerable<ModuleExtensionFolder> roots,
        bool includeExamples,
        bool includeAppSourceCop)
    {
        var contents = StartingContents(includeAppSourceCop);
        foreach (var folder in roots.OrderBy(f => f.Ordering))
        {
            contents.Add(BuildFolderNode(folder, includeExamples));
        }
        return contents;
    }

    private static List<PreviewNode> StartingContents(bool includeAppSourceCop)
    {
        var list = new List<PreviewNode> { PreviewNode.File("app.json") };
        if (includeAppSourceCop) list.Add(PreviewNode.File("AppSourceCop.json"));
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
