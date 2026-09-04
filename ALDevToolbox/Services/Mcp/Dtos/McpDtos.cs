using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Mcp.Dtos;

/// <summary>
/// Input shape mirroring <see cref="ProjectPlan"/> for the
/// <c>generate_workspace</c> tool. Lives outside the domain layer so we
/// don't leak EF-coupled records to the MCP serialiser; <see cref="ToDomain"/>
/// is the one-liner mapping.
/// </summary>
public sealed record ProjectPlanInput(
    string TemplateKey,
    string WorkspaceName,
    string ExtensionPrefix,
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples = true,
    IReadOnlyList<string>? SelectedExtensionPaths = null,
    IReadOnlyList<string>? SelectedModuleKeys = null)
{
    public ProjectPlan ToDomain() => new(
        TemplateKey,
        WorkspaceName,
        ExtensionPrefix,
        Brief,
        Description,
        ApplicationVersion,
        RuntimeVersion,
        CoreIdRangeFrom,
        CoreIdRangeTo,
        IncludeExamples,
        SelectedExtensionPaths ?? Array.Empty<string>(),
        SelectedModuleKeys ?? Array.Empty<string>());
}

/// <summary>Mirror of <see cref="StandaloneExtensionPlan"/> for the MCP boundary.</summary>
public sealed record StandaloneExtensionPlanInput(
    string TemplateKey,
    string ExtensionName,
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int IdRangeFrom,
    int IdRangeTo,
    string Publisher,
    bool IncludeExamples = true,
    IReadOnlyList<DependencyEntryInput>? Dependencies = null)
{
    public StandaloneExtensionPlan ToDomain() => new(
        TemplateKey,
        ExtensionName,
        Brief,
        Description,
        ApplicationVersion,
        RuntimeVersion,
        IdRangeFrom,
        IdRangeTo,
        IncludeExamples,
        Publisher,
        Dependencies?.Select(d => d.ToDomain()).ToList() ?? new List<DependencyEntry>());
}

public sealed record DependencyEntryInput(string DepId, string DepName, string DepPublisher, string DepVersion)
{
    public DependencyEntry ToDomain() => new(DepId, DepName, DepPublisher, DepVersion);
}

/// <summary>
/// What a <c>generate_*</c> tool returns. The ZIP is inlined as base64 so
/// the agent has the bytes in hand without a follow-up download fetch.
/// </summary>
/// <param name="AddedToRepository">
/// Set only when the caller asked for the result to be added to a GitHub
/// repository (issue #623). The ZIP alongside it is the same one the pull
/// request carries, so an agent can hand either to the user.
/// </param>
/// <param name="CreatedRepository">
/// Set only when the caller asked for a new repository to be created for the
/// result (issue #622). As above, the ZIP alongside it is the one that was
/// committed, not a second generation with different extension GUIDs.
/// </param>
public sealed record WorkspaceResult(
    string FileName,
    string ContentBase64,
    int SizeBytes,
    string Sha256,
    RepositoryDeliveryResult? AddedToRepository = null,
    RepositoryCreationResult? CreatedRepository = null);

/// <summary>
/// The repository a <c>generate_*</c> tool created, when it was asked to put
/// its result in a new one.
/// </summary>
public sealed record RepositoryCreationResult(
    string RepositoryFullName,
    string HtmlUrl,
    string CloneUrl,
    string DefaultBranch,
    bool IsPrivate,
    int FileCount);

/// <summary>
/// The pull request a <c>generate_*</c> tool opened, when it was asked to add
/// its result to an existing repository.
/// </summary>
public sealed record RepositoryDeliveryResult(
    string RepositoryFullName,
    string Branch,
    string BaseBranch,
    int PullRequestNumber,
    string PullRequestUrl);

/// <summary>Trimmed projection of <see cref="RuntimeTemplate"/> for tool callers.</summary>
public sealed record TemplateSummary(
    string Key,
    string Name,
    string? Description,
    string Runtime,
    bool IsDefault,
    bool Deprecated,
    int CoreIdRangeFrom,
    int CoreIdRangeTo);

public sealed record ModuleSummary(
    string Key,
    string Name,
    bool Deprecated);

public sealed record WellKnownDependencySummary(
    string DepId,
    string DepName,
    string DepPublisher,
    string DepVersion);

public sealed record RecipeSummary(
    int Id,
    string Title,
    string Description,
    string Keywords,
    string Type,
    bool Deprecated,
    int FileCount,
    string? MinimumApplicationVersionName = null,
    string? MinimumApplication = null,
    decimal? EstimatedValueHours = null);

public sealed record RecipeFileDto(string Path, string Content);

public sealed record RecipeDetail(
    int Id,
    string Title,
    string Description,
    string Keywords,
    string Type,
    bool Deprecated,
    IReadOnlyList<RecipeFileDto> Files,
    string? Instructions = null,
    string? MinimumApplicationVersionName = null,
    string? MinimumApplication = null,
    decimal? EstimatedValueHours = null);

/// <summary>
/// One file body submitted as part of a <see cref="SuggestRecipeInput"/>.
/// Distinct from the read-side <see cref="RecipeFileDto"/> (which uses a
/// single <c>Path</c> field combining folder + name) so the MCP-facing
/// field names match the domain's <c>RecipeFileInput</c>. <c>RelativePath</c>
/// is empty for files at the recipe's root.
/// </summary>
public sealed record RecipeFileInputDto(string FileName, string Content, string RelativePath = "");

