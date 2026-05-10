namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Per-organisation defaults used to pre-fill the New Workspace and New
/// Extension forms (Milestone P3.14). Exactly one row per organisation;
/// validation matches the rules in <see cref="Services.GenerationService"/>.
/// </summary>
public class OrganizationSettings
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Default <c>app.json</c> publisher for new templates and extensions.</summary>
    public string DefaultPublisher { get; set; } = string.Empty;

    /// <summary>Lower bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeFrom { get; set; }

    /// <summary>Upper bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeTo { get; set; }

    /// <summary>One-line default brief copied into the form's <c>Brief</c> field.</summary>
    public string DefaultBrief { get; set; } = string.Empty;

    /// <summary>Longer default description copied into the form's <c>Description</c> field.</summary>
    public string DefaultCoreDescription { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
