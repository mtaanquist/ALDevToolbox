using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Translation;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP surface for the Translator's cross-source translation memory — the same
/// curation an editor does in the web UI, so agents can keep the memory clean:
/// search it, vote good/bad pairs (which re-ranks suggestions), and remove bad
/// ones. All reads are org-scoped by the EF query filter; <c>remove</c> is
/// gated to the Editor / Admin role like the web page. See the Translator
/// curation feature and the "keep MCP parity" rule in <c>CLAUDE.md</c>.
/// </summary>
[McpServerToolType]
public sealed class TranslatorTools
{
    private readonly TranslationMemoryService _memory;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    public TranslatorTools(TranslationMemoryService memory, AppDbContext db, IOrganizationContext orgContext)
    {
        _memory = memory;
        _db = db;
        _orgContext = orgContext;
    }

    [McpServerTool(Name = "search_translation_memory", ReadOnly = true)]
    [Description("Searches the organisation's translation memory — the accumulated source→target pairs that back the Translator's suggestions. Returns each entry's id (for vote_translation / remove_translation), languages, source + target text, kind, provenance (origin), net vote score and hit count, ranked by score then frequency.")]
    public async Task<IReadOnlyList<TranslationMemoryHit>> SearchTranslationMemoryAsync(
        [Description("Free-text substring matched against source OR target text. Null/empty returns the top-ranked entries.")] string? query = null,
        [Description("Optional source-language filter (BCP-47, e.g. 'en-US').")] string? sourceLanguage = null,
        [Description("Optional target-language filter (BCP-47, e.g. 'da-DK').")] string? targetLanguage = null,
        [Description("Optional kind filter: caption, tooltip, label, option, instructional, other.")] string? kind = null,
        [Description("Optional substring match on the provenance/origin (e.g. 'Base Application').")] string? origin = null,
        [Description("Include soft-removed entries (default false).")] bool includeRemoved = false,
        [Description("Max results (1-100, default 25).")] int maxResults = 25,
        CancellationToken ct = default)
    {
        var res = await _memory.SearchAsync(new MemorySearchQuery(
            query, sourceLanguage, targetLanguage,
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            origin, includeRemoved, 0, Math.Clamp(maxResults, 1, 100)), ct);
        return res.Items.Select(e => new TranslationMemoryHit(
            e.Id, e.SourceLanguage, e.TargetLanguage, e.SourceText, e.TargetText,
            e.Kind, e.Origin, e.Score, e.HitCount, e.IsDeleted)).ToList();
    }

    [McpServerTool(Name = "vote_translation", ReadOnly = false, Idempotent = true)]
    [Description("Casts your up/down vote on a translation memory entry to steer suggestion ranking (a good pair floats above a more-frequent but worse one). One vote per user; voting the same direction again or 'clear' removes it. Returns the entry's new net score and your resulting vote.")]
    public async Task<TranslationVoteResult> VoteTranslationAsync(
        [Description("The memory entry id from search_translation_memory.")] long entryId,
        [Description("Vote direction: 'up', 'down', or 'clear' to remove your vote.")] string direction,
        CancellationToken ct = default)
    {
        var dir = (direction?.Trim().ToLowerInvariant()) switch
        {
            "up" => 1,
            "down" => -1,
            "clear" or "none" or "" or null => 0,
            _ => throw new McpException("direction must be 'up', 'down', or 'clear'."),
        };
        try
        {
            var r = await _memory.VoteAsync(entryId, dir, ct);
            return new TranslationVoteResult(r.EntryId, r.Score, r.MyVote);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "remove_translation", ReadOnly = false, Idempotent = true)]
    [Description("Soft-removes a bad translation memory entry so it stops being suggested. Requires the Editor or Admin role. Recoverable from the web admin page (Translation memory · 'Include removed').")]
    public async Task<TranslationRemoveResult> RemoveTranslationAsync(
        [Description("The memory entry id from search_translation_memory.")] long entryId,
        CancellationToken ct = default)
    {
        await RequireEditorAsync(ct);
        try
        {
            await _memory.DeleteAsync(entryId, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
        return new TranslationRemoveResult(entryId, true);
    }

    /// <summary>
    /// Throws unless the acting MCP user is Editor / Admin (or a SiteAdmin).
    /// Removing affects the whole org, so it mirrors the web page's role gate.
    /// </summary>
    private async Task RequireEditorAsync(CancellationToken ct)
    {
        var userId = _orgContext.CurrentUserId
            ?? throw new McpException("No user in scope; call this from an authenticated MCP session.");
        var role = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.Role)
            .FirstOrDefaultAsync(ct);
        if (!_orgContext.IsSiteAdmin && role is not (UserRole.Editor or UserRole.Admin))
        {
            throw new McpException("Removing translation memory entries requires the Editor or Admin role.");
        }
    }
}

/// <summary>One translation memory entry as returned by <c>search_translation_memory</c>.</summary>
public sealed record TranslationMemoryHit(
    long EntryId,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Kind,
    string? Origin,
    int Score,
    int HitCount,
    bool Removed);

/// <summary>Result of <c>vote_translation</c>.</summary>
public sealed record TranslationVoteResult(long EntryId, int Score, int MyVote);

/// <summary>Result of <c>remove_translation</c>.</summary>
public sealed record TranslationRemoveResult(long EntryId, bool Removed);
