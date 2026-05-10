namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A folder produced inside every <strong>module</strong> extension generated
/// from a <see cref="RuntimeTemplate"/>. Parallel to <see cref="TemplateFolder"/>
/// but consumed by module extensions only — the Core extension uses
/// <see cref="RuntimeTemplate.Folders"/> instead. The split exists so a
/// template's Core scaffolding (App Install codeunits, setup tables,
/// permission sets) doesn't get duplicated into every module ZIP.
/// </summary>
public class TemplateModuleFolder
{
    public int Id { get; set; }

    /// <summary>Denormalised owning organisation; mirrors the template's value.</summary>
    public int OrganizationId { get; set; }

    /// <summary>Owning template. Cascade-deleted when the template is removed.</summary>
    public int TemplateId { get; set; }
    public RuntimeTemplate? Template { get; set; }

    /// <summary>Position within the parent template's module-folder list.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// Relative path inside the generated module extension folder, using forward
    /// slashes (e.g. <c>Source</c>). No leading slash, no <c>..</c> segments.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Files seeded into this folder when <c>IncludeExamples</c> is on. Empty
    /// list = generator emits a single <c>.gitkeep</c> regardless of the toggle.
    /// </summary>
    public List<TemplateModuleFile> Files { get; set; } = new();
}
