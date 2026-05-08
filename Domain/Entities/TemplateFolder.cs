namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A single folder produced by a <see cref="RuntimeTemplate"/>. Rows are ordered
/// per template via <see cref="Ordering"/> so the generator emits them in a
/// predictable shape.
/// </summary>
public class TemplateFolder
{
    public int Id { get; set; }

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
    /// Optional name of an example folder under
    /// <c>Templates.seed/&lt;runtime&gt;/examples/</c> whose AL files are copied
    /// into this folder when the user enables examples. <c>null</c> means the
    /// folder is always seeded with a single <c>.gitkeep</c>.
    /// </summary>
    public string? ExamplePath { get; set; }
}
