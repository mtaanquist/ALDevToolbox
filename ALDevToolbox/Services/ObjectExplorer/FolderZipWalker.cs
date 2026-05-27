using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Walks a single ZIP that wraps a DVD-style <c>applications/</c> folder tree
/// and returns one <see cref="FolderZipEntry"/> per <c>.app</c> file, paired
/// with the sibling <c>.Source.zip</c> when one is present in the same
/// directory. Used by the admin bulk-upload endpoint so admins can drop the
/// whole 100+ app DVD as one upload rather than picking each pair by hand.
///
/// Flag inference follows the documented Microsoft conventions:
/// <list type="bullet">
///   <item><c>IsTest</c> — any ancestor directory name matches one of
///         <c>Test</c> / <c>Tests</c> / <c>Test Library</c> / <c>TestLibraries</c>
///         / <c>TestFramework</c> (case-insensitive).</item>
///   <item><c>IsInternal</c> — the <c>.app</c> filename contains the
///         <c>_Exclude_</c> marker Microsoft uses for platform-internal apps
///         that ship on the DVD but aren't part of the public product.</item>
///   <item><c>IsLanguagePack</c> — the <c>.app</c> filename matches the
///         <c>&lt;Language&gt; language (&lt;Region&gt;)</c> pattern
///         Microsoft's translation-only modules use.</item>
/// </list>
/// </summary>
public static class FolderZipWalker
{
    private static readonly HashSet<string> TestFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test",
        "Tests",
        "Test Library",
        "Test Libraries",
        "TestLibraries",
        "TestFramework",
    };

    // Matches "<X> language (<Y>)" at the *end* of an .app filename stem.
    // The Microsoft DVD uses this exact shape for every translation-only
    // app (e.g. "Microsoft_Danish language (Denmark).app"). The leading
    // publisher prefix is irrelevant — we anchor on the right side.
    private static readonly Regex LanguagePackPattern = new(
        @"\blanguage\s*\([^)]+\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Walks every <c>.app</c> in the archive (the uploaded-folder-ZIP case),
    /// or only those an optional <paramref name="includeApp"/> predicate
    /// accepts. The predicate receives each <c>.app</c> entry's full path; when
    /// <see langword="null"/> every <c>.app</c> is included.
    /// </summary>
    public static IReadOnlyList<FolderZipEntry> Walk(ZipArchive archive, Func<string, bool>? includeApp = null)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Index source zips by their containing directory + basename stem so
        // we can pair them up in one pass.
        var sourceZips = new Dictionary<(string Directory, string Stem), ZipArchiveEntry>(
            new DirectoryStemComparer());
        foreach (var e in archive.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;
            if (!e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = StripSourceSuffix(Path.GetFileNameWithoutExtension(e.Name));
            sourceZips[(GetDirectory(e.FullName), stem)] = e;
        }

        var result = new List<FolderZipEntry>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            if (includeApp is not null && !includeApp(entry.FullName)) continue;

            var dir = GetDirectory(entry.FullName);
            var appStem = StripPublisherPrefix(Path.GetFileNameWithoutExtension(entry.Name));

            ZipArchiveEntry? pairedSource = null;
            // Try a few stem variants so common filename conventions on the
            // DVD all pair correctly:
            //   Microsoft_Base Application.app ↔ Base Application.Source.zip
            //   _Exclude_Foo.app               ↔ _Exclude_Foo.Source.zip
            foreach (var candidate in PairingCandidates(entry.Name, appStem))
            {
                if (sourceZips.TryGetValue((dir, candidate), out var src))
                {
                    pairedSource = src;
                    break;
                }
            }

            result.Add(new FolderZipEntry(
                FileName: entry.Name,
                AppEntry: entry,
                SourceZipEntry: pairedSource,
                IsTest: HasTestAncestor(entry.FullName),
                IsInternal: entry.Name.Contains("_Exclude_", StringComparison.OrdinalIgnoreCase),
                IsLanguagePack: LanguagePackPattern.IsMatch(Path.GetFileNameWithoutExtension(entry.Name))));
        }
        return result;
    }

    /// <summary>
    /// Walks a full BC DVD ZIP, keeping only the apps that matter for the
    /// Object Explorer: every <c>.app</c> under an <c>Applications/</c> folder
    /// plus the platform symbols app named exactly <c>System.app</c> (it lives
    /// under <c>ModernDev/PFiles/Microsoft Dynamics NAV/&lt;ver&gt;/AL
    /// Development Environment/</c>). Test extensions and their source are
    /// dropped entirely — any <c>.app</c> under a <c>Test*</c> folder is
    /// skipped, unlike <see cref="Walk(ZipArchive, Func{string, bool})"/> which
    /// imports them flagged.
    ///
    /// The match deliberately anchors only on the long-standing
    /// <c>Applications/</c> segment and the literal <c>System.app</c> filename,
    /// not on the version segment, so a BC version bump needs no code change.
    /// If Microsoft reorganises the DVD so nothing matches, this returns an
    /// empty list and the caller surfaces a clear error.
    /// </summary>
    public static IReadOnlyList<FolderZipEntry> WalkDvd(ZipArchive archive) =>
        Walk(archive, IsWantedDvdApp);

    private static bool IsWantedDvdApp(string fullName)
    {
        if (HasTestAncestor(fullName)) return false;

        var segments = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (string.Equals(segment, "Applications", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var name = segments.Length > 0 ? segments[^1] : fullName;
        return string.Equals(name, "System.app", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> PairingCandidates(string appName, string stripped)
    {
        var bareStem = Path.GetFileNameWithoutExtension(appName);
        // 1. Exact bare stem (handles "_Exclude_Foo.app" → "_Exclude_Foo.Source.zip").
        yield return bareStem;
        // 2. With publisher prefix stripped (handles "Microsoft_Base Application" → "Base Application").
        if (!string.Equals(stripped, bareStem, StringComparison.Ordinal))
        {
            yield return stripped;
        }
    }

    private static string GetDirectory(string fullName)
    {
        var lastSlash = fullName.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : fullName[..lastSlash];
    }

    private static string StripPublisherPrefix(string stem)
    {
        // Microsoft's DVD wraps each app filename with a "Microsoft_" prefix.
        // We strip the first <Publisher>_ segment when present so the stem
        // aligns with the paired source zip naming.
        var underscore = stem.IndexOf('_');
        if (underscore <= 0) return stem;
        return stem[(underscore + 1)..];
    }

    private static string StripSourceSuffix(string stem)
    {
        // "<Name>.Source" → "<Name>". GetFileNameWithoutExtension already
        // dropped the ".zip" extension.
        if (stem.EndsWith(".Source", StringComparison.OrdinalIgnoreCase))
        {
            return stem[..^".Source".Length];
        }
        return stem;
    }

    private static bool HasTestAncestor(string fullName)
    {
        // Walk every path segment; the .app file's own filename is the last
        // segment so it's harmless to scan.
        foreach (var segment in fullName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TestFolderNames.Contains(segment)) return true;
        }
        return false;
    }

    private sealed class DirectoryStemComparer : IEqualityComparer<(string Directory, string Stem)>
    {
        public bool Equals((string Directory, string Stem) x, (string Directory, string Stem) y) =>
            string.Equals(x.Directory, y.Directory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Stem, y.Stem, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Directory, string Stem) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Directory),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Stem));
    }
}

/// <summary>
/// One <c>.app</c> entry inside the folder-style upload ZIP, paired with its
/// sibling <c>.Source.zip</c> when found, plus the flag tuple inferred from
/// the surrounding folder + filename conventions.
/// </summary>
public sealed record FolderZipEntry(
    string FileName,
    ZipArchiveEntry AppEntry,
    ZipArchiveEntry? SourceZipEntry,
    bool IsTest,
    bool IsInternal,
    bool IsLanguagePack);
