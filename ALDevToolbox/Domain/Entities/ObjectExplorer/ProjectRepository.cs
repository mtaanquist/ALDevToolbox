using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One Git repository belonging to a <see cref="Project"/>. A child table rather
/// than a JSON column because repos are listed, validated (per-provider URL
/// shape), and managed individually. <see cref="OrganizationId"/> is denormalised
/// from the parent so the row carries its own tenant key — consistent with the
/// "every editable entity carries organization_id" fence — and the build uses the
/// org PAT matching <see cref="Provider"/> to clone. See
/// <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public class ProjectRepository
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the parent project). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>The Git host this repo lives on; selects which org PAT clones it. Persisted as a string discriminator.</summary>
    public RepositoryProvider Provider { get; set; }

    /// <summary>The clone URL (HTTPS). Validated per-provider before save.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Short admin-facing label for the repo (e.g. the repo name).</summary>
    public string DisplayName { get; set; } = string.Empty;
}
