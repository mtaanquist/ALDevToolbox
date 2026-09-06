namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// Where a release pipeline's apps come from. A pipeline draws either from a build
/// pipeline the toolbox compiles itself, or from the Releases published on one of the
/// solution's GitHub repositories - which is how a version the toolbox did not build
/// can still be deployed. See <c>.design/github-integration-phase2.md</c> (#632).
/// </summary>
public static class ReleaseArtifactSource
{
    /// <summary>Builds of the pipeline named by <c>build_pipeline_id</c>. The default.</summary>
    public const string Build = "build";

    /// <summary>GitHub Releases on the repository named by <c>github_release_repository_id</c>.</summary>
    public const string GithubRelease = "github_release";

    /// <summary>The two sources, in the order the editor offers them.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Build, GithubRelease };

    /// <summary>True when <paramref name="value"/> is one of the two stored sources.</summary>
    public static bool IsValid(string? value) => value is Build or GithubRelease;
}
