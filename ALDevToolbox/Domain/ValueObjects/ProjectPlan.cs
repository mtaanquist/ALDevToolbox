using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The form-collected inputs for the New Workspace flow. Drives
/// <c>GenerationService</c>'s workspace output. The shape mirrors the form on
/// <c>/templates/workspace</c>.
/// </summary>
public record ProjectPlan(
    string TemplateKey,
    /// <summary>The customer's name, as typed and as it should appear.</summary>
    string WorkspaceName,
    /// <summary>
    /// The customer's name abbreviated for use in extension names ("JM" for
    /// "Jørgensen Møbler"). Optional: blank means the customer name is used
    /// unchanged. Read through <see cref="EffectiveShortName"/>, never
    /// directly. See <c>.design/customer-naming.md</c>.
    /// </summary>
    string? ShortName,
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
    IReadOnlyList<string> SelectedModuleKeys,
    /// <summary>
    /// Tenant GUID captured on the New Workspace form. Pre-filled with a
    /// fresh GUID; user can paste an existing one. Surfaced through
    /// mustache substitution as <c>{{tenant_id}}</c> so always-included
    /// files and the workspace JSON can embed it.
    /// </summary>
    string TenantId = ""
)
{
    /// <summary>
    /// The short name to render, with its fallback applied: the abbreviation
    /// when one was given, the customer's name otherwise. Every reader of
    /// <see cref="ShortName"/> goes through here so the fallback is decided in
    /// one place.
    /// </summary>
    public string EffectiveShortName => CustomerNaming.ShortNameOrFallback(ShortName, WorkspaceName);
}

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
