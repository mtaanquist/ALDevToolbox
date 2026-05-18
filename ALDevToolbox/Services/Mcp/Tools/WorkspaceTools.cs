using System.ComponentModel;
using System.Security.Cryptography;
using ALDevToolbox.Domain.ValueObjects;
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
    private readonly McpOptions _options;

    public WorkspaceTools(
        TemplateService templates,
        ModuleService modules,
        CatalogService catalog,
        GenerationService generation,
        IOptions<McpOptions> options)
    {
        _templates = templates;
        _modules = modules;
        _catalog = catalog;
        _generation = generation;
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
    [Description("Generates a new BC workspace as a ZIP. Pass the template key from list_templates, the workspace details, and the Core ID range. The ZIP is returned inline as base64-encoded contentBase64 alongside its file name, size, and SHA-256.")]
    public async Task<WorkspaceResult> GenerateWorkspaceAsync(
        ProjectPlanInput plan,
        CancellationToken ct = default)
    {
        try
        {
            var archive = await _generation.GenerateWorkspaceAsync(plan.ToDomain(), ct);
            try { return BuildResult(archive); }
            finally { archive.Stream.Dispose(); }
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
    }

    [McpServerTool(Name = "generate_extension", ReadOnly = false, Idempotent = false)]
    [Description("Generates a single standalone BC extension as a ZIP. Pass the template key from list_templates, the extension details, its app ID range, publisher, and any optional dependencies. The ZIP is returned inline as base64-encoded contentBase64.")]
    public async Task<WorkspaceResult> GenerateExtensionAsync(
        StandaloneExtensionPlanInput plan,
        CancellationToken ct = default)
    {
        try
        {
            var archive = await _generation.GenerateExtensionAsync(plan.ToDomain(), sibling: null, ct);
            try { return BuildResult(archive); }
            finally { archive.Stream.Dispose(); }
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Validation failed: " + FormatErrors(ex.Errors));
        }
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
