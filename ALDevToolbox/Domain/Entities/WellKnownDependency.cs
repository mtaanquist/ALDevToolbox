namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// An entry in the well-known dependency catalogue. Used by the New Extension
/// flow to populate the dependency picker — admins maintain this list so users
/// don't have to retype GUIDs.
/// </summary>
public class WellKnownDependency
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The dependency app's GUID.</summary>
    public string DepId { get; set; } = string.Empty;

    public string DepName { get; set; } = string.Empty;
    public string DepPublisher { get; set; } = string.Empty;

    /// <summary>Pre-fills the version field; the user can override it per project.</summary>
    public string DepVersionDefault { get; set; } = string.Empty;

    /// <summary>Optional grouping label used to bucket items in the picker UI.</summary>
    public string? Category { get; set; }

    /// <summary>Display order within the catalogue.</summary>
    public int Ordering { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
