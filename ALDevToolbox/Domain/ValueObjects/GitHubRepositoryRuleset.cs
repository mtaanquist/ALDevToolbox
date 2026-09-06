using System.Text.Json.Serialization;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The branch protection an organisation wants on the default branch of every
/// repository the toolbox creates for it (issue #628), stored as
/// <c>organization_settings.github_repository_ruleset_json</c> and applied as a
/// GitHub *repository ruleset* right after the standards commit.
///
/// <para>A deliberately small subset of what GitHub's ruleset API accepts: the
/// four rules a BC team actually asks for on a customer repository. Anything
/// beyond them is edited on GitHub, where the full surface lives - a settings
/// page that reproduced GitHub's own would be out of date the week after it
/// shipped. See <c>.design/github-integration-phase2.md</c>.</para>
///
/// <para>Values are plain, not mustache: a ruleset is the same in every
/// repository by definition.</para>
/// </summary>
public class GitHubRepositoryRuleset
{
    /// <summary>Every push to the default branch has to go through a pull request.</summary>
    [JsonPropertyName("require_pull_request")]
    public bool RequirePullRequest { get; set; }

    /// <summary>
    /// How many approving reviews a pull request needs. Only meaningful while
    /// <see cref="RequirePullRequest"/> is on; zero means "a pull request, but
    /// nobody has to approve it".
    /// </summary>
    [JsonPropertyName("required_approvals")]
    public int RequiredApprovals { get; set; }

    /// <summary>No merge commits on the default branch.</summary>
    [JsonPropertyName("require_linear_history")]
    public bool RequireLinearHistory { get; set; }

    /// <summary>Nobody may rewrite the default branch's history.</summary>
    [JsonPropertyName("block_force_pushes")]
    public bool BlockForcePushes { get; set; }

    /// <summary>
    /// Names of the checks that have to pass first, as GitHub reports them on a
    /// commit (the job name, typically). Empty means no check is required.
    /// </summary>
    [JsonPropertyName("required_status_checks")]
    public List<string> RequiredStatusChecks { get; set; } = new();

    /// <summary>
    /// True when the ruleset would ask GitHub for nothing at all. Stored rather
    /// than deleted rows can end up here (an admin who unticks everything), and
    /// posting an empty ruleset would leave a rule named after us that enforces
    /// nothing - worse than not creating one.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        !RequirePullRequest
        && !RequireLinearHistory
        && !BlockForcePushes
        && RequiredStatusChecks.Count == 0;
}
