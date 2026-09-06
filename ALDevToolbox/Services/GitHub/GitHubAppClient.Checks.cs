using System.Net;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One inline annotation on a check run: the line of the file GitHub draws the
/// message against in the pull request's Files tab.
///
/// <para><see cref="Path"/> is relative to the repository root - an absolute
/// path from the build machine matches no file GitHub knows about, and GitHub
/// silently drops the annotation rather than saying so. <see cref="Level"/> is
/// one of <c>failure</c>, <c>warning</c> or <c>notice</c>.</para>
/// </summary>
public sealed record GitHubCheckAnnotation(
    string Path,
    int StartLine,
    int EndLine,
    string Level,
    string Message,
    string? Title);

/// <summary>The annotation levels GitHub accepts.</summary>
public static class GitHubCheckAnnotationLevel
{
    public const string Failure = "failure";
    public const string Warning = "warning";
    public const string Notice = "notice";
}

/// <summary>The conclusions the compile gate uses. GitHub defines more; these are the three that mean something here.</summary>
public static class GitHubCheckConclusion
{
    /// <summary>Everything the build was asked to compile did.</summary>
    public const string Success = "success";

    /// <summary>At least one extension failed to compile, or the build failed as a whole.</summary>
    public const string Failure = "failure";

    /// <summary>The build could not run at all, so nothing was learned about the code. Not a red X.</summary>
    public const string Neutral = "neutral";
}

/// <summary>
/// The Checks API half of <see cref="GitHubAppClient"/> (issue #627).
///
/// <para>Check runs are the one thing in the toolbox that <em>must</em> act as
/// the App rather than as a person: GitHub attributes a check run to whoever
/// created it, a webhook build has no user behind it, and only an App may write
/// to the Checks API at all. So every method here takes an installation token,
/// and the <c>checks: write</c> permission is what an organisation grants to turn
/// the gate on. See <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed partial class GitHubAppClient
{
    /// <summary>
    /// GitHub accepts at most fifty annotations per request, and answers 422 for
    /// the fifty-first rather than truncating - so an update carrying more is sent
    /// as several calls.
    /// </summary>
    public const int MaxAnnotationsPerRequest = 50;

    /// <summary>
    /// Opens a check run against <paramref name="headSha"/> and returns its id.
    /// Created <c>in_progress</c> by the caller so the pull request shows the build
    /// running from the moment it is queued rather than appearing only once it
    /// finishes.
    /// </summary>
    /// <param name="externalId">
    /// Our own identifier for the run, echoed back by GitHub. The build id goes
    /// here so a check run seen on GitHub can be traced to a build without a
    /// lookup table.
    /// </param>
    public async Task<long> CreateCheckRunAsync(
        string installationToken,
        string owner,
        string repo,
        string name,
        string headSha,
        string status,
        string? detailsUrl = null,
        string? externalId = null,
        CancellationToken ct = default)
    {
        object body = new
        {
            name,
            head_sha = headSha,
            status,
            details_url = detailsUrl,
            external_id = externalId,
            started_at = _clock.GetUtcNow().UtcDateTime.ToString("O"),
        };
        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/check-runs", installationToken, body);
        using var document = await SendAsync(request, ct);
        if (!document.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value))
        {
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not return a check run id.");
        }
        _logger.LogInformation(
            "Opened check run {CheckRunId} \"{Name}\" on {Owner}/{Repo} at {HeadSha}.",
            value, name, owner, repo, headSha);
        return value;
    }

    /// <summary>
    /// Updates a check run: its status, its conclusion once it has one, the title
    /// and summary a reader sees on the Checks tab, and the inline annotations.
    ///
    /// <para><paramref name="annotations"/> is sent in batches of
    /// <see cref="MaxAnnotationsPerRequest"/>; the first batch carries the status
    /// and conclusion, and later batches only add annotations, so a completed run
    /// is never re-completed. GitHub keeps annotations across calls on the same
    /// run, which is what makes the batching invisible to the reader.</para>
    /// </summary>
    public async Task UpdateCheckRunAsync(
        string installationToken,
        string owner,
        string repo,
        long checkRunId,
        string status,
        string? conclusion,
        string title,
        string summary,
        IReadOnlyList<GitHubCheckAnnotation>? annotations = null,
        CancellationToken ct = default)
    {
        var batches = Batch(annotations ?? [], MaxAnnotationsPerRequest);
        var isFirst = true;
        foreach (var batch in batches)
        {
            object output = new
            {
                title,
                summary,
                annotations = batch.Select(a => new
                {
                    path = a.Path,
                    start_line = a.StartLine,
                    end_line = a.EndLine,
                    annotation_level = a.Level,
                    message = a.Message,
                    title = a.Title,
                }).ToList(),
            };

            // Only the first call carries the lifecycle fields. Sending
            // "completed" twice is accepted by GitHub but would re-stamp
            // completed_at on every batch, so the run would claim to have
            // finished when its last annotation was written rather than when the
            // build did.
            object body = isFirst
                ? new
                {
                    status,
                    conclusion,
                    completed_at = conclusion is null ? null : _clock.GetUtcNow().UtcDateTime.ToString("O"),
                    output,
                }
                : new { output };

            using var request = NewJsonRequest(
                new HttpMethod("PATCH"), $"{RepoPath(owner, repo)}/check-runs/{checkRunId}", installationToken, body);
            using var document = await SendAsync(request, ct);
            isFirst = false;
        }

        _logger.LogInformation(
            "Updated check run {CheckRunId} on {Owner}/{Repo} to {Status}/{Conclusion} with {AnnotationCount} annotation(s).",
            checkRunId, owner, repo, status, conclusion ?? "-", annotations?.Count ?? 0);
    }

    /// <summary>
    /// Splits <paramref name="items"/> into chunks of <paramref name="size"/>,
    /// always yielding at least one chunk - an update with no annotations is still
    /// an update, and it is the one that carries the conclusion.
    /// </summary>
    private static List<List<GitHubCheckAnnotation>> Batch(IReadOnlyList<GitHubCheckAnnotation> items, int size)
    {
        if (items.Count == 0) return [[]];
        var batches = new List<List<GitHubCheckAnnotation>>();
        for (var i = 0; i < items.Count; i += size)
        {
            batches.Add(items.Skip(i).Take(size).ToList());
        }
        return batches;
    }
}
