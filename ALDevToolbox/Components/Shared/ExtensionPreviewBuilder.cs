using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Builds the per-extension <see cref="PreviewNode"/> contents shown on the
/// New Workspace, New Extension, and Template Detail pages. Mirrors what
/// <c>GenerationService</c> emits for a single extension folder: <c>app.json</c>,
/// <c>AppSourceCop.json</c>, the recursive folder/file tree, and the
/// <c>libs</c> / <c>permissionsets</c> / <c>Translations</c> fallback folders
/// for any names the template didn't already declare. Empty folders pick up a
/// <c>.gitkeep</c> the same way the generator does.
/// </summary>
public static class ExtensionPreviewBuilder
{
    private static readonly string[] FallbackFolderNames = ["libs", "permissionsets", "Translations"];

    public static IReadOnlyList<PreviewNode> BuildContents(
        IEnumerable<WorkspaceExtensionFolder> roots,
        bool includeExamples)
    {
        var contents = StartingContents();
        foreach (var folder in roots.OrderBy(f => f.Ordering))
        {
            contents.Add(BuildFolderNode(folder, includeExamples));
        }
        AddFallbackFolders(contents);
        return contents;
    }

    public static IReadOnlyList<PreviewNode> BuildContents(
        IEnumerable<ModuleExtensionFolder> roots,
        bool includeExamples)
    {
        var contents = StartingContents();
        foreach (var folder in roots.OrderBy(f => f.Ordering))
        {
            contents.Add(BuildFolderNode(folder, includeExamples));
        }
        AddFallbackFolders(contents);
        return contents;
    }

    private static List<PreviewNode> StartingContents() => new()
    {
        PreviewNode.File("app.json"),
        PreviewNode.File("AppSourceCop.json"),
    };

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

    private static void AddFallbackFolders(List<PreviewNode> contents)
    {
        foreach (var name in FallbackFolderNames)
        {
            if (contents.Any(n => n.Kind != PreviewNodeKind.File
                                  && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            contents.Add(PreviewNode.Folder(name, new[] { PreviewNode.File(".gitkeep") }));
        }
    }
}
