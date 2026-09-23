using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// A symbol package stored on a solution, identified by what its own manifest
/// says rather than by its file name - a consultant names an upload whatever the
/// vendor's download was called.
/// </summary>
public sealed record StoredSymbolPackage(string FileName, Guid AppId, string Publisher, string Name, string Version);

/// <summary>
/// One app as the solution's Business Central environment last reported it installed
/// (the installed-apps mirror, not a live read). Tells the consultant which version of
/// a missing dependency the customer actually runs, which is the one to fetch.
/// </summary>
public sealed record InstalledAppFact(string EnvironmentName, bool IsProduction, Guid AppId, string Version);

/// <summary>
/// Turns the compiler's "package not found" errors for one extension into the
/// sentence its build-report row carries: which dependency, by whom, which
/// version, where the build looked, and - when the solution already has that
/// app stored at another version - that it is the wrong one.
///
/// <para>Pure so it can be tested without a compile. The compiler names a missing
/// package by publisher, name and minimum version only; the app id comes from the
/// extension's own <c>app.json</c>, matched by name. See
/// <c>.design/object-explorer-project-builds.md</c> ("Partial failure, retry, and
/// manual symbols").</para>
/// </summary>
public static class MissingDependencyReport
{
    private const string SinglePrefix = "Missing dependency: ";
    private const string ManyPrefix = "Missing dependencies: ";

    /// <summary>
    /// The row message for an extension that failed because of
    /// <paramref name="missing"/>, or null when there is nothing to name (the
    /// compile failed for some other reason).
    /// </summary>
    /// <param name="manifest">The failed extension's <c>app.json</c>.</param>
    /// <param name="missing">What the compiler said it could not find.</param>
    /// <param name="stored">The solution's stored symbol packages, read from their manifests.</param>
    /// <param name="failedSiblingIds">App ids of extensions in this same build that already failed. A dependency on one of them is not a symbols problem.</param>
    /// <param name="lookedIn">Where the build looked, in order, as the reader should see it.</param>
    /// <param name="installed">What the solution's environments last reported installed; empty when it has none.</param>
    public static string? Compose(
        AppJsonManifest manifest,
        IReadOnlyList<AlcMissingPackage> missing,
        IReadOnlyList<StoredSymbolPackage> stored,
        IReadOnlyCollection<string> failedSiblingIds,
        IReadOnlyList<string> lookedIn,
        IReadOnlyList<InstalledAppFact>? installed = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (missing is null || missing.Count == 0) return null;

        var siblings = new List<string>();
        var external = new List<(AlcMissingPackage Package, string? AppId)>();
        foreach (var package in missing)
        {
            var appId = AppIdFor(manifest, package);
            if (appId is not null && failedSiblingIds.Contains(appId, StringComparer.OrdinalIgnoreCase))
            {
                siblings.Add(package.Name);
            }
            else
            {
                external.Add((package, appId));
            }
        }

        var sentences = new List<string>();
        if (external.Count > 0)
        {
            var named = external.Select(e => Describe(e.Package, e.AppId)).ToList();
            sentences.Add((named.Count == 1 ? SinglePrefix : ManyPrefix) + string.Join("; ", named) + ".");
            if (lookedIn.Count > 0)
            {
                sentences.Add("Looked in " + JoinList(lookedIn) + ".");
            }
            foreach (var (package, appId) in external)
            {
                if (InstalledMatch(installed, appId) is { } running)
                {
                    sentences.Add($"The customer's {running.EnvironmentName} environment has {package.Name} {running.Version} installed.");
                }
                var have = StoredMatch(stored, package, appId);
                if (have is not null && IsOlder(have.Version, package.Version))
                {
                    sentences.Add($"This solution has {have.Name} {have.Version} stored; the build needs {package.Version} or later.");
                }
            }
        }
        foreach (var sibling in siblings)
        {
            sentences.Add($"Needs {sibling}, which is built by this solution but did not compile - fix that first.");
        }
        return string.Join(" ", sentences);
    }

    /// <summary>
    /// True when a build-report message names a dependency the solution has to
    /// supply - the rows that should lead to the solution's Symbols tab.
    /// </summary>
    public static bool NamesMissingDependency(string? message) =>
        message is not null
        && (message.StartsWith(SinglePrefix, StringComparison.Ordinal)
            || message.StartsWith(ManyPrefix, StringComparison.Ordinal));

    private static string Describe(AlcMissingPackage package, string? appId)
    {
        var text = $"{package.Name} by {package.Publisher}, version {package.Version} or later";
        return appId is null ? text : $"{text} (app id {appId})";
    }

    /// <summary>
    /// The app id <paramref name="manifest"/> declares for the package the compiler
    /// named. <c>app.json</c> dependencies carry no publisher, so the name is the key;
    /// Microsoft's Application and System packages are declared through the
    /// <c>application</c>/<c>platform</c> fields and have no id to report.
    /// </summary>
    private static string? AppIdFor(AppJsonManifest manifest, AlcMissingPackage package)
    {
        var dependency = manifest.Dependencies.FirstOrDefault(d =>
            string.Equals(d.Name.Trim(), package.Name, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(dependency?.Id) ? null : dependency.Id.Trim();
    }

    /// <summary>
    /// The environment to quote for an app: production first, because that is the
    /// version the customer's extensions have to run against; a sandbox otherwise.
    /// Only by app id - a name match across environments is too loose to state as fact.
    /// </summary>
    private static InstalledAppFact? InstalledMatch(IReadOnlyList<InstalledAppFact>? installed, string? appId)
    {
        if (installed is null || appId is null || !Guid.TryParse(appId, out var id)) return null;
        return installed
            .Where(i => i.AppId == id)
            .OrderByDescending(i => i.IsProduction)
            .ThenBy(i => i.EnvironmentName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static StoredSymbolPackage? StoredMatch(
        IReadOnlyList<StoredSymbolPackage> stored, AlcMissingPackage package, string? appId)
    {
        if (appId is not null && Guid.TryParse(appId, out var id))
        {
            var byId = stored.FirstOrDefault(s => s.AppId == id);
            if (byId is not null) return byId;
        }
        return stored.FirstOrDefault(s =>
            string.Equals(s.Name, package.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Publisher, package.Publisher, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A stored version the compiler would have refused. Anything that does not parse
    /// counts as older: saying "you have X stored" costs nothing when it is wrong,
    /// and staying silent about a stored package that did not help costs a guess.
    /// </summary>
    private static bool IsOlder(string storedVersion, string neededVersion) =>
        !Version.TryParse(storedVersion, out var have)
        || !Version.TryParse(neededVersion, out var need)
        || have < need;

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
