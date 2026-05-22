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
public sealed record WorkspaceResult(
    string FileName,
    string ContentBase64,
    int SizeBytes,
    string Sha256);

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

public sealed record SnippetSummary(
    int Id,
    string Title,
    string Description,
    string Keywords,
    bool Deprecated,
    int FileCount,
    string? MinimumApplicationVersionName = null,
    string? MinimumApplication = null);

public sealed record SnippetFileDto(string Path, string Content);

public sealed record SnippetDetail(
    int Id,
    string Title,
    string Description,
    string Keywords,
    bool Deprecated,
    IReadOnlyList<SnippetFileDto> Files,
    string? Instructions = null,
    string? MinimumApplicationVersionName = null,
    string? MinimumApplication = null);

/// <summary>
/// One file body submitted as part of a <see cref="SuggestSnippetInput"/>.
/// Distinct from the read-side <see cref="SnippetFileDto"/> (which uses
/// <c>Path</c>) so the MCP-facing field name matches the domain's
/// <c>SnippetFileInput</c> and the agent isn't asked to pick between
/// near-synonyms.
/// </summary>
public sealed record SnippetFileInputDto(string FileName, string Content);

/// <summary>
/// Input shape for the <c>suggest_snippet</c> tool. Mirrors
/// <see cref="ALDevToolbox.Services.SnippetSuggestionInput"/> for the MCP
/// boundary; <see cref="ToDomain"/> is the one-liner mapping.
/// </summary>
public sealed record SuggestSnippetInput(
    string Title,
    string Description,
    string Keywords,
    IReadOnlyList<SnippetFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null)
{
    public ALDevToolbox.Services.SnippetSuggestionInput ToDomain() => new(
        Title,
        Description,
        Keywords,
        Files
            .Select(f => new ALDevToolbox.Services.SnippetFileInput(f.FileName, f.Content))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId);
}

/// <summary>What <c>suggest_snippet</c> returns: the new suggestion's id plus a confirmation pointing the agent at the admin queue.</summary>
public sealed record SuggestSnippetResult(int SuggestionId, string Message);

/// <summary>
/// Input shape for the <c>update_snippet_suggestion</c> tool. Carries the
/// id of the suggestion being edited alongside the same fields
/// <see cref="SuggestSnippetInput"/> accepts; <see cref="ToDomain"/> drops
/// the id when handing off to the service layer (which already takes the
/// id as a separate argument).
/// </summary>
public sealed record UpdateSnippetSuggestionInput(
    int SuggestionId,
    string Title,
    string Description,
    string Keywords,
    IReadOnlyList<SnippetFileInputDto> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null)
{
    public ALDevToolbox.Services.SnippetSuggestionInput ToDomain() => new(
        Title,
        Description,
        Keywords,
        Files
            .Select(f => new ALDevToolbox.Services.SnippetFileInput(f.FileName, f.Content))
            .ToList(),
        Instructions,
        MinimumApplicationVersionId);
}

/// <summary>What <c>update_snippet_suggestion</c> returns: the updated suggestion's id plus a confirmation.</summary>
public sealed record UpdateSnippetSuggestionResult(int SuggestionId, string Message);
