using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The read surface behind the Artifacts tool (and the Projects tool's latest-build
/// chip): project directories with their newest build, per-project build history,
/// a build's commits / changelog / logs / deliverables, the project-scoped compare
/// picker, and the byte fetches the download endpoints stream. All reads are
/// org-scoped by the EF query filter — these run inside a normal authenticated
/// request. Mutations (create/build/delete) live in <see cref="ProjectService"/> /
/// <see cref="ProjectBuildImporter"/>; this service never writes. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public sealed class ArtifactService
{
    private readonly AppDbContext _db;

    public ArtifactService(AppDbContext db)
    {
        _db = db;
    }

    // ── Project directory (Projects + Artifacts browsers) ───────────────

    /// <summary>
    /// Active projects with owner, repo count, and a summary of their newest build,
    /// ordered by name. Optionally filtered by a name/owner/repo substring. Drives
    /// both the Projects directory and the Artifacts landing.
    /// </summary>
    public async Task<List<ProjectArtifactsRow>> ListProjectsAsync(string? search = null, CancellationToken ct = default)
    {
        var projects = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Select(p => new
            {
                p.Id,
                p.Name,
                OwnerName = p.CreatedByUser != null ? p.CreatedByUser.DisplayName : null,
                RepoCount = p.Repositories.Count,
                RepoNames = p.Repositories.Select(r => r.DisplayName).ToList(),
            })
            .ToListAsync(ct);

        // The newest build per project in one query, plus the newest *successful*
        // one (the "Download all" target). Bounded per org, so the in-memory join
        // is cheap and keeps the projection simple.
        var projectIds = projects.Select(p => p.Id).ToList();
        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => projectIds.Contains(b.ProjectId))
            .Select(b => new
            {
                b.Id, b.ProjectId, b.Status, b.BcVersion, b.Branch, b.StartedAt, b.FinishedAt,
                ArtifactCount = b.Artifacts.Count,
            })
            .ToListAsync(ct);
        var byProject = builds.GroupBy(b => b.ProjectId).ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.StartedAt).ToList());

        // One representative commit per latest build for the list's "latest build"
        // cell. Builds are multi-repo, so show the first repo's commit (by display
        // name, matching the detail page's ordering).
        var latestBuildIds = byProject.Values.Where(l => l.Count > 0).Select(l => l[0].Id).ToList();
        var commitByBuild = (await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => latestBuildIds.Contains(c.ProjectBuildId))
                .OrderBy(c => c.RepoDisplayName)
                .Select(c => new { c.ProjectBuildId, c.CommitHash })
                .ToListAsync(ct))
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.First().CommitHash);

        var rows = new List<ProjectArtifactsRow>(projects.Count);
        foreach (var p in projects)
        {
            byProject.TryGetValue(p.Id, out var pb);
            var latest = pb is { Count: > 0 } ? pb[0] : null;
            var latestSuccessful = pb?.FirstOrDefault(b => b.Status == ProjectBuildStatus.Ready);
            string? commitShort = null;
            if (latest is not null && commitByBuild.TryGetValue(latest.Id, out var hash) && !string.IsNullOrEmpty(hash))
                commitShort = hash.Length > 7 ? hash[..7] : hash;
            rows.Add(new ProjectArtifactsRow(
                p.Id, p.Name, p.OwnerName, p.RepoCount,
                Latest: latest is null ? null : new BuildSummary(
                    latest.Id, latest.Status, latest.BcVersion, latest.Branch, commitShort, latest.StartedAt, latest.FinishedAt, latest.ArtifactCount),
                LatestSuccessfulBuildId: latestSuccessful?.Id,
                RepoNames: p.RepoNames));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r =>
                    r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (r.OwnerName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || r.RepoNames.Any(n => n.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The project header (name + owner) for the Artifacts builds page, or null when not found / deleted.</summary>
    public async Task<ProjectHeader?> GetProjectHeaderAsync(int projectId, CancellationToken ct = default)
    {
        return await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new ProjectHeader(
                p.Id, p.Name,
                p.CreatedByUser != null ? p.CreatedByUser.DisplayName : null,
                p.CreatedByUserId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The project a <c>project</c>-kind Release belongs to, via the build that
    /// produced it — so a user who deep-linked into an unlisted project release in
    /// the Object Explorer can get back to its Artifacts page. Null when the release
    /// isn't a tracked project build. See <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task<int?> GetProjectIdForReleaseAsync(int releaseId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .Select(b => (int?)b.ProjectId)
            .FirstOrDefaultAsync(ct);
    }

    // ── Pipeline directory (Pipelines landing) ──────────────────────────

    /// <summary>
    /// Active pipelines with their project, owner, and a summary of their newest
    /// build, ordered by project then pipeline name. Optionally filtered by a
    /// pipeline/project/owner substring. Drives the Pipelines landing.
    /// </summary>
    public async Task<List<PipelineArtifactsRow>> ListPipelinesAsync(string? search = null, CancellationToken ct = default)
    {
        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Select(p => new
            {
                p.Id, p.Name, p.ProjectId,
                ProjectName = p.Project!.Name,
                OwnerName = p.Project.CreatedByUser != null ? p.Project.CreatedByUser.DisplayName : null,
            })
            .ToListAsync(ct);

        // The newest build per pipeline (and the newest successful one) in one query.
        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId!.Value))
            .Select(b => new
            {
                b.Id, PipelineId = b.PipelineId!.Value, b.Status, b.BcVersion, b.Branch, b.StartedAt, b.FinishedAt,
                ArtifactCount = b.Artifacts.Count,
            })
            .ToListAsync(ct);
        var byPipeline = builds.GroupBy(b => b.PipelineId).ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.StartedAt).ToList());

        var latestBuildIds = byPipeline.Values.Where(l => l.Count > 0).Select(l => l[0].Id).ToList();
        var commitByBuild = (await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => latestBuildIds.Contains(c.ProjectBuildId))
                .OrderBy(c => c.RepoDisplayName)
                .Select(c => new { c.ProjectBuildId, c.CommitHash })
                .ToListAsync(ct))
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.First().CommitHash);

        var rows = new List<PipelineArtifactsRow>(pipelines.Count);
        foreach (var p in pipelines)
        {
            byPipeline.TryGetValue(p.Id, out var pb);
            var latest = pb is { Count: > 0 } ? pb[0] : null;
            var latestSuccessful = pb?.FirstOrDefault(b => b.Status == ProjectBuildStatus.Ready);
            string? commitShort = null;
            if (latest is not null && commitByBuild.TryGetValue(latest.Id, out var hash) && !string.IsNullOrEmpty(hash))
                commitShort = hash.Length > 7 ? hash[..7] : hash;
            rows.Add(new PipelineArtifactsRow(
                p.Id, p.Name, p.ProjectId, p.ProjectName, p.OwnerName,
                Latest: latest is null ? null : new BuildSummary(
                    latest.Id, latest.Status, latest.BcVersion, latest.Branch, commitShort, latest.StartedAt, latest.FinishedAt, latest.ArtifactCount),
                LatestSuccessfulBuildId: latestSuccessful?.Id));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r =>
                    r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.ProjectName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (r.OwnerName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        return rows
            .OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The pipeline header (name + project + owner) for the pipeline detail page, or null when not found / deleted.</summary>
    public async Task<PipelineHeader?> GetPipelineHeaderAsync(int pipelineId, CancellationToken ct = default)
    {
        return await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new PipelineHeader(
                p.Id, p.Name, p.ProjectId, p.Project!.Name,
                p.Project.CreatedByUser != null ? p.Project.CreatedByUser.DisplayName : null,
                p.Project.CreatedByUserId))
            .FirstOrDefaultAsync(ct);
    }

    // ── Build history + detail ──────────────────────────────────────────

    /// <summary>One pipeline's builds, newest first — the pipeline detail page's build history.</summary>
    public Task<List<BuildRow>> ListBuildsAsync(int pipelineId, CancellationToken ct = default) =>
        ListBuildsCoreAsync(_db.OeProjectBuilds.AsNoTracking().Where(b => b.PipelineId == pipelineId), ct);

    /// <summary>All of a project's builds across its pipelines, newest first — the MCP <c>list_project_builds</c> surface.</summary>
    public Task<List<BuildRow>> ListBuildsForProjectAsync(int projectId, CancellationToken ct = default) =>
        ListBuildsCoreAsync(_db.OeProjectBuilds.AsNoTracking().Where(b => b.ProjectId == projectId), ct);

    private async Task<List<BuildRow>> ListBuildsCoreAsync(IQueryable<ProjectBuild> filtered, CancellationToken ct)
    {
        var builds = await filtered
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new
            {
                b.Id, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage,
                StartedByName = b.StartedByUser != null ? b.StartedByUser.DisplayName : null,
                ArtifactCount = b.Artifacts.Count,
            })
            .ToListAsync(ct);

        var buildIds = builds.Select(b => b.Id).ToList();

        // The changelog ("what changed since the last successful build") names each
        // row and its size drives the "+N more" hint. A first build / a build with
        // no new commits has only a summary note here (empty hash, message only).
        var changelog = await _db.OeProjectBuildCommits.AsNoTracking()
            .Where(c => buildIds.Contains(c.ProjectBuildId))
            .Select(c => new { c.ProjectBuildId, c.ShortHash, c.Message, c.Ordering })
            .ToListAsync(ct);
        var changelogByBuild = changelog
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordering).ToList());

        // The build's pinned commit (what it was built *at*) — shown when the
        // changelog is just a note, so every real build still surfaces a hash.
        // Representative = first repo by display name, matching the landing + hero.
        var pinnedByBuild = (await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => buildIds.Contains(c.ProjectBuildId))
                .OrderBy(c => c.RepoDisplayName)
                .Select(c => new { c.ProjectBuildId, c.CommitHash })
                .ToListAsync(ct))
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.First().CommitHash);

        return builds.Select(b =>
        {
            changelogByBuild.TryGetValue(b.Id, out var cl);
            // Real commits only (a summary note has an empty hash). When there are
            // new commits, the head names the row so hash + message come from the
            // same commit; otherwise fall back to the build's pinned commit + note.
            var realCommits = cl?.Where(c => !string.IsNullOrEmpty(c.ShortHash)).ToList() ?? [];
            var head = realCommits.Count > 0 ? realCommits[0] : null;

            string? shortHash;
            string? message;
            if (head is not null)
            {
                shortHash = head.ShortHash;
                message = head.Message;
            }
            else
            {
                pinnedByBuild.TryGetValue(b.Id, out var pinned);
                shortHash = string.IsNullOrEmpty(pinned) ? null : (pinned.Length > 7 ? pinned[..7] : pinned);
                message = cl?.FirstOrDefault()?.Message; // the summary note, if any
            }

            return new BuildRow(
                b.Id, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage, b.StartedByName, b.ArtifactCount,
                HeadCommitShort: shortHash,
                HeadCommitMessage: string.IsNullOrEmpty(message) ? null : message,
                CommitCount: realCommits.Count);
        }).ToList();
    }

    /// <summary>True while any of the pipeline's builds is still queued or building — drives the live status poll.</summary>
    public async Task<bool> HasBuildInFlightAsync(int pipelineId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuilds.AsNoTracking()
            .AnyAsync(b => b.PipelineId == pipelineId
                           && (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building), ct);
    }

    /// <summary>
    /// One build's full detail: the per-repo commit set, the changelog grouped by
    /// repo, the deliverables (metadata only — no bytes), and the log sections.
    /// Null when the build isn't in the acting org.
    /// </summary>
    public async Task<BuildDetail?> GetBuildDetailAsync(int buildId, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => new
            {
                b.Id, b.ProjectId, b.PipelineId, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage,
                StartedBy = b.StartedByUser != null ? b.StartedByUser.DisplayName : null,
                ProjectName = b.Project != null ? b.Project.Name : string.Empty,
                PipelineName = b.Pipeline != null ? b.Pipeline.Name : null,
            })
            .FirstOrDefaultAsync(ct);
        if (build is null) return null;

        var repoCommits = await _db.OeProjectBuildRepoCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId)
            .OrderBy(c => c.RepoDisplayName)
            .Select(c => new RepoCommitRow(c.RepoDisplayName, c.RepoUrl, c.CommitHash, c.CommittedAt))
            .ToListAsync(ct);

        var changelog = await _db.OeProjectBuildCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId)
            .OrderBy(c => c.Ordering)
            .Select(c => new { c.ProjectRepositoryId, c.ShortHash, c.Message, c.Author, c.CommittedAt })
            .ToListAsync(ct);

        // Group the changelog by repo using the repo commit set's display names so
        // the UI can show "changes in <repo>". A repo id we no longer have a name
        // for (repo removed since) falls back to a generic label.
        var repoNamesById = await _db.OeProjectBuildRepoCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId && c.ProjectRepositoryId != null)
            .Select(c => new { c.ProjectRepositoryId, c.RepoDisplayName })
            .ToListAsync(ct);
        var nameLookup = repoNamesById
            .GroupBy(x => x.ProjectRepositoryId!.Value)
            .ToDictionary(g => g.Key, g => g.First().RepoDisplayName);

        var changelogGroups = changelog
            .GroupBy(c => c.ProjectRepositoryId)
            .Select(g => new ChangelogGroup(
                g.Key is { } rid && nameLookup.TryGetValue(rid, out var nm) ? nm : "Repository",
                g.Select(c => new ChangelogRow(c.ShortHash, c.Message, c.Author, c.CommittedAt)).ToList()))
            .OrderBy(g => g.RepoName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var artifacts = await ListArtifactRowsAsync(buildId, ct);

        var logSections = await _db.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId)
            .OrderBy(l => l.Ordering)
            .Select(l => new LogSectionRow(l.Section, l.Content))
            .ToListAsync(ct);

        return new BuildDetail(
            build.Id, build.ProjectId, build.ProjectName, build.PipelineId, build.PipelineName,
            build.ReleaseId, build.Status,
            build.BcVersion, build.Branch, build.StartedAt, build.FinishedAt, build.FailureMessage,
            build.StartedBy, repoCommits, changelogGroups, artifacts, logSections);
    }

    /// <summary>The deliverables of a build (metadata only), ordered by file name.</summary>
    public async Task<List<ArtifactRow>> ListArtifactRowsAsync(int buildId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == buildId)
            .OrderBy(a => a.FileName)
            .Select(a => new ArtifactRow(a.Id, a.FileName, a.AppName, a.AppVersion, a.RuntimeVersion, a.SizeBytes))
            .ToListAsync(ct);
    }

    // ── Project-scoped compare ──────────────────────────────────────────

    /// <summary>
    /// The builds of one pipeline that can be compared — those that produced a
    /// navigable Release (ready, with a ReleaseId), newest first. The picker is
    /// deliberately pipeline-scoped: the global Object Explorer compare never lists
    /// project builds. See <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task<List<ComparableBuildRow>> ListComparableBuildsAsync(int pipelineId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId == pipelineId && b.Status == ProjectBuildStatus.Ready && b.ReleaseId != null)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new ComparableBuildRow(b.Id, b.ReleaseId!.Value, b.BcVersion, b.StartedAt))
            .ToListAsync(ct);
    }

    // ── Download byte fetches (endpoints) ───────────────────────────────

    /// <summary>The id of the pipeline's newest <c>ready</c> build, or null when none succeeded — the "Download all" target.</summary>
    public async Task<int?> GetLatestSuccessfulBuildIdAsync(int pipelineId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId == pipelineId && b.Status == ProjectBuildStatus.Ready)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>One deliverable's bytes for streaming, or null when it isn't in the acting org / build.</summary>
    public async Task<DownloadFile?> GetArtifactBytesAsync(int buildId, int artifactId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.Id == artifactId && a.ProjectBuildId == buildId)
            .Select(a => new DownloadFile(a.FileName, a.Content))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Every deliverable's bytes for a build's "Download all" zip. Empty when the build has none.</summary>
    public async Task<List<DownloadFile>> GetAllArtifactBytesAsync(int buildId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == buildId)
            .OrderBy(a => a.FileName)
            .Select(a => new DownloadFile(a.FileName, a.Content))
            .ToListAsync(ct);
    }

    /// <summary>The build's concatenated raw log for the <c>Raw log</c> download, or null when the build has none.</summary>
    public async Task<RawLog?> GetRawLogAsync(int buildId, CancellationToken ct = default)
    {
        var exists = await _db.OeProjectBuilds.AsNoTracking().AnyAsync(b => b.Id == buildId, ct);
        if (!exists) return null;

        var sections = await _db.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId)
            .OrderBy(l => l.Ordering)
            .Select(l => new { l.Section, l.Content })
            .ToListAsync(ct);

        var text = string.Join("\n\n", sections.Select(s => $"=== {s.Section} ===\n{s.Content}"));
        return new RawLog($"build-{buildId}-log.txt", text);
    }
}

