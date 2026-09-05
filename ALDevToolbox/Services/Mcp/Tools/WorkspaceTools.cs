using System.ComponentModel;
using System.Security.Cryptography;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Mcp.Dtos;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools that wrap <see cref="TemplateService"/>, <see cref="ModuleService"/>,
/// <see cref="CatalogService"/>, and <see cref="GenerationService"/>.
/// Read tools return summaries; <c>generate_*</c> returns the workspace
/// ZIP inline as base64 (see <see cref="WorkspaceResult"/>).
/// </summary>
[McpServerToolType]
public sealed class WorkspaceTools
{
    private readonly TemplateService _templates;
    private readonly ModuleService _modules;
    private readonly CatalogService _catalog;
    private readonly GenerationService _generation;
    private readonly GitHubExtensionDeliveryService _delivery;
    private readonly GitHubWorkspaceRepositoryService _repositories;
    private readonly McpOptions _options;

    public WorkspaceTools(
        TemplateService templates,
        ModuleService modules,
        CatalogService catalog,
        GenerationService generation,
        GitHubExtensionDeliveryService delivery,
        GitHubWorkspaceRepositoryService repositories,
        IOptions<McpOptions> options)
    {
        _templates = templates;
        _modules = modules;
        _catalog = catalog;
        _generation = generation;
        _delivery = delivery;
        _repositories = repositories;
        _options = options.Value;
    }

    [McpServerTool(Name = "list_templates", ReadOnly = true)]
    [Description("Lists the workspace and standalone-extension templates available in the caller's organisation. Returns each template's key (use it as templateKey when generating), name, runtime version, and default core ID range.")]
    public async Task<IReadOnlyList<TemplateSummary>> ListTemplatesAsync(
        [Description("If true, include templates that have been deprecated. Defaults to false.")] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        var rows = await _templates.GetTemplatesAsync(includeDeprecated: includeDeprecated, ct);
        return rows.Select(t => new TemplateSummary(
            t.Key, t.Name, t.Description, t.Runtime, t.IsDefault, t.Deprecated,
            t.CoreIdRangeFrom, t.CoreIdRangeTo)).ToList();
    }

    [McpServerTool(Name = "list_modules", ReadOnly = true)]
    [Description("Lists the optional modules (per-org named code blocks) that can be selected when generating a workspace.")]
    public async Task<IReadOnlyList<ModuleSummary>> ListModulesAsync(
        [Description("If true, include modules that have been deprecated. Defaults to false.")] bool includeDeprecated = false,
        CancellationToken ct = default)
    {
        var rows = await _modules.GetAllForAdminAsync(includeDeleted: false, ct);
        if (!includeDeprecated)
        {
            rows = rows.Where(m => !m.Deprecated).ToList();
        }
        return rows.Select(m => new ModuleSummary(m.Key, m.Name, m.Deprecated)).ToList();
    }

    [McpServerTool(Name = "list_well_known_dependencies", ReadOnly = true)]
    [Description("Lists the catalogue of well-known BC dependencies (id, publisher, version) the caller can add when generating a standalone extension.")]
    public async Task<IReadOnlyList<WellKnownDependencySummary>> ListWellKnownDependenciesAsync(
        CancellationToken ct = default)
    {
        var rows = await _catalog.GetAllAsync(ct);
        return rows.Select(w => new WellKnownDependencySummary(
            w.DepId, w.DepName, w.DepPublisher, w.DepVersionDefault)).ToList();
    }

    [McpServerTool(Name = "generate_workspace", ReadOnly = false, Idempotent = false)]
    [Description("Generates a new BC workspace as a ZIP. Pass the template key from list_templates, the workspace details, and the Core ID range. The ZIP is returned inline as base64-encoded contentBase64 alongside its file name, size, and SHA-256. Set createRepository to also create a repository for it in your organisation's connected GitHub organisation and commit the generated files to it.")]
    public async Task<WorkspaceResult> GenerateWorkspaceAsync(
        ProjectPlanInput plan,
        [Description("Optional. A repository name (no owner - it is created in the GitHub organisation your organisation has connected, and nowhere else). When set, the repository is created and the generated files are committed to it; createdRepository in the result carries its link. You have to be a member of that GitHub organisation.")]
        string? createRepository = null,
        [Description("Whether a repository created by createRepository is private. Defaults to true.")]
        bool repositoryPrivate = true,
        CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(createRepository))
            {
                // Routed through the same service the New Workspace page uses,
                // so the organisation, the membership check and the credential
                // split are inherited rather than restated here - see "Keeping
                // MCP parity with the web UI" in PROJECT.md. The tool names a
                // repository, never an owner, so an agent cannot aim it at an
                // organisation the page would not offer.
                var created = await _repositories.CreateAsync(
                    plan.ToDomain(), createRepository!.Trim(), repositoryPrivate, ct);
                return BuildCreatedResult(created);
            }