/// <summary>
/// Input shape for the <c>suggest_recipe</c> tool. Mirrors
/// <see cref="ALDevToolbox.Services.RecipeSuggestionInput"/> for the MCP
/// boundary; <see cref="ToDomain"/> is the one-liner mapping. <c>Type</c>
/// is a string (<c>Snippet</c>, <c>Pattern</c>, or <c>Module</c>) so the
/// agent reads the same name humans see on the cookbook chip-row.
/// <c>GuidanceToken</c> is the short-lived signed token returned by
/// <c>get_cookbook_guidance</c>; the write tool refuses to run without
/// a valid one.
/// </summary>
public sealed record SuggestRecipeInput(
    string GuidanceToken,
    string Title,
    string Description,
    string Keywords,
    string Type,
    IReadOnlyList<RecipeFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null)
{
    public ALDevToolbox.Services.RecipeSuggestionInput ToDomain() => new(
        Title,
        Description,
        Keywords,
        ParseType(Type),
        Files
            .Select(f => new ALDevToolbox.Services.RecipeFileInput(f.FileName, f.Content, f.RelativePath))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId,
        EstimatedValueHours);

    internal static ALDevToolbox.Domain.ValueObjects.RecipeType ParseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ALDevToolbox.Domain.ValueObjects.RecipeType.Snippet;
        return Enum.TryParse<ALDevToolbox.Domain.ValueObjects.RecipeType>(raw.Trim(), ignoreCase: true, out var t)
            ? t
            : throw new ModelContextProtocol.McpException(
                $"Unknown recipe Type '{raw}'. Use one of: Snippet, Pattern, Module.");
    }
}

/// <summary>What <c>suggest_recipe</c> returns: the new suggestion's id plus a confirmation pointing the agent at the admin queue.</summary>
public sealed record SuggestRecipeResult(int SuggestionId, string Message);

/// <summary>
/// Input shape for the <c>update_recipe_suggestion</c> tool. Carries the
/// id of the suggestion being edited alongside the same fields
/// <see cref="SuggestRecipeInput"/> accepts; <see cref="ToDomain"/> drops
/// the id when handing off to the service layer (which already takes the
/// id as a separate argument). Requires the same <c>GuidanceToken</c>
/// gate as <see cref="SuggestRecipeInput"/>.
/// </summary>
public sealed record UpdateRecipeSuggestionInput(
    int SuggestionId,
    string GuidanceToken,
    string Title,
    string Description,
    string Keywords,
    string Type,
    IReadOnlyList<RecipeFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null)
{
    public ALDevToolbox.Services.RecipeSuggestionInput ToDomain() => new(
        Title,
        Description,
        Keywords,
        SuggestRecipeInput.ParseType(Type),
        Files
            .Select(f => new ALDevToolbox.Services.RecipeFileInput(f.FileName, f.Content, f.RelativePath))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId,
        EstimatedValueHours);
}

/// <summary>What <c>update_recipe_suggestion</c> returns: the updated suggestion's id plus a confirmation.</summary>
public sealed record UpdateRecipeSuggestionResult(int SuggestionId, string Message);

/// <summary>
/// Input shape for the <c>update_recipe</c> tool: a full-replace payload
/// for an already-published recipe, mirroring <see cref="UpdateRecipeSuggestionInput"/>
/// plus the fields only published recipes carry. <c>Deprecated</c> is
/// nullable — omitting it keeps the recipe's current flag rather than
/// silently un-deprecating on every edit. Requires the same
/// <c>GuidanceToken</c> gate as the suggestion write tools; the caller
/// must additionally hold the Editor or Admin role.
/// </summary>
public sealed record UpdateRecipeInput(
    int RecipeId,
    string GuidanceToken,
    string Title,
    string Description,
    string Keywords,
    string Type,
    IReadOnlyList<RecipeFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null,
    decimal? EstimatedValueHours = null,
    bool? Deprecated = null)
{
    public ALDevToolbox.Services.RecipeInput ToDomain(bool currentDeprecated) => new(
        Title,
        Description,
        Keywords,
        SuggestRecipeInput.ParseType(Type),
        Deprecated ?? currentDeprecated,
        Files
            .Select(f => new ALDevToolbox.Services.RecipeFileInput(f.FileName, f.Content, f.RelativePath))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId,
        EstimatedValueHours);
}

/// <summary>What <c>update_recipe</c> returns: the unchanged recipe id plus a confirmation.</summary>
public sealed record UpdateRecipeResult(int RecipeId, string Message);

/// <summary>
/// What <c>get_cookbook_guidance</c> returns: the org's authored markdown,
/// the built-in type taxonomy (so an empty org-level guidance still gives
/// the agent something to anchor on), and a short-lived signed
/// <c>GuidanceToken</c> the write tools require. <c>GuidanceTokenExpiresInSeconds</c>
/// is the lifetime in seconds; tokens older than that are refused.
/// </summary>
public sealed record CookbookGuidance(
    string Guidance,
    IReadOnlyList<string> RecipeTypes,
    IReadOnlyDictionary<string, string> TypeDescriptions,
    string GuidanceToken,
    int GuidanceTokenExpiresInSeconds);
