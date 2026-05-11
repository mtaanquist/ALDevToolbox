namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A single folder produced by a <see cref="RuntimeTemplate"/>. Rows are ordered
/// per template via <see cref="Ordering"/> so the generator emits them in a
/// predictable shape. The folder's seeded contents live in <see cref="Files"/>;
/// a folder with no files generates a single <c>.gitkeep</c> placeholder.
/// </summary>
public class TemplateFolder
{
    public int Id { get; set; }

    /// <summary>
    /// Denormalised owning organisation. Mirrors <see cref="Template"/>'s
    /// <c>OrganizationId</c> so cross-org filters can apply at this row's level
    /// without joining; the service layer keeps the two in sync.
    /// </summary>
    public int OrganizationId { get; set; }

    /// <summary>Owning template. Cascade-deleted when the template is removed.</summary>
    public int TemplateId { get; set; }
    public RuntimeTemplate? Template { get; set; }

    /// <summary>Position within the parent template's folder list.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// Relative path inside the generated extension folder, using forward slashes
    /// (e.g. <c>Source/Foundation</c>). No leading slash, no <c>..</c> segments.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Files seeded into this folder when <c>IncludeExamples</c> is on. Empty
    /// list = generator emits a single <c>.gitkeep</c> regardless of the toggle.
    /// </summary>
    public List<TemplateFile> Files { get; set; } = new();
}
