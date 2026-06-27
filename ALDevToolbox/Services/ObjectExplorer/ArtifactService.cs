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

    // ── Build history + detail ──────────────────────────────────────────

    /// <summary>One project's builds, newest first — the Artifacts build history.</summary>
    public async Task<List<BuildRow>> ListBuildsAsync(int projectId, CancellationToken ct = default)
    {
        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new
            {
                b.Id, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage,
                StartedByName = b.StartedByUser != null ? b.StartedByUser.DisplayName : null,
                ArtifactCount = b.Artifacts.Count,
            })
            .ToListAsync(ct);

        // Representative commit per build for the history list, so a row can show
        // "what changed" inline — saving the user a trip into the repo. Builds are
        // multi-repo; we surface the head commit (Ordering 0, newest as git emitted
        // it) plus a count so the row can hint at the rest. A build-level summary
        // note (first build / force-push) has an empty hash but still carries a
        // message, which the UI shows as the fallback. See .design/artifacts.md.
        var buildIds = builds.Select(b => b.Id).ToList();
        var commits = await _db.OeProjectBuildCommits.AsNoTracking()
            .Where(c => buildIds.Contains(c.ProjectBuildId))
            .Select(c => new { c.ProjectBuildId, c.ShortHash, c.Message, c.Ordering })
            .ToListAsync(ct);
        var headByBuild = commits
            .GroupBy(c => c.ProjectBuildId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ordering).ToList());

        return builds.Select(b =>
        {
            headByBuild.TryGetValue(b.Id, out var cs);
            var head = cs is { Count: > 0 } ? cs[0] : null;
            return new BuildRow(
                b.Id, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage, b.StartedByName, b.ArtifactCount,
                HeadCommitShort: string.IsNullOrEmpty(head?.ShortHash) ? null : head!.ShortHash,
                HeadCommitMessage: string.IsNullOrEmpty(head?.Message) ? null : head!.Message,
                CommitCount: cs?.Count ?? 0);
        }).ToList();
    }

    /// <summary>True while any of the project's builds is still queued or building — drives the live status poll.</summary>
    public async Task<bool> HasBuildInFlightAsync(int projectId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuilds.AsNoTracking()
            .AnyAsync(b => b.ProjectId == projectId
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
                b.Id, b.ProjectId, b.ReleaseId, b.Status, b.BcVersion, b.Branch,
                b.StartedAt, b.FinishedAt, b.FailureMessage,
                StartedBy = b.StartedByUser != null ? b.StartedByUser.DisplayName : null,
                ProjectName = b.Project != null ? b.Project.Name : string.Empty,
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
            build.Id, build.ProjectId, build.ProjectName, build.ReleaseId, build.Status,
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
    /// The builds of one project that can be compared — those that produced a
    /// navigable Release (ready, with a ReleaseId), newest first. The picker is
    /// deliberately project-scoped: the global Object Explorer compare never lists
    /// project builds. See <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task<List<ComparableBuildRow>> ListComparableBuildsAsync(int projectId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == projectId && b.Status == ProjectBuildStatus.Ready && b.ReleaseId != null)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new ComparableBuildRow(b.Id, b.ReleaseId!.Value, b.BcVersion, b.StartedAt))
            .ToListAsync(ct);
    }

    // ── Download byte fetches (endpoints) ───────────────────────────────

    /// <summary>The id of the project's newest <c>ready</c> build, or null when none succeeded — the "Download all" target.</summary>
    public async Task<int?> GetLatestSuccessfulBuildIdAsync(int projectId, CancellationToken ct = default)
    {
        return await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == projectId && b.Status == ProjectBuildStatus.Ready)
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

/// <summary>One build in the history list.</summary>
/// <remarks>
/// <see cref="HeadCommitShort"/> / <see cref="HeadCommitMessage"/> / <see cref="CommitCount"/>
/// surface the build's head changelog commit so the history can show what changed
/// without opening each build. They're optional (and default to empty) for builds
/// with no captured commits.
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
