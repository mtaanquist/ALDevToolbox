using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A reusable AL artefact made of one or more files, organised in the
/// per-organisation Cookbook. Recipes carry a <see cref="Type"/> so the
/// browser can group small one-shot snippets, multi-file patterns, and
/// near-complete modules side-by-side. Recipes are org-scoped and managed
/// by admins; end users browse and search them from <c>/cookbook</c> and
/// can download all files as a ZIP archive via
/// <c>GET /api/cookbook/{id}/download</c> with the folder layout preserved.
/// </summary>
public class Recipe
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
    /// Comma-separated tag tokens (lower-cased on save). A tag may contain
    /// spaces — authors wrap a phrase in double quotes to keep it as one tag
    /// (e.g. <c>"document attachments"</c>). Matched alongside
    /// <see cref="Title"/> and <see cref="Description"/> by the fuzzy
    /// <c>ILike</c> search; the substring match spans the comma delimiter, so
    /// search is unaffected by the grouping. See
    /// <see cref="Services.RecipeService.NormaliseKeywords"/>.
    /// </summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>
    /// Coarse type tag. Drives the chip-row filter and the badge on each
    /// card but never participates in the search expression — type is a
    /// post-filter on results, not a search dimension.
    /// </summary>
    public RecipeType Type { get; set; }

    /// <summary>Hidden from the user-facing browser when true; remains in the admin list.</summary>
    public bool Deprecated { get; set; }

    /// <summary>
    /// Optional usage instructions (e.g. "place this codeunit in the Setup
    /// area, then add the permission set entry"). Authored as Markdown and
    /// rendered on the public detail page; null/empty hides the section.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Optional minimum BC application version this recipe targets. Surfaced
    /// as a badge on the browser card and the detail page so users can tell at
    /// a glance whether it'll compile against their target runtime. Soft-deleted
    /// catalogue rows are still referencable so the badge keeps rendering;
    /// deprecated rows are also still referencable because flipping
    /// <c>Deprecated</c> on the catalogue shouldn't silently drop labels on
    /// existing recipes.
    /// </summary>
    public int? MinimumApplicationVersionId { get; set; }
    public ApplicationVersion? MinimumApplicationVersion { get; set; }

    /// <summary>
    /// Optional estimate of how many hours of work this recipe saves an
    /// implementer, surfaced as a factbox on the detail page for commercial
    /// guidance ("a customer should be billed at least this much"). Measured
    /// in hours rather than money so it stays currency-neutral; <c>null</c>
    /// means "not estimated" and hides the factbox row. Visible to every user
    /// who can see the recipe.
    /// </summary>
    public decimal? EstimatedValueHours { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. <c>null</c> means the row is active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Files attached to this recipe, ordered as the admin arranged them.</summary>
    public List<RecipeFile> Files { get; set; } = new();
}
