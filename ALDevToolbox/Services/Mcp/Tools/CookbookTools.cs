using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp.Dtos;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools that wrap <see cref="RecipeService"/> and
/// <see cref="RecipeSuggestionService"/>. Lets an agent answer
/// "is there a recipe for X?"-style questions without going through the
/// web UI. All reads are org-scoped by the EF query filter.
///
/// Authoring conventions are surfaced through
/// <see cref="GetCookbookGuidanceAsync"/>, which hands the agent a
/// short-lived signed <c>GuidanceToken</c>. <see cref="SuggestAsync"/>
/// and <see cref="UpdateSuggestionAsync"/> require that token and reject
/// the call without it — the ordering of "read guidance, then submit" is
/// mandatory rather than suggested.
/// </summary>
[McpServerToolType]
public sealed class CookbookTools
{
    /// <summary>
    /// Data Protection purpose string for the cookbook-guidance token. The
    /// suffix is versioned so we can rotate the format without invalidating
    /// every Data Protection purpose ring at the same time.
    /// </summary>
    public const string GuidanceTokenProtectionPurpose = "ALDevToolbox.Cookbook.GuidanceToken.v1";

    /// <summary>
    /// How long a token issued by <see cref="GetCookbookGuidanceAsync"/>
    /// stays usable. Long enough for an agent to draft a recipe after
    /// reading the guidance; short enough to force a re-read per working
    /// session.
    /// </summary>
    public static readonly TimeSpan GuidanceTokenLifetime = TimeSpan.FromMinutes(30);

    private readonly RecipeService _recipes;
    private readonly AppDbContext _db;
    private readonly RecipeSuggestionService _suggestions;
    private readonly IOrganizationContext _orgContext;
    private readonly IDataProtector _guidanceProtector;
    private readonly TimeProvider _clock;

    public CookbookTools(
        RecipeService recipes,
        AppDbContext db,
        RecipeSuggestionService suggestions,
        IOrganizationContext orgContext,
        IDataProtectionProvider protectionProvider,
        TimeProvider clock)
    {
        _recipes = recipes;
        _db = db;
        _suggestions = suggestions;
        _orgContext = orgContext;
        _guidanceProtector = protectionProvider.CreateProtector(GuidanceTokenProtectionPurpose);
        _clock = clock;
    }

    /// <summary>
    /// Built-in copy describing what each <see cref="RecipeType"/> means.
    /// Lives in code (not in the DB) so an empty org-level
    /// <see cref="OrganizationSettings.CookbookGuidance"/> still steers an
    /// agent towards the right type bucket.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> BuiltInTypeDescriptions =
        new Dictionary<string, string>
        {
            ["Snippet"] = "A small one- or two-file pattern. Use for self-contained fragments — a single event subscriber, one tableextension, a focused helper codeunit.",
            ["Pattern"] = "A few related files that together solve one problem — for example an event subscriber + the page/table it modifies, or a setup table + page + install codeunit. Files may live under a folder structure (RelativePath).",
            ["Module"] = "A near-complete feature spanning several files and namespaces under one top-level namespace. Bigger than a Pattern; smaller than a full BC app.",
        };

