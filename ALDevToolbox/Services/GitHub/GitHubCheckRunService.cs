using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Reports a pull-request build back to GitHub as a check run (issue #627).
///
/// <para>Two calls, one lifecycle: the worker opens a run <c>in_progress</c>
/// before it queues the build, so the pull request shows something the moment
/// GitHub told us about it, and completes it once the build is done. Both act on
/// the installation token, because a check run is written by an <em>app</em> and
/// this build has no user behind it at all - see the credential split in
/// <c>.design/github-integration.md</c>.</para>
///
/// <para>Every method here is best-effort by design. A check run the toolbox
/// could not write is a missing tick on a pull request; a build that fell over
/// because GitHub was unreachable would be a missing answer, which is worse. So
/// refusals are logged and swallowed, and <see cref="OpenAsync"/> answers null
/// rather than throwing - a build with no check run still builds, still ingests,
/// and is still visible in the toolbox.</para>
/// </summary>
public sealed class GitHubCheckRunService
{
    /// <summary>
    /// How the run is named on the pull request. The prefix says who is speaking
    /// - a repository may be tracked by more than one solution, and each gets its
    /// own run, so the solution's name is what tells them apart.
    /// </summary>
    public static string CheckRunName(string solutionName) => $"AL Dev Toolbox / {solutionName}";

    private readonly AppDbContext _db;
    private readonly GitHubAppClient _github;
    private readonly PublicOrigin _origin;
    private readonly ILogger<GitHubCheckRunService> _logger;

    public GitHubCheckRunService(
        AppDbContext db,
        GitHubAppClient github,
        PublicOrigin origin,
        ILogger<GitHubCheckRunService> logger)
    {
        _db = db;
        _github = github;
        _origin = origin;
        _logger = logger;
    }

    /// <summary>
    /// Opens an <c>in_progress</c> check run for <paramref name="solutionName"/>
    /// on <paramref name="headSha"/>, returning its id or null when GitHub refused.
    /// The commonest refusal by far is the organisation not having granted the
    /// app permission to report build results, which is worth one warning per
    /// attempt and nothing more.
    /// </summary>
    public async Task<long?> OpenAsync(
        long installationId,
        string repositoryFullName,
        string solutionName,
        string headSha,
        int? projectId,
        CancellationToken ct = default)
    {
        var parts = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        try
        {
            var token = await _github.GetInstallationTokenAsync(installationId, ct);
            return await _github.CreateCheckRunAsync(
                token, parts[0], parts[1],
                name: CheckRunName(solutionName),
                headSha: headSha,
                status: "in_progress",
                detailsUrl: DetailsUrl(projectId),
                externalId: projectId?.ToString(),
                ct: ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
        {
            _logger.LogWarning(ex,
                "Could not open a check run on {Repository} at {HeadSha}; the build runs anyway.",
                repositoryFullName, headSha);
            return null;
        }
    }

    /// <summary>
    /// Completes the check run the build carries, reading the build's own state and
    /// its compiler diagnostics to decide what to say.
    ///
    /// <para>The three conclusions mean three different things, and the
    /// distinction is deliberately simple:</para>
    /// <list type="bullet">
    /// <item><description><c>success</c> - the build reached <c>ready</c> and the
    /// compiler reported no errors.</description></item>
    /// <item><description><c>failure</c> - the compiler reported at least one
    /// error, so the pull request does not build.</description></item>
    /// <item><description><c>neutral</c> - the build could not run at all (no
    /// symbols for the declared application version, the compiler unavailable, the
    /// commit gone). Nothing was learned about the code, so a red X would be a
    /// claim we cannot support; the summary says what stopped it.</description></item>
    /// </list>
    /// </summary>
    public async Task CompleteAsync(
        long installationId,
        string repositoryFullName,
        int releaseId,
        CancellationToken ct = default)
    {
        var parts = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return;

        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .Select(b => new { b.Id, b.ProjectId, b.Status, b.FailureMessage, b.CheckRunId, b.BcVersion })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (build?.CheckRunId is not long checkRunId) return;

        var diagnostics = await _db.OeProjectBuildDiagnostics.AsNoTracking()
            .Where(d => d.ProjectBuildId == build.Id)
            .OrderBy(d => d.Ordering)
            .ToListAsync(ct).ConfigureAwait(false);
        var results = await _db.OeProjectBuildResults.AsNoTracking()
            .Where(r => r.ReleaseId == releaseId)
            .Select(r => new { r.AppName, r.Status, r.Message })
            .ToListAsync(ct).ConfigureAwait(false);

        var errors = diagnostics.Count(d => d.Severity == ProjectBuildDiagnosticSeverity.Error);
        var warnings = diagnostics.Count(d => d.Severity == ProjectBuildDiagnosticSeverity.Warning);
        var compiled = results.Count(r => r.Status is ProjectBuildResultStatus.Compiled or ProjectBuildResultStatus.Ingested);

        string conclusion, title;
        if (errors > 0)
        {
            conclusion = GitHubCheckConclusion.Failure;
            title = errors == 1 ? "1 compile error" : $"{errors} compile errors";
        }
        else if (build.Status == ProjectBuildStatus.Ready)
        {
            conclusion = GitHubCheckConclusion.Success;
            title = compiled == 1 ? "1 extension compiled" : $"{compiled} extensions compiled";
        }
        else
        {
            conclusion = GitHubCheckConclusion.Neutral;
            title = "The build could not run";
        }

        var summary = BuildSummary(
            conclusion, build.FailureMessage, build.BcVersion, errors, warnings,
            results.Select(r => (r.AppName, r.Status, r.Message)).ToList());

        var annotations = diagnostics
            .Where(d => d.Path.Length > 0 && d.Line > 0)
            .Select(d => new GitHubCheckAnnotation(
                Path: d.Path,
                StartLine: d.Line,
                EndLine: d.Line,
                Level: d.Severity switch
                {
                    ProjectBuildDiagnosticSeverity.Error => GitHubCheckAnnotationLevel.Failure,
                    ProjectBuildDiagnosticSeverity.Warning => GitHubCheckAnnotationLevel.Warning,
                    _ => GitHubCheckAnnotationLevel.Notice,
                },
                Message: d.Message,
                Title: d.Code.Length > 0 ? d.Code : null))
            .ToList();

        try
        {
            var token = await _github.GetInstallationTokenAsync(installationId, ct);
            await _github.UpdateCheckRunAsync(
                token, parts[0], parts[1], checkRunId,
                status: "completed",
                conclusion: conclusion,
                title: title,
                summary: summary,
                annotations: annotations,
                ct: ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
        {
            _logger.LogWarning(ex,
                "Could not complete check run {CheckRunId} on {Repository}; the build itself is recorded in the toolbox.",
                checkRunId, repositoryFullName);
        }
    }

    /// <summary>
    /// The Markdown body of the check run: what happened, then per-extension
    /// outcomes. Deliberately short - the annotations carry the detail, and the
    /// summary is what a reviewer reads first.
    /// </summary>
    private static string BuildSummary(
        string conclusion,
        string? failureMessage,
        string? bcVersion,
        int errors,
        int warnings,
        IReadOnlyList<(string AppName, string Status, string? Message)> results)
    {
        var lines = new List<string>();
        lines.Add(conclusion switch
        {
            GitHubCheckConclusion.Success => "Everything compiled.",
            GitHubCheckConclusion.Failure => "The compiler reported errors. Each one is marked on its own line in the Files tab.",
            _ => failureMessage ?? "The build could not run, so nothing was compiled.",
        });

        if (bcVersion is { Length: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add($"Compiled against Business Central {bcVersion}.");
        }

        if (errors > 0 || warnings > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{Count(errors, "error")} and {Count(warnings, "warning")}.");
        }

        if (results.Count > 0)
        {
            lines.Add(string.Empty);
            foreach (var r in results.OrderBy(r => r.AppName, StringComparer.OrdinalIgnoreCase))
            {
                var mark = r.Status is ProjectBuildResultStatus.Compiled or ProjectBuildResultStatus.Ingested ? "OK" : "Failed";
                lines.Add($"- {r.AppName}: {mark}{(r.Message is { Length: > 0 } m ? $" - {m}" : string.Empty)}");
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>"3 errors" / "1 warning". A person reads this on the pull request, so it reads like a sentence.</summary>
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>
    /// Where "Details" on the check run goes: the solution in the toolbox, built
    /// from the configured public origin. Null when the deployment has not been
    /// told its own address - a link to <c>localhost</c> would be worse than no
    /// link, and GitHub simply renders the run without one.
    /// </summary>
    private string? DetailsUrl(int? projectId) =>
        _origin.IsConfigured && projectId is int id ? $"{_origin.Configured}/solutions/{id}" : null;
}