// ── DTOs ────────────────────────────────────────────────────────────────

/// <summary>A project row for the Projects / Artifacts directories: identity, owner, repo count, and its newest build.</summary>
public sealed record ProjectArtifactsRow(
    int Id,
    string Name,
    string? OwnerName,
    int RepoCount,
    BuildSummary? Latest,
    int? LatestSuccessfulBuildId,
    IReadOnlyList<string> RepoNames);

/// <summary>A compact summary of one build for a directory chip.</summary>
public sealed record BuildSummary(int BuildId, string Status, string? BcVersion, string? Branch, string? CommitShort, DateTime StartedAt, DateTime? FinishedAt, int ArtifactCount);

/// <summary>A project's header for the Artifacts builds page.</summary>
public sealed record ProjectHeader(int Id, string Name, string? OwnerName, int? OwnerUserId);

/// <summary>A pipeline row for the Pipelines landing: identity, its project, owner, and its newest build.</summary>
public sealed record PipelineArtifactsRow(
    int Id,
    string Name,
    int ProjectId,
    string ProjectName,
    string? OwnerName,
    BuildSummary? Latest,
    int? LatestSuccessfulBuildId);

/// <summary>A pipeline's header for the pipeline detail page (its project + owner drive the breadcrumb and manage-gating).</summary>
public sealed record PipelineHeader(int Id, string Name, int ProjectId, string ProjectName, string? OwnerName, int? OwnerUserId);

