using ALDevToolbox.Domain.ValueObjects;

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

    /// <summary>Default <c>app.json</c> <c>url</c> for every generated extension.</summary>
    public string? DefaultUrl { get; set; }

    /// <summary>Default <c>app.json</c> <c>logo</c> path for every generated extension.</summary>
    public string? DefaultLogo { get; set; }

    /// <summary>
    /// Org-wide list of country codes that get spliced into every generated
    /// <c>AppSourceCop.json</c>'s <c>supportedCountries</c> array. Org-wide
    /// because the supported markets are an organisation policy, not a
    /// per-template choice.
    /// </summary>
    public List<string> DefaultSupportedCountries { get; set; } = new();

    /// <summary>Lower bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeFrom { get; set; }

    /// <summary>Upper bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeTo { get; set; }

    /// <summary>One-line default brief copied into the form's <c>Brief</c> field.</summary>
    public string DefaultBrief { get; set; } = string.Empty;

    /// <summary>Longer default description copied into the form's <c>Description</c> field.</summary>
    public string DefaultCoreDescription { get; set; } = string.Empty;

    /// <summary>
    /// Admin-editable JSON template for the workspace's
    /// <c>{ShortName}.code-workspace</c> file. The generator runs mustache
    /// substitution over it, then overlays a computed <c>folders</c> array
    /// before writing the file — so the admin owns <c>settings</c> and any
    /// other top-level keys, and the generator owns the folder list.
    /// </summary>
    public string CodeWorkspaceJson { get; set; } = OrganizationDefaults.CodeWorkspaceJson;

    public DateTime UpdatedAt { get; set; }
}
