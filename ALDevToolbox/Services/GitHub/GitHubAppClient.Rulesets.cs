using System.Net;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Repository rulesets: the branch rules an organisation wants on every
/// repository the toolbox creates for it (issue #628).
///
/// <para>A ruleset is the modern replacement for a branch protection rule, and
/// it is what this uses because it can be aimed at <c>~DEFAULT_BRANCH</c> - a
/// symbolic name that keeps meaning the right branch whatever the repository
/// renames it to - rather than at a branch name we would have to guess.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed partial class GitHubAppClient
{
    /// <summary>
    /// The name the toolbox's ruleset carries on GitHub. Fixed rather than
    /// derived from the organisation, so somebody reading a repository's
    /// settings can see at a glance which rules were not written by hand.
    /// </summary>
    public const string RulesetName = "AL Dev Toolbox repository standards";

    /// <summary>
    /// Creates the organisation's ruleset on <paramref name="owner"/>/<paramref name="repo"/>,
    /// enforced on the default branch, and returns its id.
    ///
    /// <para>Needs the installation's <c>administration: write</c> grant. When
    /// that is missing GitHub answers 403, which the caller turns into a warning
    /// on an already-created repository rather than a failure - by the time this
    /// runs the repository exists and is committed.</para>
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused to create the ruleset.</exception>
    public async Task<long> CreateRepositoryRulesetAsync(
        string installationToken, string owner, string repo, GitHubRepositoryRuleset ruleset,
        CancellationToken ct = default)
    {
        var rules = new List<object>();
        if (ruleset.RequirePullRequest)
        {
            // GitHub wants every parameter of the pull_request rule, not only
            // the one we vary; the four booleans are its own defaults said out
            // loud rather than choices of ours.
            rules.Add(new
            {
                type = "pull_request",
                parameters = new
                {
                    required_approving_review_count = Math.Max(0, ruleset.RequiredApprovals),
                    dismiss_stale_reviews_on_push = false,
                    require_code_owner_review = false,
                    require_last_push_approval = false,
                    required_review_thread_resolution = false,
                },
            });
        }
        if (ruleset.RequireLinearHistory)
        {
            rules.Add(new { type = "required_linear_history" });
        }
        if (ruleset.BlockForcePushes)
        {
            // GitHub's name for "no force pushes": the rule forbids a
            // non-fast-forward update of the ref.
            rules.Add(new { type = "non_fast_forward" });
        }
        if (ruleset.RequiredStatusChecks.Count > 0)
        {
            rules.Add(new
            {
                type = "required_status_checks",
                parameters = new
                {
                    required_status_checks = ruleset.RequiredStatusChecks
                        .Select(c => new { context = c })
                        .ToList(),
                    strict_required_status_checks_policy = false,
                },
            });
        }

        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/rulesets", installationToken,
            new
            {
                name = RulesetName,
                target = "branch",
                enforcement = "active",
                conditions = new
                {
                    ref_name = new { include = new[] { "~DEFAULT_BRANCH" }, exclude = Array.Empty<string>() },
                },
                rules,
            });
        using var document = await SendAsync(request, ct);
        var id = document.RootElement.TryGetProperty("id", out var element)
            && element.TryGetInt64(out var value)
                ? value
                : throw new GitHubApiException(
                    HttpStatusCode.BadGateway, "GitHub did not say which ruleset it created.");

        _logger.LogInformation(
            "Created repository ruleset {RulesetId} on {Owner}/{Repo} with {RuleCount} rule(s).",
            id, owner, repo, rules.Count);
        return id;
    }
}
