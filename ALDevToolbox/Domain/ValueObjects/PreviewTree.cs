namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// One node in the live folder-tree preview shown on the New Workspace and
/// New Extension pages. Built from the current form state — see
/// <c>ui-design.md</c> for the visual spec.
/// </summary>
public record PreviewNode(
    string Name,
    PreviewNodeKind Kind,
    IReadOnlyList<PreviewNode> Children
)
{
    /// <summary>
    /// Marks a node as "newly added" relative to an existing workspace, so the
    /// preview renderer can label it. Used by the New Extension page when a
    /// workspace config has been imported and the user is scaffolding a sibling
    /// extension; the rest of the tree shows what's already there, this flag
    /// pins the addition.
    /// </summary>
    public bool IsNew { get; init; }

    public static PreviewNode Folder(string name, IEnumerable<PreviewNode>? children = null) =>
        new(name, PreviewNodeKind.Folder, children?.ToList() ?? new List<PreviewNode>());

    public static PreviewNode Extension(string name, IEnumerable<PreviewNode>? children = null) =>
        new(name, PreviewNodeKind.Extension, children?.ToList() ?? new List<PreviewNode>());

    public static PreviewNode File(string name) =>
        new(name, PreviewNodeKind.File, Array.Empty<PreviewNode>());
}

/// <summary>
/// Discriminator on <see cref="PreviewNode"/>. Drives icon and colour choice:
/// <see cref="Extension"/> nodes pick up the accent treatment described in
/// <c>ui-design.md</c>; everything else renders in the secondary text colour.
/// </summary>
public enum PreviewNodeKind
{
    /// <summary>The workspace root (e.g. <c>AcmeCustomer/</c>).</summary>
    Workspace,

    /// <summary>A generated extension folder (Core or a module).</summary>
    Extension,

    /// <summary>A grouping or static folder (<c>.assets</c>, <c>Source</c>, <c>Translations</c>).</summary>
    Folder,

    /// <summary>A leaf file (<c>app.json</c>, <c>.gitkeep</c>, an example <c>.al</c>).</summary>
    File,
}

/// <summary>
/// Helpers for building a <see cref="PreviewNode"/> tree from a list of
/// slash-delimited template folder paths. Splitting each path independently
/// would emit one parent chain per leaf, so siblings that share a prefix
/// (e.g. <c>Source/Foundation</c> and <c>Source/Finance</c>) would each get
/// their own <c>Source/</c> wrapper. This helper merges them so a shared
/// prefix renders as a single parent.
/// </summary>
public static class PreviewTreeBuilder
{
    /// <summary>
    /// Folds the given <paramref name="entries"/> into a list of merged
    /// <see cref="PreviewNode"/>s ready to be spliced into an extension's
    /// contents. <c>Path</c> uses <c>/</c> as the separator; <c>Leaves</c>
    /// is the file list to attach at the deepest segment.
    /// </summary>
    public static List<PreviewNode> BuildFolderChildren(
        IEnumerable<(string Path, IReadOnlyList<PreviewNode> Leaves)> entries)
    {
        var root = new MutableNode();
        foreach (var (path, leaves) in entries)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            foreach (var segment in segments)
            {
                if (!node.ChildIndex.TryGetValue(segment, out var child))
                {
                    child = new MutableNode { Name = segment };
                    node.ChildIndex[segment] = child;
                    node.Children.Add(child);
                }
                node = child;
            }
            node.Files.AddRange(leaves);
        }
        return root.Children.Select(ToPreview).ToList();
    }

    /// <summary>
    /// Finishes a workspace-root preview the same way every generator-backed
    /// page does: appends the two side-car files the generator always emits
    /// (<c>{rootName}.code-workspace</c> and the workspace config file), grafts
    /// each workspace-root-scoped organisation file into the tree, then wraps
    /// the lot in a <see cref="PreviewNodeKind.Workspace"/> root and sorts it
    /// for display. <paramref name="children"/> is the per-page set of
    /// extension/module/logo nodes already assembled by the caller; it is
    /// mutated in place (the side-cars and grafted files are appended to it).
    /// <paramref name="configFileName"/> is passed in rather than referenced
    /// directly so this value-object layer stays free of a Services dependency.
    /// </summary>
    public static PreviewNode BuildWorkspacePreview(
        string rootName,
        IList<PreviewNode> children,
        string configFileName,
        IEnumerable<string> workspaceRootPaths)
    {
        children.Add(PreviewNode.File($"{rootName}.code-workspace"));
        children.Add(PreviewNode.File(configFileName));
        foreach (var path in workspaceRootPaths)
        {
            GraftFile(children, path);
        }
        return SortForDisplay(new PreviewNode(
            rootName, PreviewNodeKind.Workspace, children as IReadOnlyList<PreviewNode> ?? children.ToList()));
    }

    /// <summary>
    /// Returns a copy of <paramref name="node"/> with every level of children
    /// reordered to mimic Windows File Explorer: folders first (alphabetical),
    /// then files (alphabetical). Comparison is case-insensitive so casing
    /// inconsistencies don't surprise users.
    /// </summary>
    public static PreviewNode SortForDisplay(PreviewNode node)
    {
        if (node.Children.Count == 0) return node;
        var sorted = node.Children
            .Select(SortForDisplay)
            .OrderBy(c => c.Kind == PreviewNodeKind.File ? 1 : 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return node with { Children = sorted };
    }

    /// <summary>
    /// Walks a forward-slash-separated <paramref name="path"/> and grafts a
    /// leaf <see cref="PreviewNode.File"/> into the supplied
    /// <paramref name="siblings"/>, materialising intermediate folders as
    /// needed. Two grafts that share a prefix end up under one folder so the
    /// preview matches what an OS file explorer would render. Duplicate
    /// files at the same path are no-ops. The factory methods always
    /// allocate mutable <c>List&lt;PreviewNode&gt;</c> for children, so the
    /// downward walk casts each level back to <see cref="IList{T}"/>.
    /// </summary>
    public static void GraftFile(IList<PreviewNode> siblings, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;
        GraftRecursive(siblings, segments, 0);
    }

    private static void GraftRecursive(IList<PreviewNode> siblings, string[] segments, int idx)
    {
        var name = segments[idx];
        if (idx == segments.Length - 1)
        {
            if (!siblings.Any(n => n.Kind == PreviewNodeKind.File && n.Name == name))
            {
                siblings.Add(PreviewNode.File(name));
            }
            return;
        }
        var existing = siblings.FirstOrDefault(n => n.Kind == PreviewNodeKind.Folder && n.Name == name);
        if (existing is null)
        {
            var children = new List<PreviewNode>();
            siblings.Add(new PreviewNode(name, PreviewNodeKind.Folder, children));
            GraftRecursive(children, segments, idx + 1);
        }
        else if (existing.Children is IList<PreviewNode> nestedList)
        {
            GraftRecursive(nestedList, segments, idx + 1);
        }
    }

    private static PreviewNode ToPreview(MutableNode node)
    {
        var children = new List<PreviewNode>(node.Children.Count + node.Files.Count);
        foreach (var child in node.Children)
        {
            children.Add(ToPreview(child));
        }
        children.AddRange(node.Files);
        return PreviewNode.Folder(node.Name, children);
    }

    private sealed class MutableNode
    {
        public string Name = string.Empty;
        public List<MutableNode> Children { get; } = new();
        public Dictionary<string, MutableNode> ChildIndex { get; } = new();
        public List<PreviewNode> Files { get; } = new();
    }
}
