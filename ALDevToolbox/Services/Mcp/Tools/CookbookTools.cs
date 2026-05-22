using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp.Dtos;
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
/// <see cref="GetCookbookGuidanceAsync"/>; <see cref="SuggestAsync"/>'s
/// description tells agents to call that tool first so the submission
/// matches the org's house style.
/// </summary>
[McpServerToolType]
public sealed class CookbookTools
{
    private readonly RecipeService _recipes;
    private readonly AppDbContext _db;
    private readonly RecipeSuggestionService _suggestions;
    private readonly IOrganizationContext _orgContext;

    public CookbookTools(
        RecipeService recipes,
        AppDbContext db,
        RecipeSuggestionService suggestions,
        IOrganizationContext orgContext)
    {
        _recipes = recipes;
        _db = db;
        _suggestions = suggestions;
        _orgContext = orgContext;
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
        "Returns the caller organisation's authoring conventions for the Cookbook plus the built-in description " +
        "of each recipe Type (Snippet, Pattern, Module). Call this BEFORE suggest_recipe — recipes that don't " +
        "match the org's conventions are likely to be rejected by the admin reviewer.")]
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

        return new CookbookGuidance(
            guidance,
            BuiltInTypeDescriptions.Keys.ToList(),
            BuiltInTypeDescriptions);
    }

    [McpServerTool(Name = "suggest_recipe", ReadOnly = false, Idempotent = false)]
    [Description(
        "BEFORE calling this, call get_cookbook_guidance to read the organisation's authoring conventions — " +
        "recipes that don't follow them are likely to be rejected by the admin reviewer.\n" +
        "Submits a draft recipe to the caller organisation's admin review queue at /admin/cookbook/suggestions. " +
        "The submission is NOT immediately visible from search_recipes — an admin must approve it first. " +
        "Use this when you have a useful AL pattern (e.g. extracted and generalised from a customer codebase, " +
        "with customer-specific names stripped) that the team's Cookbook is missing. " +
        "Provide a descriptive Title, a 1–3 sentence Description of when to use it, comma-separated Keywords, " +
        "a Type (Snippet / Pattern / Module — see get_cookbook_guidance for definitions), and one or more files. " +
        "Each file has a flat FileName (no slashes), an optional RelativePath for the folder it lives in (no '..', " +
        "no leading '/', empty = root), and the file body in Content. Returns the new SuggestionId.")]
    public async Task<SuggestRecipeResult> SuggestAsync(
        [Description("The draft recipe payload. Title, Description and Type are required; Files must contain at least one entry with a flat FileName, optional RelativePath, and the file body in Content.")] SuggestRecipeInput input,
        CancellationToken ct = default)
    {
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
        "Only the original submitter can edit, and only while the suggestion is still Pending — " +
        "approved or rejected suggestions are terminal and refuse the update (rejected suggestions " +
        "should be re-submitted as a new draft). The full payload replaces the existing fields and " +
        "file list, so include every file you want the suggestion to end up with, not just the ones " +
        "you're changing. Returns the unchanged SuggestionId on success.")]
    public async Task<UpdateRecipeSuggestionResult> UpdateSuggestionAsync(
        [Description("The replacement payload, including the id of the suggestion to update. Same field rules as suggest_recipe: Title, Description and Type required, Files non-empty, FileName flat (no slashes or '..'), RelativePath relative (no leading '/', no '..').")] UpdateRecipeSuggestionInput input,
        CancellationToken ct = default)
    {
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

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
