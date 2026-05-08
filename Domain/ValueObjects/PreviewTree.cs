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
