namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The Git hosting provider a project repository lives on. Each provider maps to
/// a per-org Personal Access Token (stored encrypted on
/// <c>organization_settings</c>) that the project-build pipeline uses to clone.
/// Persisted as a short string discriminator (<c>azure_devops</c> / <c>github</c>)
/// on <c>oe_project_repositories</c>. See
/// <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public enum RepositoryProvider
{
    /// <summary>Azure DevOps (dev.azure.com / *.visualstudio.com).</summary>
    AzureDevOps,

    /// <summary>GitHub (github.com).</summary>
    GitHub,
}

/// <summary>
/// Maps <see cref="RepositoryProvider"/> to and from its persisted string
/// discriminator. Kept as a small helper rather than an EF value converter so
/// both the entity mapping and ad-hoc string comparisons share one source of truth.
/// </summary>
public static class RepositoryProviders
{
    /// <summary>Persisted discriminator for Azure DevOps.</summary>
    public const string AzureDevOps = "azure_devops";

    /// <summary>Persisted discriminator for GitHub.</summary>
    public const string GitHub = "github";

    /// <summary>The string discriminator stored in the database for a provider.</summary>
    public static string ToDiscriminator(this RepositoryProvider provider) => provider switch
    {
        RepositoryProvider.AzureDevOps => AzureDevOps,
        RepositoryProvider.GitHub => GitHub,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown repository provider."),
    };

    /// <summary>Parses a stored discriminator back to the enum, or null when unrecognised.</summary>
    public static RepositoryProvider? FromDiscriminator(string? value) => value switch
    {
        AzureDevOps => RepositoryProvider.AzureDevOps,
        GitHub => RepositoryProvider.GitHub,
        _ => null,
    };

    /// <summary>
    /// Parses a stored discriminator, throwing on an unrecognised value. Used by
    /// the EF value converter (expression trees can't contain a <c>throw</c>, so
    /// the guard lives in this method rather than inline).
    /// </summary>
    public static RepositoryProvider FromDiscriminatorStrict(string value) =>
        FromDiscriminator(value) ?? throw new InvalidOperationException($"Unknown repository provider discriminator '{value}'.");

    /// <summary>Human-facing provider name for UI copy and error messages.</summary>
    public static string DisplayName(this RepositoryProvider provider) => provider switch
    {
        RepositoryProvider.AzureDevOps => "Azure DevOps",
        RepositoryProvider.GitHub => "GitHub",
        _ => provider.ToString(),
    };
}
