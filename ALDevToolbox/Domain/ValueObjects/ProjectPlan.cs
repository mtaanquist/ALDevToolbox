namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The form-collected inputs for the New Workspace flow. Drives
/// <c>GenerationService</c>'s workspace output. The shape mirrors the form on
/// <c>/projects/new</c>.
/// </summary>
public record ProjectPlan(
    string TemplateKey,
    string WorkspaceName,
    /// <summary>
    /// The per-workspace short identifier substituted for <c>{{extension_prefix}}</c>
    /// (e.g. <c>"CRO"</c> renders as <c>"CRO Core"</c>). Pre-filled from
    /// <c>defaults.extension_prefix</c>; user can override on the form.
    /// Distinct from <c>defaults.affix</c>, which is the AL object-name affix.
    /// </summary>
    string ExtensionPrefix,
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples,
    /// <summary>
    /// Paths of optional template-declared extensions the user ticked on the
    /// form (template entries with <c>required = false</c>). Required extensions
    /// are always emitted regardless of this list.
    /// </summary>
    IReadOnlyList<string> SelectedExtensionPaths,
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
