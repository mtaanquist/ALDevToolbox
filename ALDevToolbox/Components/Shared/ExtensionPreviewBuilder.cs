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
/// land in the ZIP. <c>app.json</c> follows the same path: it ships as a
/// seeded org file with <see cref="OrganizationFileScope.EveryExtension"/>,
/// so it appears whenever the template opts into it (every new template
/// does, by default).
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
        var list = new List<PreviewNode>();
        // Per-extension org files (admin-authored, opt-in per template).
        // Includes app.json, which is now a seeded EveryExtension org file
        // rather than a hardcoded emission. Paths can be nested (e.g.
        // ".vscode/settings.json") — graft them into the same tree so they
        // share intermediate folder nodes.
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
            // Example files stay in the tree when the toggle is off, flagged
            // so the renderer strikes them through — the toggle sits in the
            // preview card head and has to visibly cost something.
            var excluded = !includeExamples && file.IsExample;
            children.Add(PreviewNode.File(file.Path) with { IsExcluded = excluded });
        }
        AddGitkeepIfEmpty(children);
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
            // Example files stay in the tree when the toggle is off, flagged
            // so the renderer strikes them through — the toggle sits in the
            // preview card head and has to visibly cost something.
            var excluded = !includeExamples && file.IsExample;
            children.Add(PreviewNode.File(file.Path) with { IsExcluded = excluded });
        }
        AddGitkeepIfEmpty(children);
        return PreviewNode.Folder(folder.Path, children);
    }

    /// <summary>
    /// Mirrors the generator's empty-leaf rule: a folder with nothing the ZIP
    /// would actually contain gets a <c>.gitkeep</c>. Excluded rows don't
    /// count, so turning examples off makes the placeholder appear alongside
    /// them — which is what the ZIP will hold.
    /// </summary>
    private static void AddGitkeepIfEmpty(List<PreviewNode> children)
    {
        if (children.All(c => c.IsExcluded))
        {
            children.Add(PreviewNode.File(".gitkeep"));
        }
    }
}