/// <summary>One build in the history list.</summary>
/// <remarks>
/// <see cref="HeadCommitShort"/> / <see cref="HeadCommitMessage"/> / <see cref="CommitCount"/>
/// let the history show what changed without opening each build: the head changelog
/// commit when there are new commits, otherwise the build's pinned commit hash plus
/// the summary note ("first build" / "no new commits"). All optional — they default
/// to empty for a build with neither a changelog nor a pinned commit.
/// </remarks>
public sealed record BuildRow(
    int Id,
    int? ReleaseId,
    string Status,
    string? BcVersion,
    string? Branch,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? StartedByName,
    int ArtifactCount,
    string? HeadCommitShort = null,
    string? HeadCommitMessage = null,
    int CommitCount = 0);

/// <summary>One build's full detail for the Artifacts build card.</summary>
public sealed record BuildDetail(
    int Id,
    int ProjectId,
    string ProjectName,
    int? PipelineId,
    string? PipelineName,
    int? ReleaseId,
    string Status,
    string? BcVersion,
    string? Branch,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? StartedByName,
    IReadOnlyList<RepoCommitRow> RepoCommits,
    IReadOnlyList<ChangelogGroup> Changelog,
    IReadOnlyList<ArtifactRow> Artifacts,
    IReadOnlyList<LogSectionRow> Logs);

/// <summary>One repository's pinned commit for a build.</summary>
public sealed record RepoCommitRow(string RepoName, string RepoUrl, string CommitHash, DateTime? CommittedAt);

/// <summary>The changelog for one repository within a build.</summary>
public sealed record ChangelogGroup(string RepoName, IReadOnlyList<ChangelogRow> Commits);

/// <summary>One changelog commit (or a summary note when <see cref="ShortHash"/> is empty).</summary>
public sealed record ChangelogRow(string ShortHash, string Message, string Author, DateTime? CommittedAt);

/// <summary>One downloadable deliverable's metadata.</summary>
public sealed record ArtifactRow(int Id, string FileName, string AppName, string AppVersion, string? RuntimeVersion, long SizeBytes);

/// <summary>One captured log section.</summary>
public sealed record LogSectionRow(string Section, string Content);

/// <summary>A build eligible for project-scoped comparison.</summary>
public sealed record ComparableBuildRow(int BuildId, int ReleaseId, string? BcVersion, DateTime StartedAt);

/// <summary>A file's bytes ready to stream from a download endpoint.</summary>
public sealed record DownloadFile(string FileName, byte[] Content);

/// <summary>A build's raw log ready to stream.</summary>
public sealed record RawLog(string FileName, string Content);
