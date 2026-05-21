namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A reusable AL code pattern made of one or more files. Snippets are
/// org-scoped and managed by admins; end users browse and search them
/// from <c>/snippets</c> and can download all files as a ZIP archive via
/// <c>GET /api/snippets/{id}/download</c>.
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

    /// <summary>
    /// Optional usage instructions (e.g. "place this codeunit in the Setup
    /// area, then add the permission set entry"). Authored as Markdown and
    /// rendered on the public detail page; null/empty hides the section.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Optional minimum BC application version this snippet targets. Surfaced
    /// as a badge on the browser card and the detail page so users can tell at
    /// a glance whether it'll compile against their target runtime. Soft-deleted
    /// catalogue rows are still referencable so the badge keeps rendering;
    /// deprecated rows are also still referencable because flipping
    /// <c>Deprecated</c> on the catalogue shouldn't silently drop labels on
    /// existing snippets.
    /// </summary>
    public int? MinimumApplicationVersionId { get; set; }
    public ApplicationVersion? MinimumApplicationVersion { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. <c>null</c> means the row is active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Files attached to this snippet, ordered as the admin arranged them.</summary>
    public List<SnippetFile> Files { get; set; } = new();
}