    [McpServerTool(Name = "search_recipes", ReadOnly = true)]
    [Description(
        "Searches the caller's organisation Cookbook by free-text query (title, description, keywords). " +
        "Returns lightweight summaries with the recipe Type (Snippet, Pattern, or Module) — search itself " +
        "ignores type; the type field is for the agent to post-filter. Use get_recipe to fetch the files.")]
    public async Task<IReadOnlyList<RecipeSummary>> SearchAsync(
        [Description("Search text. Matches against title, description, and keywords. Pass an empty string to list everything.")] string query,
        [Description("If true, also return recipes that have been marked deprecated. Defaults to false.")] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        var rows = await _recipes.SearchAsync(query, includeDeprecated, ct);
        if (rows.Count == 0) return Array.Empty<RecipeSummary>();
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.RecipeFiles.AsNoTracking()
            .Where(f => ids.Contains(f.RecipeId))
            .GroupBy(f => f.RecipeId)
            .Select(g => new { RecipeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RecipeId, x => x.Count, ct);
        return rows.Select(s => new RecipeSummary(
            s.Id, s.Title, s.Description, s.Keywords,
            s.Type.ToString(),
            s.Deprecated,
            counts.TryGetValue(s.Id, out var c) ? c : 0,
            s.MinimumApplicationVersion?.Name,
            s.MinimumApplicationVersion?.Application)).ToList();
    }

    [McpServerTool(Name = "get_recipe", ReadOnly = true)]
    [Description("Returns the full content of a recipe (every file's path and body) by id. Each file's Path includes its folder layout (RelativePath/FileName). Use search_recipes to find candidate ids.")]
    public async Task<RecipeDetail> GetAsync(
        [Description("Recipe id from search_recipes.")] int id,
        CancellationToken ct = default)
    {
        var row = await _recipes.GetAsync(id, ct);
        if (row is null)
        {
            throw new McpException($"Recipe {id} not found, or not visible to your organisation.");
        }
        return new RecipeDetail(
            row.Id, row.Title, row.Description, row.Keywords,
            row.Type.ToString(),
            row.Deprecated,
            row.Files.OrderBy(f => f.Ordering).Select(f => new RecipeFileDto(
                string.IsNullOrEmpty(f.RelativePath) ? f.FileName : f.RelativePath + "/" + f.FileName,
                f.Content)).ToList(),
            row.Instructions,
            row.MinimumApplicationVersion?.Name,
            row.MinimumApplicationVersion?.Application);
    }

    [McpServerTool(Name = "get_cookbook_guidance", ReadOnly = true)]
    [Description(
        "Returns the caller organisation's authoring conventions for the Cookbook, the built-in description " +
        "of each recipe Type (Snippet, Pattern, Module), AND a short-lived GuidanceToken that suggest_recipe " +
        "and update_recipe_suggestion require. Calling this tool is MANDATORY before submitting a recipe — " +
        "the write tools reject the call without a valid token from a recent get_cookbook_guidance.")]
    public async Task<CookbookGuidance> GetCookbookGuidanceAsync(CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new McpException("No organisation in scope; call this from an authenticated MCP session.");

        var guidance = await _db.OrganizationSettings
            .AsNoTracking()
            .Where(s => s.OrganizationId == orgId)
            .Select(s => s.CookbookGuidance)
            .FirstOrDefaultAsync(ct)
            ?? string.Empty;

        var token = IssueGuidanceToken(orgId);
        return new CookbookGuidance(
            guidance,
            BuiltInTypeDescriptions.Keys.ToList(),
            BuiltInTypeDescriptions,
            token,
            (int)GuidanceTokenLifetime.TotalSeconds);
    }

    [McpServerTool(Name = "suggest_recipe", ReadOnly = false, Idempotent = false)]
    [Description(
        "Submits a draft recipe to the caller organisation's admin review queue at /admin/cookbook/suggestions. " +
        "MANDATORY two-step protocol: first call get_cookbook_guidance and pass the returned GuidanceToken in " +
        "this call's input. The call is rejected with a clear error if the token is missing, expired (older " +
        "than 30 minutes), or issued for a different organisation. " +
        "The submission is NOT immediately visible from search_recipes — an admin must approve it first. " +
        "Use this when you have a useful AL pattern (e.g. extracted and generalised from a customer codebase, " +
        "with customer-specific names stripped) that the team's Cookbook is missing. " +
        "Provide a descriptive Title, a 1–3 sentence Description of when to use it, comma-separated Keywords, " +
        "a Type (Snippet / Pattern / Module — see get_cookbook_guidance for definitions), and one or more files. " +
        "Each file has a flat FileName (no slashes), an optional RelativePath for the folder it lives in (no '..', " +
        "no leading '/', empty = root), and the file body in Content. Returns the new SuggestionId.")]
    public async Task<SuggestRecipeResult> SuggestAsync(
        [Description("The draft recipe payload. GuidanceToken (from get_cookbook_guidance) is required; Title, Description and Type are required; Files must contain at least one entry with a flat FileName, optional RelativePath, and the file body in Content.")] SuggestRecipeInput input,
        CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        RequireValidGuidanceToken(input.GuidanceToken, orgId);

        try
        {
            var id = await _suggestions.SubmitAsync(input.ToDomain(), ct);
            return new SuggestRecipeResult(
                id,
                "Submitted for review. An admin will approve or reject it at /admin/cookbook/suggestions.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    [McpServerTool(Name = "update_recipe_suggestion", ReadOnly = false, Idempotent = true)]
    [Description(
        "Edits a pending recipe suggestion that the caller previously submitted via suggest_recipe. " +
        "MANDATORY two-step protocol: include a fresh GuidanceToken from get_cookbook_guidance (same rules as suggest_recipe). " +
        "Only the original submitter can edit, and only while the suggestion is still Pending — " +
        "approved or rejected suggestions are terminal and refuse the update (rejected suggestions " +
        "should be re-submitted as a new draft). The full payload replaces the existing fields and " +
        "file list, so include every file you want the suggestion to end up with, not just the ones " +
        "you're changing. Returns the unchanged SuggestionId on success.")]
    public async Task<UpdateRecipeSuggestionResult> UpdateSuggestionAsync(
        [Description("The replacement payload, including the id of the suggestion to update and a GuidanceToken from get_cookbook_guidance. Same field rules as suggest_recipe: Title, Description and Type required, Files non-empty, FileName flat (no slashes or '..'), RelativePath relative (no leading '/', no '..').")] UpdateRecipeSuggestionInput input,
        CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        RequireValidGuidanceToken(input.GuidanceToken, orgId);

        try
        {
            await _suggestions.UpdateAsync(input.SuggestionId, input.ToDomain(), ct);
            return new UpdateRecipeSuggestionResult(
                input.SuggestionId,
                "Updated. The suggestion is still Pending at /admin/cookbook/suggestions.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new McpException("No organisation in scope; call this from an authenticated MCP session.");

    /// <summary>
    /// Issues a token binding the caller's organisation to a deadline. The
    /// payload is <c>"orgId|expiresUnix"</c> passed through
    /// <see cref="IDataProtector"/> so the agent can't forge or alter it —
    /// the Data Protection key ring carries both HMAC and key rotation.
    /// </summary>
    internal string IssueGuidanceToken(int organizationId)
    {
        var expiresAt = _clock.GetUtcNow().Add(GuidanceTokenLifetime);
        var payload = organizationId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|"
            + expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return _guidanceProtector.Protect(payload);
    }

    /// <summary>
    /// Verifies a token from <see cref="IssueGuidanceToken"/>: not tampered,
    /// not expired, and issued to the same organisation that's now writing.
    /// Throws <see cref="McpException"/> with a message that names the
    /// recovery action so the agent knows how to fix the next call.
    /// </summary>
    private void RequireValidGuidanceToken(string? token, int callerOrgId)
    {
        const string fixupHint = "Call get_cookbook_guidance and pass the returned GuidanceToken to this tool.";

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new McpException(
                "Missing GuidanceToken. " + fixupHint);
        }

        string payload;
        try
        {
            payload = _guidanceProtector.Unprotect(token);
        }
        catch
        {
            // Tampered, truncated, base64-mangled, or signed with a key ring
            // that's no longer trusted (e.g. /app-keys volume reset).
            throw new McpException(
                "GuidanceToken is invalid or no longer accepted. " + fixupHint);
        }

        var parts = payload.Split('|', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var tokenOrgId)
            || !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var expiresUnix))
        {
            throw new McpException(
                "GuidanceToken payload is malformed. " + fixupHint);
        }

        if (tokenOrgId != callerOrgId)
        {
            // The token signs the org id, so this only fires if someone
            // pasted a token issued in a different organisation. Refuse
            // with the same generic recovery message so the surface stays
            // narrow.
            throw new McpException(
                "GuidanceToken is for a different organisation. " + fixupHint);
        }

        var nowUnix = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (nowUnix >= expiresUnix)
        {
            throw new McpException(
                "GuidanceToken has expired (tokens are valid for "
                + (int)GuidanceTokenLifetime.TotalMinutes
                + " minutes). " + fixupHint);
        }
    }

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
