using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Domain.Entities;

/// <summary>Outcome an admin recorded against a recipe suggestion.</summary>
public enum RecipeSuggestionDecision
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// A user-submitted draft recipe awaiting admin review. Carries the full
/// proposed payload (type, title, description, keywords, files); on
/// approval the admin promotes it to a real <see cref="Recipe"/> in one
/// atomic write.
/// </summary>
public class RecipeSuggestion
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// User who submitted the suggestion. Nullable so deleting the suggester's
    /// account doesn't block on this row (FK uses <c>SetNull</c> on delete);
    /// the audit log retains the original id for the trail.
    /// </summary>
    public int? SuggestedByUserId { get; set; }
    public User? SuggestedByUser { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Keywords { get; set; } = string.Empty;

    /// <summary>Proposed recipe type. Carried through on approval.</summary>
    public RecipeType Type { get; set; }

    /// <summary>
    /// Proposed Markdown instructions. Carried through to the created
    /// <see cref="Recipe"/> on approval.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Proposed minimum BC application version. Carried through on approval.
    /// </summary>
    public int? MinimumApplicationVersionId { get; set; }
    public ApplicationVersion? MinimumApplicationVersion { get; set; }

    public RecipeSuggestionDecision Decision { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }

    /// <summary>Optional note left by the admin when rejecting. Surfaced in the audit trail.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>FK to the <see cref="Recipe"/> created on approval. <c>null</c> while pending or after a rejection.</summary>
    public int? ApprovedRecipeId { get; set; }
    public Recipe? ApprovedRecipe { get; set; }

    /// <summary>Proposed files. Cascade-deleted with the suggestion.</summary>
    public List<RecipeSuggestionFile> Files { get; set; } = new();
}
