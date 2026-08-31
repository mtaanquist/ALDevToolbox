using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools over the Artifacts surface — the agent-facing parallel of the
/// Projects/Pipelines web tools. Agents can list projects and pipelines (a pipeline
/// is a named build flow under a project), list a pipeline's or project's builds
/// (with the per-repo commit set and changelog), inspect one build's deliverables
/// and logs, and compare two of a project's builds at the object level. The
/// compiled <c>.app</c> bytes never travel through MCP (they can be tens of MB);
/// each deliverable carries a <c>DownloadPath</c> the agent shares with the user,
/// who appends it to the app's base URL and fetches it via the streaming endpoint
/// — the same pattern as <c>download_symbol_reference</c>. All reads are org-scoped
/// by the EF query filter, and project-scoped by <see cref="ProjectAccess"/>: a
/// Private project the caller has no grant on is absent from <c>list_solutions</c>
/// (a locked name is no use to an agent) and unresolvable by every other tool
/// here. See <c>.design/artifacts.md</c> and <c>.design/teams-and-visibility.md</c>.
/// </summary>
[McpServerToolType]
public sealed class ArtifactsTools
{
    private readonly ArtifactService _artifacts;
    private readonly ReleaseComparisonService _comparison;
    private readonly ProjectAccess _access;
    private readonly AppDbContext _db;

    public ArtifactsTools(ArtifactService artifacts, ReleaseComparisonService comparison, ProjectAccess access, AppDbContext db)
    {
        _artifacts = artifacts;
        _comparison = comparison;
        _access = access;
        _db = db;
    }

    [McpServerTool(Name = "list_solutions", ReadOnly = true)]
    [Description("Lists the solutions you can see in the organisation — each points at one or more Git repositories that get compiled into downloadable .app files. Returns each solution's id, name, owner, repository count, and a summary of its newest build (status, BC version). Private solutions you are not on the team for are not listed. Use the id with list_solution_builds.")]
    public async Task<IReadOnlyList<ProjectArtifactsRow>> ListProjectsAsync(
        [Description("Optional substring to filter by solution name, owner, or repository name.")] string? search = null,
        CancellationToken ct = default)
    {
        var rows = await _artifacts.ListProjectsAsync(search, ct);
        // The web list keeps a locked, name-only row so a project doesn't appear
        // to vanish for a human reading /projects. An agent has no such
        // confusion to spare — drop them entirely.
        return rows.Where(r => !r.IsLocked).ToList();
    }

    [McpServerTool(Name = "list_solution_builds", ReadOnly = true)]
    [Description("Lists a solution's builds, newest first. Each build is a compile of the solution's repositories at a point in time; returns its id, status ('queued'/'building'/'ready'/'failed'), BC version, timings, who started it, the number of downloadable .app files, and the Object Explorer release id (when ready). Use a build id with get_solution_build.")]
    public async Task<IReadOnlyList<BuildRow>> ListProjectBuildsAsync(
        [Description("Solution name or numeric id (from list_solutions).")] string solutionNameOrId,
        CancellationToken ct = default)
    {
        var projectId = await ResolveProjectAsync(solutionNameOrId, ct);
        return await _artifacts.ListBuildsForProjectAsync(projectId, ct);
    }

    [McpServerTool(Name = "list_pipelines", ReadOnly = true)]
    [Description("Lists the pipelines you can see in the organisation. A pipeline is a named build flow under a solution that compiles a chosen subset of the solution's extensions (a solution can have several). Returns each pipeline's id, name, its solution, owner, and a summary of its newest build (status, BC version). Pipelines under a private solution you are not on the team for are not listed. Use the id with list_pipeline_builds.")]
    public async Task<IReadOnlyList<PipelineArtifactsRow>> ListPipelinesAsync(
        [Description("Optional substring to filter by pipeline name, solution name, or owner.")] string? search = null,
        CancellationToken ct = default) =>
        await _artifacts.ListPipelinesAsync(search, ct);

    [McpServerTool(Name = "list_pipeline_builds", ReadOnly = true)]
    [Description("Lists one pipeline's builds, newest first. Each build is a run of the pipeline — a compile of its chosen extensions at a point in time; returns its id, status ('queued'/'building'/'ready'/'failed'), BC version, timings, who started it, the number of downloadable .app files, and the Object Explorer release id (when ready). Use a build id with get_solution_build.")]
    public async Task<IReadOnlyList<BuildRow>> ListPipelineBuildsAsync(
        [Description("Pipeline id (from list_pipelines).")] int pipelineId,
        CancellationToken ct = default)
    {
        try
        {
            return await _artifacts.ListBuildsAsync(pipelineId, ct);
        }
        catch (ProjectAccessDeniedException)
        {
            throw NotFound($"Pipeline {pipelineId} was not found in this organisation.");
        }
    }

