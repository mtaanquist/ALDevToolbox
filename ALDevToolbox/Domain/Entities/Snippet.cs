namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A reusable AL code pattern made of one or more files. Snippets are
/// org-scoped and managed by admins; end users browse and search them
/// from <c>/snippets</c>. Per the design, snippets are viewed in-browser
/// only — there is no ZIP export.
/// </summary>
public class Snippet
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Display title shown in the browser and detail page. Unique within an organisation.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Free-text description rendered above the file list.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Space-separated tag tokens (lower-cased on save). Matched alongside
    /// <see cref="Title"/> and <see cref="Description"/> by the fuzzy
    /// <c>ILike</c> search.
    /// </summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>Hidden from the user-facing browser when true; remains in the admin list.</summary>
    public bool Deprecated { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. <c>null</c> means the row is active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Files attached to this snippet, ordered as the admin arranged them.</summary>
    public List<SnippetFile> Files { get; set; } = new();
}
