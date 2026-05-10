namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Always-included text file written into every workspace generated for the
/// organisation (Milestone P3.14). Stored verbatim; mustache substitution runs
/// at generation time when <see cref="MustacheEnabled"/> is true. Files are
/// emitted at the workspace root before per-extension folders so a per-template
/// file can override on path collision; see <c>.design/generation-engine.md</c>.
/// </summary>
public class OrganizationFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// Workspace-relative path with forward slashes (e.g. <c>.editorconfig</c>
    /// or <c>docs/onboarding.md</c>). No leading slash and no <c>..</c>
    /// segments. Unique per organisation.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Raw file body. Mustache variables are substituted at generation time.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// When true, mustache variables in <see cref="Content"/> are substituted
    /// against the same context per-template files use ({{workspaceName}},
    /// {{publisher}}, {{prefix}}, …).
    /// </summary>
    public bool MustacheEnabled { get; set; }

    /// <summary>Position in the admin's reorderable list.</summary>
    public int Ordering { get; set; }

    public DateTime UpdatedAt { get; set; }
}