            var archive = await _generation.GenerateWorkspaceAsync(plan.ToDomain(), ct);
            try { return BuildResult(archive); }
            finally { archive.Stream.Dispose(); }
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused the request: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "generate_extension", ReadOnly = false, Idempotent = false)]
    [Description("Generates a single standalone BC extension as a ZIP. Pass the template key from list_templates, the extension details, its app ID range, publisher, and any optional dependencies. The ZIP is returned inline as base64-encoded contentBase64. Set addToRepository to also add the extension to one of your organisation's GitHub repositories as a pull request.")]
    public async Task<WorkspaceResult> GenerateExtensionAsync(
        StandaloneExtensionPlanInput plan,
        [Description("Optional. A repository as 'owner/name'. When set, the extension is committed to a new branch there and a pull request is opened for it, in your name and never onto the repository's default branch; addedToRepository in the result carries the pull request. Only repositories in the GitHub organisation your organisation has connected, and that you can open on GitHub yourself, are accepted.")]
        string? addToRepository = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(addToRepository))
            {
                // Routed through the same service the New Extension page uses,
                // so the access rule is inherited rather than restated here -
                // see "Keeping MCP parity with the web UI" in PROJECT.md. A
                // repository the picker would not offer is refused here too.
                var delivered = await _delivery.AddExtensionAsync(
                    plan.ToDomain(), sibling: null, addToRepository!.Trim(), ct);
                return BuildDeliveredResult(delivered);
            }

            var archive = await _generation.GenerateExtensionAsync(plan.ToDomain(), sibling: null, ct: ct);
            try { return BuildResult(archive); }
            finally { archive.Stream.Dispose(); }
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused the request: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// The result for the add-to-repository path: the pull request, plus the
    /// same ZIP that went into it.
    ///
    /// <para>An oversized ZIP is reported as no ZIP rather than as a failure.
    /// The pull request already exists by this point, and swallowing that to
    /// complain about a size cap would leave the caller unaware of a change
    /// they made.</para>
    /// </summary>
    private WorkspaceResult BuildDeliveredResult(GitHubExtensionDelivery delivered)
    {
        var bytes = delivered.Archive;
        var inline = bytes.Length <= _options.MaxWorkspaceBytes;
        return new WorkspaceResult(
            FileName: delivered.ArchiveFileName,
            ContentBase64: inline ? Convert.ToBase64String(bytes) : string.Empty,
            SizeBytes: bytes.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            AddedToRepository: new RepositoryDeliveryResult(
                RepositoryFullName: delivered.Repository.FullName,
                Branch: delivered.PullRequest.HeadBranch,
                BaseBranch: delivered.Repository.DefaultBranch,
                PullRequestNumber: delivered.PullRequest.Number,
                PullRequestUrl: delivered.PullRequest.HtmlUrl));
    }

    /// <summary>
    /// The result for the create-a-repository path: the repository, plus the
    /// same ZIP whose files are in it.
    ///
    /// <para>An oversized ZIP is reported as no ZIP rather than as a failure,
    /// for the same reason as above: the repository already exists by this
    /// point, and failing over a size cap would leave the caller unaware of
    /// something they created.</para>
    /// </summary>
    private WorkspaceResult BuildCreatedResult(GitHubWorkspaceRepository created)
    {
        var bytes = created.Archive;
        var inline = bytes.Length <= _options.MaxWorkspaceBytes;
        return new WorkspaceResult(
            FileName: created.ArchiveFileName,
            ContentBase64: inline ? Convert.ToBase64String(bytes) : string.Empty,
            SizeBytes: bytes.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            CreatedRepository: new RepositoryCreationResult(
                RepositoryFullName: created.Repository.FullName,
                HtmlUrl: created.Repository.HtmlUrl,
                CloneUrl: created.Repository.CloneUrl,
                DefaultBranch: created.Repository.DefaultBranch,
                IsPrivate: created.Repository.IsPrivate,
                FileCount: created.FileCount));
    }

    private WorkspaceResult BuildResult(GeneratedArchive archive)
    {
        var bytes = archive.Stream.ToArray();
        if (bytes.Length > _options.MaxWorkspaceBytes)
        {
            throw new McpException(
                $"Generated workspace is {bytes.Length} bytes which exceeds the MCP server's MaxWorkspaceBytes ({_options.MaxWorkspaceBytes}). " +
                "Generate from the web UI for a download, or ask a SiteAdmin to raise the cap.");
        }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new WorkspaceResult(
            FileName: archive.FileName,
            ContentBase64: Convert.ToBase64String(bytes),
            SizeBytes: bytes.Length,
            Sha256: sha);
    }

    private static string FormatErrors(IReadOnlyDictionary<string, string> errors) =>
        string.Join("; ", errors.Select(kv => $"{kv.Key}: {kv.Value}"));
}
