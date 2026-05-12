namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One extension declared by a <see cref="RuntimeTemplate"/>. Templates declare
/// an ordered list of extensions — required ones are always emitted by the
/// generator; <c>Required = false</c> entries surface as opt-in checkboxes on
/// the New Workspace form. <see cref="Path"/> is the stable per-template
/// identifier (used by dependencies referencing <c>extension = "Core"</c>);
/// <see cref="NameTemplate"/> drives display and is mustache-substituted at
/// generation time. See <c>.design/templates-and-seeding.md</c>.
/// </summary>
public class WorkspaceExtension
{
    public int Id { get; set; }

    /// <summary>Denormalised owning organisation; mirrors the template's value.</summary>
    public int OrganizationId { get; set; }

    public int TemplateId { get; set; }
    public RuntimeTemplate? Template { get; set; }

    /// <summary>Display order within the parent template's extension list.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// The extension's stable identifier within the template (also used as the
    /// folder name in the generated ZIP, e.g. <c>Core</c>, <c>Hotfix</c>).
    /// Dependencies reference an extension by this value, so it must not change
    /// after the template is created without rewriting referrers.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Mustache template for the rendered extension name (e.g.
    /// <c>{{extension_prefix}} Core</c>). Substitution happens at generation
    /// time; the stored value carries the placeholders verbatim.
    /// </summary>
    public string NameTemplate { get; set; } = string.Empty;

    /// <summary>
    /// True when the extension is always emitted. False marks it as optional —
    /// the New Workspace form renders a checkbox; the user opts in.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// Optional per-extension override for the AL application version. Falls
    /// back to <c>TemplateDefaults.Application</c> when null. Most workspaces
    /// use one BC version; the override unblocks mixed-version edge cases.
    /// </summary>
    public string? Application { get; set; }

    /// <summary>Optional per-extension override for the AL runtime. Falls back to <see cref="RuntimeTemplate.Runtime"/>.</summary>
    public string? Runtime { get; set; }

    /// <summary>Optional explicit id-range start (inclusive). When null + <see cref="IdRangeTo"/> null, the generator auto-allocates.</summary>
    public int? IdRangeFrom { get; set; }

    /// <summary>Optional explicit id-range end (inclusive). When null + <see cref="IdRangeFrom"/> null, the generator auto-allocates.</summary>
    public int? IdRangeTo { get; set; }

    /// <summary>Top-level folders under this extension. Children live recursively in <see cref="WorkspaceExtensionFolder.Folders"/>.</summary>
    public List<WorkspaceExtensionFolder> Folders { get; set; } = new();

    /// <summary>Dependencies emitted into this extension's <c>app.json</c>.</summary>
    public List<WorkspaceExtensionDependency> Dependencies { get; set; } = new();
}
