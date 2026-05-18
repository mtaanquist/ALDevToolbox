using System.ComponentModel;
using ALDevToolbox.Data;
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

    public SnippetTools(SnippetService snippets, AppDbContext db)
    {
        _snippets = snippets;
        _db = db;
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
            counts.TryGetValue(s.Id, out var c) ? c : 0)).ToList();
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
            row.Files.OrderBy(f => f.Ordering).Select(f => new SnippetFileDto(f.FileName, f.Content)).ToList());
    }
}
