namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Turns GitHub's permission names into the sentence an admin can act on.
///
/// <para>GitHub reports an installation's grants as machine names paired with
/// <c>read</c> or <c>write</c> (<c>administration: write</c>). Those names mean
/// nothing to a consultant who was told to "connect our GitHub", and CLAUDE.md
/// bans surfacing them raw, so the Repositories tab renders these strings
/// instead. An unmapped name still renders — a future permission should show up
/// as something readable rather than disappear.</para>
/// </summary>
public static class GitHubPermissionLabels
{
    private static readonly IReadOnlyDictionary<string, (string Read, string Write)> Known =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["administration"] = ("See repository settings", "Create new repositories"),
            ["contents"] = ("Read files in repositories", "Read and write files, and publish releases, in repositories"),
            ["metadata"] = ("See repository names and descriptions", "See repository names and descriptions"),
            ["members"] = ("See who is in the organisation", "Manage who is in the organisation"),
            ["pull_requests"] = ("Read pull requests", "Open and update pull requests"),
            ["issues"] = ("Read issues", "Open and update issues"),
            ["workflows"] = ("Read workflow files", "Write workflow files"),
        };

    /// <summary>The plain-words sentence for one granted permission.</summary>
    public static string Describe(string name, string level)
    {
        var isWrite = string.Equals(level, "write", StringComparison.OrdinalIgnoreCase);
        if (Known.TryGetValue(name, out var pair))
        {
            return isWrite ? pair.Write : pair.Read;
        }
        var readable = name.Replace('_', ' ').Replace('-', ' ');
        return isWrite ? $"Read and change {readable}" : $"Read {readable}";
    }
}
