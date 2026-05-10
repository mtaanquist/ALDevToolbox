namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Logical kind of binary asset attached to an organisation. Today only
/// <see cref="Logo"/> ships; new kinds (favicon, etc.) can be added without a
/// schema change.
/// </summary>
public enum OrganizationAssetKind
{
    Logo,
}

/// <summary>
/// Per-organisation binary asset (Milestone P3.14). At most one row per
/// (<see cref="OrganizationId"/>, <see cref="Kind"/>) pair — uploading replaces
/// the existing row. Stored as a BLOB inside SQLite; size is capped at 256 KB
/// by <see cref="Services.OrganizationConfigService"/>.
/// </summary>
public class OrganizationAsset
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public OrganizationAssetKind Kind { get; set; }

    /// <summary>MIME type of <see cref="Content"/>, e.g. <c>image/png</c> or <c>image/svg+xml</c>.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Raw bytes. SVGs are sanitised before storage.</summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTime UpdatedAt { get; set; }
}
