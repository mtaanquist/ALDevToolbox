using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Mcp.Dtos;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools that wrap <see cref="SnippetService"/>. Lets an agent answer
/// "is there a snippet for X?"-style questions without going through the
/// web UI. All reads are org-scoped by the EF query filter.
/// </summary>
[McpServerToolType]
public sealed class SnippetTools
{
    private readonly SnippetService _snippets;
    private readonly AppDbContext _db;
    private readonly SnippetSuggestionService _suggestions;

    public SnippetTools(SnippetService snippets, AppDbContext db, SnippetSuggestionService suggestions)
    {
        _snippets = snippets;
        _db = db;
        _suggestions = suggestions;
    }

    [McpServerTool(Name = "search_snippets", ReadOnly = true)]
    [Description("Searches the caller's organisation snippet library by free-text query (title, description, keywords). Returns lightweight summaries — use get_snippet to fetch the files.")]
    public async Task<IReadOnlyList<SnippetSummary>> SearchAsync(
        [Description("Search text. Matches against title, description, and keywords. Pass an empty string to list everything.")] string query,
        [Description("If true, also return snippets that have been marked deprecated. Defaults to false.")] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        var rows = await _snippets.SearchAsync(query, includeDeprecated, ct);
        if (rows.Count == 0) return Array.Empty<SnippetSummary>();
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.SnippetFiles.AsNoTracking()
            .Where(f => ids.Contains(f.SnippetId))
            .GroupBy(f => f.SnippetId)
            .Select(g => new { SnippetId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SnippetId, x => x.Count, ct);
        return rows.Select(s => new SnippetSummary(
            s.Id, s.Title, s.Description, s.Keywords, s.Deprecated,
            counts.TryGetValue(s.Id, out var c) ? c : 0,
            s.MinimumApplicationVersion?.Name,
            s.MinimumApplicationVersion?.Application)).ToList();
    }

    [McpServerTool(Name = "get_snippet", ReadOnly = true)]
    [Description("Returns the full content of a snippet (every file's path and body) by id. Use search_snippets to find candidate ids.")]
    public async Task<SnippetDetail> GetAsync(
        [Description("Snippet id from search_snippets.")] int id,
        CancellationToken ct = default)
    {
        var row = await _snippets.GetAsync(id, ct);
        if (row is null)
        {
            throw new McpException($"Snippet {id} not found, or not visible to your organisation.");
        }
        return new SnippetDetail(
            row.Id, row.Title, row.Description, row.Keywords, row.Deprecated,
            row.Files.OrderBy(f => f.Ordering).Select(f => new SnippetFileDto(f.FileName, f.Content)).ToList(),
            row.Instructions,
            row.MinimumApplicationVersion?.Name,
            row.MinimumApplicationVersion?.Application);
    }

    [McpServerTool(Name = "suggest_snippet", ReadOnly = false, Idempotent = false)]
    [Description(
        "Submits a draft snippet to the caller organisation's admin review queue at /admin/snippets/suggestions. " +
        "The submission is NOT immediately visible from search_snippets — an admin must approve it first. " +
        "Use this when you have a useful AL pattern (e.g. extracted and generalised from a customer codebase, " +
        "with customer-specific names stripped) that the team's snippet library is missing. " +
        "Provide a descriptive Title, a 1–3 sentence Description of when to use it, comma-separated Keywords, " +
        "and one or more flat-named files (no slashes or '..'). Returns the new SuggestionId.")]
    public async Task<SuggestSnippetResult> SuggestAsync(
        [Description("The draft snippet payload. Title and Description are required; Files must contain at least one entry with a flat file name (no path separators) and the file body in Content.")] SuggestSnippetInput input,
        CancellationToken ct = default)
    {
        try
        {
            var id = await _suggestions.SubmitAsync(input.ToDomain(), ct);
            return new SuggestSnippetResult(
                id,
                "Submitted for review. An admin will approve or reject it at /admin/snippets/suggestions.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    [McpServerTool(Name = "update_snippet_suggestion", ReadOnly = false, Idempotent = true)]
    [Description(
        "Edits a pending snippet suggestion that the caller previously submitted via suggest_snippet. " +
        "Only the original submitter can edit, and only while the suggestion is still Pending — " +
        "approved or rejected suggestions are terminal and refuse the update (rejected suggestions " +
        "should be re-submitted as a new draft). The full payload replaces the existing fields and " +
        "file list, so include every file you want the suggestion to end up with, not just the ones " +
        "you're changing. Returns the unchanged SuggestionId on success.")]
    public async Task<UpdateSnippetSuggestionResult> UpdateSuggestionAsync(
        [Description("The replacement payload, including the id of the suggestion to update. Same field rules as suggest_snippet: Title and Description required, Files non-empty, file names flat (no slashes or '..').")] UpdateSnippetSuggestionInput input,
        CancellationToken ct = default)
    {
        try
        {
            await _suggestions.UpdateAsync(input.SuggestionId, input.ToDomain(), ct);
            return new UpdateSnippetSuggestionResult(
                input.SuggestionId,
                "Updated. The suggestion is still Pending at /admin/snippets/suggestions.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