    [McpServerTool(Name = "get_solution_build", ReadOnly = true)]
    [Description("Returns one build's full detail: the per-repository commit it was built from, the changelog since the solution's last successful build (grouped by repository), and the downloadable deliverables. Each deliverable and the whole-build zip and raw log carry a DownloadPath the user appends to the app's base URL to fetch (the bytes are not returned inline). When the build is ready it also returns the Object Explorer release id so its objects can be searched/compared.")]
    public async Task<ProjectBuildDetailResult> GetProjectBuildAsync(
        [Description("Build id (from list_solution_builds).")] int buildId,
        CancellationToken ct = default)
    {
        BuildDetail? detail;
        try
        {
            detail = await _artifacts.GetBuildDetailAsync(buildId, ct);
        }
        catch (ProjectAccessDeniedException)
        {
            detail = null;
        }
        if (detail is null) throw NotFound($"Build {buildId} was not found in this organisation.");

        var apps = detail.Artifacts
            .Select(a => new BuildAppDownload(
                a.FileName, a.AppName, a.AppVersion, a.RuntimeVersion, a.SizeBytes,
                DownloadPath: $"/artifacts/build/{buildId}/app/{a.Id}"))
            .ToList();

        return new ProjectBuildDetailResult(
            BuildId: detail.Id,
            ProjectId: detail.ProjectId,
            ProjectName: detail.ProjectName,
            PipelineId: detail.PipelineId,
            PipelineName: detail.PipelineName,
            Status: detail.Status,
            BcVersion: detail.BcVersion,
            StartedAt: detail.StartedAt,
            FinishedAt: detail.FinishedAt,
            FailureMessage: detail.FailureMessage,
            StartedByName: detail.StartedByName,
            ReleaseId: detail.ReleaseId,
            RepoCommits: detail.RepoCommits,
            Changelog: detail.Changelog,
            Apps: apps,
            DownloadAllPath: apps.Count > 0 ? $"/artifacts/build/{buildId}/all" : null,
            RawLogPath: detail.Logs.Count > 0 ? $"/artifacts/build/{buildId}/log" : null);
    }

    [McpServerTool(Name = "compare_solution_builds", ReadOnly = true)]
    [Description("Diffs two of the SAME solution's builds at the object level (added / removed / modified / unchanged), so you can see what objects changed between two compiles. Both builds must be 'ready'. This is deliberately solution-scoped — use compare_releases for Microsoft/third-party releases.")]
    public async Task<IReadOnlyList<ObjectCompareRow>> CompareProjectBuildsAsync(
        [Description("First (earlier / base) build id.")] int baseBuildId,
        [Description("Second (later) build id.")] int otherBuildId,
        [Description("When true (default), omit unchanged objects and return only added / removed / modified.")] bool changesOnly = true,
        CancellationToken ct = default)
    {
        var (leftProject, leftRelease) = await ResolveReadyBuildAsync(baseBuildId, ct);
        var (rightProject, rightRelease) = await ResolveReadyBuildAsync(otherBuildId, ct);
        if (leftProject != rightProject)
        {
            throw new McpException(
                "Both builds must belong to the same project. compare_solution_builds is project-scoped; use compare_releases for cross-release diffs.");
        }

        var rows = await _comparison.CompareReleaseObjectsAsync(leftRelease, rightRelease, ct);
        return changesOnly ? rows.Where(r => r.Status != "unchanged").ToList() : rows;
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The one refusal shape these tools use. A project the caller may not see
    /// and an id that doesn't exist answer identically on purpose — a distinct
    /// "denied" would confirm the project is there. See
    /// <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    private static McpException NotFound(string message) => new(message);

    /// <summary>
    /// Resolves a project by name or id, applying the visibility fence in the
    /// same query — this and <see cref="ResolveReadyBuildAsync"/> read
    /// <c>oe_projects</c> directly rather than through a gated service, so they
    /// are where every tool in this class inherits the gate. A project the
    /// caller cannot see answers "does not exist", the same as an id in another
    /// org. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    private async Task<int> ResolveProjectAsync(string projectNameOrId, CancellationToken ct)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        if (int.TryParse(projectNameOrId, out var asId))
        {
            var exists = await _db.OeProjects.AsNoTracking()
                .Where(visible)
                .AnyAsync(p => p.Id == asId && p.DeletedAt == null, ct);
            if (!exists) throw new McpException($"Solution {asId} does not exist in this organisation.");
            return asId;
        }
        var name = projectNameOrId.Trim();
        var row = await _db.OeProjects.AsNoTracking()
            .Where(visible)
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == name.ToLower())
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            throw new McpException($"Solution '{projectNameOrId}' was not found. Call list_solutions to see available solutions.");
        }
        return row.Id;
    }

    /// <summary>Resolves a build that must be ready and have produced a navigable release; returns (projectId, releaseId).</summary>
    private async Task<(int ProjectId, int ReleaseId)> ResolveReadyBuildAsync(int buildId, CancellationToken ct)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => _db.OeProjects.Where(visible).Any(p => p.Id == b.ProjectId))
            .Where(b => b.Id == buildId)
            .Select(b => new { b.ProjectId, b.Status, b.ReleaseId })
            .FirstOrDefaultAsync(ct)
            ?? throw new McpException($"Build {buildId} was not found in this organisation.");
        if (build.Status != ProjectBuildStatus.Ready || build.ReleaseId is null)
        {
            throw new McpException($"Build {buildId} can't be compared — only 'ready' builds that produced a release can be diffed.");
        }
        return (build.ProjectId, build.ReleaseId.Value);
    }
}

/// <summary>One build's detail for the <c>get_solution_build</c> MCP tool, with download paths for its deliverables.</summary>
public sealed record ProjectBuildDetailResult(
    int BuildId,
    int ProjectId,
    string ProjectName,
    int? PipelineId,
    string? PipelineName,
    string Status,
    string? BcVersion,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? StartedByName,
    int? ReleaseId,
    IReadOnlyList<RepoCommitRow> RepoCommits,
    IReadOnlyList<ChangelogGroup> Changelog,
    IReadOnlyList<BuildAppDownload> Apps,
    string? DownloadAllPath,
    string? RawLogPath);

/// <summary>One downloadable deliverable for an MCP caller — metadata plus the path the user fetches it from.</summary>
public sealed record BuildAppDownload(
    string FileName,
    string AppName,
    string AppVersion,
    string? RuntimeVersion,
    long SizeBytes,
    string DownloadPath);
