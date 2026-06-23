namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A cached row from Microsoft's Business Central artifact index — one
/// available OnPrem build for a given country. Populated by
/// <see cref="Services.ObjectExplorer.BcArtifactService"/> when an admin
/// refreshes the Artifacts tab on the Import Release page, so the version
/// table renders from the database rather than re-querying Azure on every
/// page load.
///
/// <para>
/// Org-scoped (the EF query filter narrows reads to the current org) because
/// "has this org imported this version?" is inherently per-tenant and the
/// upstream index JSON is small enough to re-fetch per org. There is no stored
/// "imported" flag — whether a version is already in the catalogue is derived
/// at render time by matching the computed release label, which avoids the
/// cache drifting out of sync with <see cref="Release"/>.
/// </para>
///
/// <para>
/// The platform-artifact URL is deliberately not stored: it lives in the
/// downloaded application artifact's <c>manifest.json</c> (<c>platformUrl</c>)
/// and is resolved by the import worker at download time, so the cache only
/// needs the application-artifact URL. See <c>.design/object-explorer.md</c>.
/// </para>
/// </summary>
public class BcArtifactVersion
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>BC localisation/country code the index was fetched for, e.g. <c>dk</c> or <c>w1</c>.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Full four-part artifact version, e.g. <c>28.2.50931.51727</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Major.Minor of <see cref="Version"/>, e.g. <c>28.2</c>. The dedup key behind the derived "Imported" status and the release label.</summary>
    public string MajorMinor { get; set; } = string.Empty;

    /// <summary>Resolved application-artifact download URL (the CDN/Front Door host).</summary>
    public string ApplicationUrl { get; set; } = string.Empty;

    /// <summary>When this row was last refreshed from the upstream index.</summary>
    public DateTime RefreshedAt { get; set; }
}
