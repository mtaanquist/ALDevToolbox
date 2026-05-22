namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One proposed file inside a <see cref="RecipeSuggestion"/>. Mirrors
/// <see cref="RecipeFile"/> (including <see cref="RelativePath"/> for
/// folder layout) but lives in its own table so the pending queue
/// doesn't pollute the published catalogue.
/// </summary>
public class RecipeSuggestionFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int RecipeSuggestionId { get; set; }
    public RecipeSuggestion? Suggestion { get; set; }

    public int Ordering { get; set; }

    /// <summary>Folder path inside the proposed ZIP. Empty = root. Same rules as <see cref="RecipeFile.RelativePath"/>.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
