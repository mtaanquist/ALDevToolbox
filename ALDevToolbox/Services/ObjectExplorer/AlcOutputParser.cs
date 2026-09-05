using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// One diagnostic the AL compiler printed, before it becomes a
/// <c>ProjectBuildDiagnostic</c> row. <see cref="Path"/> is still whatever the
/// compiler said - absolute, and with whatever slashes the host uses -
/// <see cref="AlcOutputParser.MakeRelative"/> is what turns it into something
/// GitHub can draw an annotation against.
/// </summary>
public sealed record AlcDiagnostic(
    string Path,
    int Line,
    int Column,
    string Severity,
    string Code,
    string Message);

/// <summary>
/// Reads <c>alc</c>'s console output into structured diagnostics.
///
/// <para>The compiler prints one diagnostic per line in the long-standing
/// Microsoft build format:</para>
/// <code>
/// C:\src\App\Pages\MyPage.al(12,5): error AL0118: The name 'Foo' does not exist
/// /tmp/oe-build-x/repo-0/App/Codeunits/My.al(4,1): warning AA0005: Braces are redundant [/tmp/.../app.json]
/// </code>
/// <para>Everything after the message is the project the diagnostic belongs to,
/// which the compiler appends in brackets on some builds and omits on others; it
/// repeats what we already know from the file path, so it is dropped. Lines that
/// do not match the shape at all - the compiler's banner, its "Compilation ended"
/// summary, an MSBuild-style progress line - are simply not diagnostics and are
/// skipped rather than guessed at.</para>
///
/// <para>Static and free of I/O so it can be tested against real compiler output
/// without a build. See <c>.design/github-integration-phase2.md</c> (#627).</para>
/// </summary>
public static class AlcOutputParser
{
    /// <summary>
    /// The diagnostic line shape. Deliberately anchored at both ends: a
    /// half-matching line is much more likely to be prose that happens to contain
    /// a colon than a diagnostic the compiler mangled.
    ///
    /// <para>The path group is lazy up to the <c>(line,col)</c> so a Windows
    /// drive letter's own colon does not end it, and the code group allows the
    /// letters-then-digits shape every AL analyser uses (<c>AL0118</c>,
    /// <c>AA0005</c>, <c>AS0011</c>) while staying optional, because the compiler
    /// occasionally emits a message with no code.</para>
    /// </summary>
    private static readonly Regex DiagnosticLine = new(
        @"^\s*(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning|info)\s*(?<code>[A-Za-z]{2,}\d{3,})?\s*:\s*(?<message>.*?)\s*(?:\[[^\[\]]*\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeout: TimeSpan.FromSeconds(1));

    /// <summary>
    /// Every diagnostic in <paramref name="output"/>, in the order the compiler
    /// printed them. Duplicate lines are kept: the compiler repeats a diagnostic
    /// when the same file is compiled twice in one build, and silently collapsing
    /// them would make the count on the build page disagree with the log.
    /// </summary>
    public static IReadOnlyList<AlcDiagnostic> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var found = new List<AlcDiagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            Match match;
            try
            {
                match = DiagnosticLine.Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological line is not worth failing a build over; it just
                // is not a diagnostic as far as this pass is concerned.
                continue;
            }
            if (!match.Success) continue;

            found.Add(new AlcDiagnostic(
                Path: match.Groups["path"].Value.Trim(),
                Line: int.TryParse(match.Groups["line"].Value, out var l) ? l : 0,
                Column: int.TryParse(match.Groups["col"].Value, out var c) ? c : 0,
                Severity: match.Groups["severity"].Value.ToLowerInvariant(),
                Code: match.Groups["code"].Success ? match.Groups["code"].Value.ToUpperInvariant() : string.Empty,
                Message: match.Groups["message"].Value.Trim()));
        }
        return found;
    }

    /// <summary>
    /// Rewrites an absolute compiler path as a repository-relative one with
    /// forward slashes, given the clone directory that repository was checked out
    /// into.
    ///
    /// <para>This is what makes a diagnostic usable as a check-run annotation:
    /// GitHub matches the path against the pull request's own files, and
    /// <c>/tmp/oe-build-8f2.../repo-0/App/My.al</c> matches nothing. A path that
    /// is not under <paramref name="cloneRoot"/> is returned normalised but
    /// otherwise unchanged - it is a diagnostic about a symbol package or a
    /// generated file, and inventing a repository-relative name for it would be
    /// worse than leaving it alone.</para>
    /// </summary>
    public static string MakeRelative(string path, string? cloneRoot)
    {
        var normalised = path.Replace('\\', '/').Trim();
        if (string.IsNullOrEmpty(cloneRoot)) return normalised;

        var root = cloneRoot.Replace('\\', '/').TrimEnd('/');
        if (root.Length == 0) return normalised;

        if (normalised.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalised[(root.Length + 1)..];
        }
        return normalised;
    }
}
