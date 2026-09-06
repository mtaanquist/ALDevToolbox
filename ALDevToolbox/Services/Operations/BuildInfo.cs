using System.Globalization;
using System.Reflection;

namespace ALDevToolbox.Services.Operations;

/// <summary>
/// The released version this build was stamped with, surfaced in the sidebar
/// footer (issue #604) so a user looking at a running instance can tell which
/// release they are on and jump to its notes.
///
/// <para>
/// The stamp is a build-time input, not something the app can discover at
/// runtime: <c>release.yml</c> passes the git tag into the Dockerfile, which
/// forwards it to <c>dotnet publish</c> as the <c>ReleaseVersion</c> /
/// <c>ReleaseDate</c> MSBuild properties, and the csproj turns those into
/// <see cref="AssemblyMetadataAttribute"/> entries. Nothing else writes those
/// attributes, so their <em>absence</em> is the unambiguous signal for "this
/// is a dev or branch build" — which is why we don't read
/// <c>InformationalVersion</c> here: unstamped, it silently defaults to
/// "1.0.0" and would render a link to a release that isn't this build.
/// </para>
/// </summary>
public sealed record BuildInfo(string Version, string ReleaseUrl, string? ReleaseDateDisplay)
{
    private const string RepositoryUrl = "https://github.com/mtaanquist/aldevtoolbox";

    /// <summary>
    /// The stamp for this build, or <c>null</c> when the build wasn't stamped
    /// (local `dotnet run`, CI branch builds). Callers render nothing in that
    /// case rather than guessing a version.
    /// </summary>
    public static BuildInfo? Current { get; } = Read(typeof(BuildInfo).Assembly);

    internal static BuildInfo? Read(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Value(string key) => metadata.FirstOrDefault(m => m.Key == key)?.Value;
        return Create(Value("ReleaseVersion"), Value("ReleaseDate"));
    }

    /// <summary>
    /// Builds the footer stamp from the raw build arguments. A missing or
    /// blank version means "not a release build"; an unparseable date is
    /// dropped (the version still renders) rather than shown raw.
    /// </summary>
    internal static BuildInfo? Create(string? version, string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var normalised = version.Trim();
        if (normalised.StartsWith('v') || normalised.StartsWith('V'))
        {
            normalised = normalised[1..];
        }

        // Drop any semver build metadata (the "+abc1234" commit suffix the
        // SDK can append) — the release tag has no such suffix, so keeping it
        // would break the link.
        var plus = normalised.IndexOf('+');
        if (plus >= 0)
        {
            normalised = normalised[..plus];
        }

        if (normalised.Length == 0)
        {
            return null;
        }

        string? dateDisplay = null;
        if (!string.IsNullOrWhiteSpace(releaseDate)
            && DateTimeOffset.TryParse(releaseDate.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            dateDisplay = parsed.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        }

        return new BuildInfo(normalised, $"{RepositoryUrl}/releases/tag/v{normalised}", dateDisplay);
    }

    /// <summary>Hover text for the version link: the release date when we have one.</summary>
    public string HoverTitle =>
        ReleaseDateDisplay is null
            ? $"Release notes for version {Version}"
            : $"Released {ReleaseDateDisplay} - release notes";
}
