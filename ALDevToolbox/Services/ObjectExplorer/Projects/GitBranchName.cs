namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// The branch names the workbench accepts: git's own ref-name rules
/// (<c>git check-ref-format --branch</c>), over a narrower alphabet.
///
/// <para>One rule for three places, which is why it is shared: the pipeline editor
/// (a person typing the branch a pipeline watches), the clone that checks that
/// branch out, and the push webhook that records where a branch now points. A name
/// one of them accepted and another refused would be a pipeline that can never be
/// fresh. The alphabet is <c>A-Z a-z 0-9 . _ / -</c>, the same one the pull-request
/// webhook holds a head branch to, so a name never needs quoting on a git command
/// line; within it, git's structural rules apply (no <c>..</c>, no leading dash,
/// no component starting with a dot, no <c>.lock</c> ending, and so on). See
/// <c>.design/github-integration-phase2.md</c>, "Branch watching" (#963).</para>
/// </summary>
public static class GitBranchName
{
    /// <summary>Longest name accepted; the column is sized to match.</summary>
    public const int MaxLength = 255;

    /// <summary>
    /// The HTML <c>pattern=</c> that mirrors <see cref="IsValid"/> in the browser.
    /// Written for the <c>v</c> flag browsers compile <c>pattern</c> with, so
    /// <c>/</c> and <c>-</c> are escaped inside the character class. A test runs it
    /// against the same names as <see cref="IsValid"/> so the two cannot drift.
    /// </summary>
    public const string HtmlPattern =
        @"(?!-)(?!\.)(?!\/)(?!.*\.\.)(?!.*\/\/)(?!.*\/\.)(?!.*\/$)(?!.*\.$)(?!.*\.lock$)(?!.*\.lock\/)[A-Za-z0-9._\/\-]+";

    /// <summary>True when <paramref name="name"/> is a branch name git and the workbench both accept.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;
        // git would read a leading dash as an option.
        if (name[0] == '-') return false;
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-')) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.EndsWith('.')) return false;

        // Per component: not empty (so no leading, trailing or doubled slash), not
        // starting with a dot, not ending in ".lock".
        foreach (var component in name.Split('/'))
        {
            if (component.Length == 0) return false;
            if (component[0] == '.') return false;
            if (component.EndsWith(".lock", StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
