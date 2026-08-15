namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// How a recipe left the app. Recorded because the customer attribution alone
/// answered only half the question it was asked: single-file recipes are almost
/// always taken with per-file Copy, which never opened the download modal, so
/// the history systematically under-counted exactly the recipes people used
/// most. See issue #539.
/// </summary>
public enum RecipeUseSource
{
    /// <summary>The recipe ZIP was downloaded. May carry a customer name.</summary>
    Download,

    /// <summary>
    /// A file was copied to the clipboard from the recipe page. Never carries a
    /// customer - copying asks nothing, deliberately - and is recorded at most
    /// once per visit however many files the user copies.
    /// </summary>
    Copy,
}

/// <summary>
/// One record of a <see cref="Recipe"/> leaving the app - downloaded as a ZIP,
/// or copied file-by-file from the recipe page. Captured so that, if a bug is
/// later found in a recipe, admins can see where it went and where a fix needs
/// to land. Org-scoped like every other editable entity; surfaced on the admin
/// recipe page only.
/// </summary>
public class RecipeDownload
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Recipe that was used. Cascade-deleted with the recipe.</summary>
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    /// <summary>
    /// Free-text customer name this use was applied to, trimmed, at most 200
    /// characters. Optional: the download modal asks for it and explains why,
    /// but does not gate the download on it, because a required field that a
    /// consultant downloading for a demo cannot answer honestly just fills the
    /// column with "test" and "x". Null means "not recorded", which is a more
    /// useful thing to know than a fake name. Always null for
    /// <see cref="RecipeUseSource.Copy"/>. See issue #539.
    /// </summary>
    public string? CustomerName { get; set; }

    /// <summary>How the recipe left the app.</summary>
    public RecipeUseSource Source { get; set; } = RecipeUseSource.Download;

    /// <summary>
    /// The user who performed the download. Nullable + SetNull on delete so the
    /// history survives a user being removed (like the audit log's attribution).
    /// </summary>
    public int? DownloadedByUserId { get; set; }
    public User? DownloadedByUser { get; set; }

    public DateTime DownloadedAt { get; set; }
}
