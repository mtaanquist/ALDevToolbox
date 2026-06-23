namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One record of a <see cref="Recipe"/> being downloaded for a named customer.
/// Captured when a user produces the recipe ZIP so that, if a bug is later
/// found in a recipe, admins can trace which customers received the affected
/// version and where a fix needs to land. The customer name is required at
/// download time (the download modal enforces it). Org-scoped like every other
/// editable entity; surfaced on the admin recipe page only.
/// </summary>
public class RecipeDownload
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Recipe that was downloaded. Cascade-deleted with the recipe.</summary>
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    /// <summary>Free-text customer name this download was applied to. Required, trimmed, ≤200 chars.</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>
    /// The user who performed the download. Nullable + SetNull on delete so the
    /// history survives a user being removed (like the audit log's attribution).
    /// </summary>
    public int? DownloadedByUserId { get; set; }
    public User? DownloadedByUser { get; set; }

    public DateTime DownloadedAt { get; set; }
}
