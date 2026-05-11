namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One proposed file inside a <see cref="SnippetSuggestion"/>. Mirrors
/// <see cref="SnippetFile"/> but lives in its own table so the pending
/// queue doesn't pollute the published catalogue.
/// </summary>
public class SnippetSuggestionFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int SnippetSuggestionId { get; set; }
    public SnippetSuggestion? Suggestion { get; set; }

    public int Ordering { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
