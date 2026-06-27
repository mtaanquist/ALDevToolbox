using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools over the Artifacts surface — the agent-facing parallel of the
/// Projects/Artifacts web tools. Agents can list projects, list a project's builds
/// (with the per-repo commit set and changelog), inspect one build's deliverables
/// and logs, and compare two of a project's builds at the object level. The
/// compiled <c>.app</c> bytes never travel through MCP (they can be tens of MB);
/// each deliverable carries a <c>DownloadPath</c> the agent shares with the user,
/// who appends it to the app's base URL and fetches it via the streaming endpoint
/// — the same pattern as <c>download_symbol_reference</c>. All reads are org-scoped
/// by the EF query filter. See <c>.design/artifacts.md</c>.
/// </summary>
[McpServerToolType]
public sealed class ArtifactsTools
{
    private readonly ArtifactService _artifacts;
    private readonly ReleaseComparisonService _comparison;
    private readonly AppDbContext _db;

    public ArtifactsTools(ArtifactService artifacts, ReleaseComparisonService comparison, AppDbContext db)
    {
        _artifacts = artifacts;
        _comparison = comparison;
        _db = db;
    }

    [McpServerTool(Name = "list_projects", ReadOnly = true)]
    [Description("Lists every project in the organisation — each points at one or more Git repositories that get compiled into downloadable .app files. Returns each project's id, name, owner, repository count, and a summary of its newest build (status, BC version). Use the id with list_project_builds.")]
    public async Task<IReadOnlyList<ProjectArtifactsRow>> ListProjectsAsync(
        [Description("Optional substring to filter by project name, owner, or repository name.")] string? search = null,
        CancellationToken ct = default) =>
        await _artifacts.ListProjectsAsync(search, ct);

    [McpServerTool(Name = "list_project_builds", ReadOnly = true)]
    [Description("Lists a project's builds, newest first. Each build is a compile of the project's repositories at a point in time; returns its id, status ('queued'/'building'/'ready'/'failed'), BC version, timings, who started it, the number of downloadable .app files, and the Object Explorer release id (when ready). Use a build id with get_project_build.")]
    public async Task<IReadOnlyList<BuildRow>> ListProjectBuildsAsync(
        [Description("Project name or numeric id (from list_projects).")] string projectNameOrId,
        CancellationToken ct = default)
    {
        var projectId = await ResolveProjectAsync(projectNameOrId, ct);
        return await _artifacts.ListBuildsAsync(projectId, ct);
    }

    [McpServerTool(Name = "get_project_build", ReadOnly = true)]
    [Description("Returns one build's full detail: the per-repository commit it was built from, the changelog since the project's last successful build (grouped by repository), and the downloadable deliverables. Each deliverable and the whole-build zip and raw log carry a DownloadPath the user appends to the app's base URL to fetch (the bytes are not returned inline). When the build is ready it also returns the Object Explorer release id so its objects can be searched/compared.")]
    public async Task<ProjectBuildDetailResult> GetProjectBuildAsync(
        [Description("Build id (from list_project_builds).")] int buildId,
        CancellationToken ct = default)
    {
        var detail = await _artifacts.GetBuildDetailAsync(buildId, ct)
            ?? throw new McpException($"Build {buildId} was not found in this organisation.");

        var apps = detail.Artifacts
            .Select(a => new BuildAppDownload(
                a.FileName, a.AppName, a.AppVersion, a.RuntimeVersion, a.SizeBytes,
                DownloadPath: $"/artifacts/build/{buildId}/app/{a.Id}"))
            .ToList();

        return new ProjectBuildDetailResult(
            BuildId: detail.Id,
            ProjectId: detail.ProjectId,
            ProjectName: detail.ProjectName,
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

    [McpServerTool(Name = "compare_project_builds", ReadOnly = true)]
    [Description("Diffs two of the SAME project's builds at the object level (added / removed / modified / unchanged), so you can see what objects changed between two compiles. Both builds must be 'ready'. This is deliberately project-scoped — use compare_releases for Microsoft/third-party releases.")]
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
                "Both builds must belong to the same project. compare_project_builds is project-scoped; use compare_releases for cross-release diffs.");
        }

        var rows = await _comparison.CompareReleaseObjectsAsync(leftRelease, rightRelease, ct);
        return changesOnly ? rows.Where(r => r.Status != "unchanged").ToList() : rows;
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<int> ResolveProjectAsync(string projectNameOrId, CancellationToken ct)
    {
        if (int.TryParse(projectNameOrId, out var asId))
        {
            var exists = await _db.OeProjects.AsNoTracking().AnyAsync(p => p.Id == asId && p.DeletedAt == null, ct);
            if (!exists) throw new McpException($"Project {asId} does not exist in this organisation.");
            return asId;
        }
        var name = projectNameOrId.Trim();
        var row = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == name.ToLower())
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            throw new McpException($"Project '{projectNameOrId}' was not found. Call list_projects to see available projects.");
        }
        return row.Id;
    }

    /// <summary>Resolves a build that must be ready and have produced a navigable release; returns (projectId, releaseId).</summary>
    private async Task<(int ProjectId, int ReleaseId)> ResolveReadyBuildAsync(int buildId, CancellationToken ct)
    {
        var build = await _db.OeProjectBuilds.AsNoTracking()
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

/// <summary>One build's detail for the <c>get_project_build</c> MCP tool, with download paths for its deliverables.</summary>
public sealed record ProjectBuildDetailResult(
    int BuildId,
    int ProjectId,
    string ProjectName,
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
