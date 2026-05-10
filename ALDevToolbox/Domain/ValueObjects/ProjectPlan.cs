namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The form-collected inputs for the New Workspace flow. Drives
/// <c>GenerationService</c>'s workspace output. The shape mirrors the form on
/// <c>/projects/new</c>.
/// </summary>
public record ProjectPlan(
    string TemplateKey,
    string WorkspaceName,
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples,
    bool IncludeForNav,
    IReadOnlyList<string> SelectedModuleKeys
);

/// <summary>
/// The form-collected inputs for the New Extension flow. Produces a single
/// extension folder rather than a workspace.
/// </summary>
public record StandaloneExtensionPlan(
    string TemplateKey,
    string ExtensionName,
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int IdRangeFrom,
    int IdRangeTo,
    bool IncludeExamples,
    string Publisher,
    IReadOnlyList<DependencyEntry> Dependencies
);

/// <summary>One dependency entry in a generated <c>app.json</c>.</summary>
public record DependencyEntry(
    string DepId,
    string DepName,
    string DepPublisher,
    string DepVersion
);
