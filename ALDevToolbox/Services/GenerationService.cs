using System.IO.Compression;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services;

/// <summary>
/// Builds an in-memory ZIP archive for a workspace or standalone extension.
/// </summary>
/// <remarks>
/// Stubbed during the Issue #54 unified-extensions transition. The full
/// per-extension generator (recursive folder walk, dependency resolution by
/// stable identifier, mustache <c>{{affix}}</c> substitution) is pending — see
/// <c>.design/generation-engine.md</c> for the new algorithm.
/// </remarks>
public class GenerationService
{
    private static readonly Regex WorkspaceNameRegex = new(@"^[A-Za-z][A-Za-z0-9 ]*$", RegexOptions.Compiled);
    private static readonly Regex ExtensionNameRegex = new(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    private const string PendingMessage =
        "GenerationService has not been migrated to the unified-extensions schema. " +
        "See Issue #54 follow-up; the new algorithm is described in .design/generation-engine.md.";

    private readonly AppDbContext _db;
    private readonly WorkspaceConfigService _config;
    private readonly OrganizationConfigService _orgConfig;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GenerationService> _logger;

    public GenerationService(
        AppDbContext db,
        WorkspaceConfigService config,
        OrganizationConfigService orgConfig,
        IOrganizationContext orgContext,
        ILogger<GenerationService> logger)
    {
        _db = db;
        _config = config;
        _orgConfig = orgConfig;
        _orgContext = orgContext;
        _logger = logger;
    }

    public Task<GeneratedArchive> GenerateWorkspaceAsync(ProjectPlan plan, CancellationToken ct = default)
    {
        ValidateWorkspacePlan(plan);
        throw new NotImplementedException(PendingMessage);
    }

    public Task<GeneratedArchive> GenerateExtensionAsync(
        StandaloneExtensionPlan plan,
        SiblingWorkspaceContext? sibling = null,
        CancellationToken ct = default)
    {
        ValidateExtensionPlan(plan);
        throw new NotImplementedException(PendingMessage);
    }

    private static void ValidateWorkspacePlan(ProjectPlan plan)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(plan.TemplateKey)) errors[nameof(plan.TemplateKey)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.WorkspaceName) || !WorkspaceNameRegex.IsMatch(plan.WorkspaceName))
            errors[nameof(plan.WorkspaceName)] = "Required. Letters, digits and spaces only; must start with a letter.";
        if (plan.CoreIdRangeFrom <= 0) errors[nameof(plan.CoreIdRangeFrom)] = "Must be greater than zero.";
        if (plan.CoreIdRangeTo <= plan.CoreIdRangeFrom) errors[nameof(plan.CoreIdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion))
            errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion))
            errors[nameof(plan.RuntimeVersion)] = "Required.";
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    private static void ValidateExtensionPlan(StandaloneExtensionPlan plan)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(plan.TemplateKey)) errors[nameof(plan.TemplateKey)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.ExtensionName) || !ExtensionNameRegex.IsMatch(plan.ExtensionName))
            errors[nameof(plan.ExtensionName)] = "Required. Letters and digits only, no spaces.";
        if (string.IsNullOrWhiteSpace(plan.Publisher)) errors[nameof(plan.Publisher)] = "Required.";
        if (plan.IdRangeFrom <= 0) errors[nameof(plan.IdRangeFrom)] = "Must be greater than zero.";
        if (plan.IdRangeTo <= plan.IdRangeFrom) errors[nameof(plan.IdRangeTo)] = "Must be greater than 'from'.";
        if (string.IsNullOrWhiteSpace(plan.ApplicationVersion))
            errors[nameof(plan.ApplicationVersion)] = "Required.";
        if (string.IsNullOrWhiteSpace(plan.RuntimeVersion))
            errors[nameof(plan.RuntimeVersion)] = "Required.";

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < plan.Dependencies.Count; i++)
        {
            var dep = plan.Dependencies[i];
            if (string.IsNullOrWhiteSpace(dep.DepId)) continue;
            if (!seenIds.Add(dep.DepId.Trim()))
            {
                errors[$"Dependencies[{i}].DepId"] = $"Duplicate dependency id '{dep.DepId}'.";
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }
}

/// <summary>
/// Container for a finished archive. The stream is rewound and ready to copy
/// into the HTTP response body.
/// </summary>
public record GeneratedArchive(MemoryStream Stream, string FileName);

/// <summary>
/// Sibling-extension context: tells <see cref="GenerationService.GenerateExtensionAsync"/>
/// the new extension is being generated for an existing workspace.
/// </summary>
public record SiblingWorkspaceContext(
    string WorkspaceName,
    IReadOnlyList<string> ModuleKeys,
    IReadOnlyList<string> ExistingFolders);
