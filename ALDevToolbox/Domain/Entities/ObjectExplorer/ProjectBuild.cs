using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One build of a <see cref="Project"/> — a first-class entity split off
/// <see cref="Release"/>. A build is a <em>set</em> of <c>(repository, commit)</c>
/// pairs (<see cref="ProjectBuildRepoCommit"/>) with captured logs
/// (<see cref="ProjectBuildLog"/>), a per-repo changelog
/// (<see cref="ProjectBuildCommit"/>), and the retained downloadable <c>.app</c>
/// deliverables (<see cref="ProjectBuildArtifact"/>) — none of which a Release
/// models. It still produces exactly one <c>project</c>-kind Release for Object
/// Explorer object navigation, referenced by <see cref="ReleaseId"/> (the
/// importer hook). Org-scoped via the standard query filter. See
/// <c>.design/artifacts.md</c>.
/// </summary>
public class ProjectBuild
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project this build belongs to. Builds ride along on the project's soft-delete.</summary>
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>
    /// The pipeline this build is a run of. Nullable (<c>ON DELETE SET NULL</c>) so
    /// deleting a pipeline doesn't destroy its build history — the build keeps its
    /// deliverables and stays attributable via <see cref="ProjectId"/>. Null only
    /// for migration-synthesised legacy builds before the Default pipeline backfill.
    /// </summary>
    public int? PipelineId { get; set; }
    public Pipeline? Pipeline { get; set; }

    /// <summary>
    /// User who triggered the build (the clone runs as them, using their per-user
    /// repository token). Nullable so a build outlives the account that started it
    /// (FK <c>ON DELETE SET NULL</c>) and so migration-synthesised legacy builds
    /// without a known starter are representable.
    /// </summary>
    public int? StartedByUserId { get; set; }
    public User? StartedByUser { get; set; }

    /// <summary>
    /// The produced <c>project</c>-kind <see cref="Release"/> — the Object Explorer
    /// hook that keeps the build's objects navigable. Nullable: set once the
    /// release row exists, and cleared (<c>ON DELETE SET NULL</c>) if the release
    /// is later reaped, leaving the build's deliverables and logs intact.
    /// </summary>
    public int? ReleaseId { get; set; }
    public Release? Release { get; set; }

    /// <summary>
    /// The branch built (provenance label). A manual build clones the default
    /// branch and leaves this null; a pull-request build stamps the head ref.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// What asked for this build: <c>manual</c> (a person pressed Build) or
    /// <c>pull_request</c> (GitHub told us a pull request moved). See
    /// <see cref="ProjectBuildTrigger"/>. Existing rows are <c>manual</c>, which
    /// is what they were.
    /// </summary>
    public string Trigger { get; set; } = ProjectBuildTrigger.Manual;

    /// <summary>The pull request this build is about, for a <c>pull_request</c> build; null otherwise.</summary>
    public int? PullRequestNumber { get; set; }

    /// <summary>
    /// The exact commit built. Set for a pull-request build, where the head is
    /// the whole point and moves under the branch name; null for a manual build,
    /// whose per-repository commits are recorded as <see cref="RepoCommits"/>.
    /// </summary>
    public string? HeadSha { get; set; }

    /// <summary>
    /// The GitHub check run this build reports into, so the worker can complete
    /// the run it opened. Null for every build that is not reporting to GitHub.
    /// </summary>
    public long? CheckRunId { get; set; }

    /// <summary>One of <c>queued</c>, <c>building</c>, <c>ready</c>, <c>failed</c>. See <see cref="ProjectBuildStatus"/>.</summary>
    public string Status { get; set; } = ProjectBuildStatus.Queued;

    /// <summary>Resolved BC application version the build compiled against (e.g. <c>25.18</c>). Null until known.</summary>
    public string? BcVersion { get; set; }

    /// <summary>Why a <c>failed</c> build failed (the whole-build reason); null otherwise.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// The extensions the user chose to compile, as a JSON array of app-id GUID
    /// strings captured from the "New build" picker's live discovery. <c>null</c>
    /// means "build everything discovered" — today's behaviour, and what a
    /// restart-resumed or migration-synthesised build falls back to. The worker
    /// reads this off the build row and filters the discovered set before compiling.
    /// See <c>.design/artifacts.md</c>.
    /// </summary>
    public string? RequestedAppIdsJson { get; set; }

    /// <summary>
    /// The GitHub Release tag this build was published as (<c>v1.2.3.0</c>), when the
    /// pipeline names a repository to publish to. It doubles as the marker of a
    /// <em>staged</em> build: a build with a tag and no pipeline was not compiled here
    /// at all but downloaded from a Release so it could be deployed. Null when nothing
    /// was published. See <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    public string? GithubReleaseTag { get; set; }

    /// <summary>The published Release's page on GitHub, for the link on the build card. Null when nothing was published.</summary>
    public string? GithubReleaseUrl { get; set; }

    /// <summary>
    /// Why the build was not published as a Release - GitHub's own refusal, or "the apps
    /// have different versions". A publish failure is never a build failure: the
    /// <c>.app</c> files exist and download regardless, so this is a note on a build
    /// that is still <c>ready</c>.
    /// </summary>
    public string? GithubReleaseError { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>When the build reached a terminal state (<c>ready</c> / <c>failed</c>); null while in flight.</summary>
    public DateTime? FinishedAt { get; set; }

    public ICollection<ProjectBuildRepoCommit> RepoCommits { get; set; } = new List<ProjectBuildRepoCommit>();
    public ICollection<ProjectBuildCommit> Changelog { get; set; } = new List<ProjectBuildCommit>();
    public ICollection<ProjectBuildArtifact> Artifacts { get; set; } = new List<ProjectBuildArtifact>();
    public ICollection<ProjectBuildLog> Logs { get; set; } = new List<ProjectBuildLog>();
    public ICollection<ProjectBuildDiagnostic> Diagnostics { get; set; } = new List<ProjectBuildDiagnostic>();
}

/// <summary>What asked for a <see cref="ProjectBuild"/>.</summary>
public static class ProjectBuildTrigger
{
    /// <summary>A person pressed Build on a pipeline. The clone uses their own repository token.</summary>
    public const string Manual = "manual";

    /// <summary>
    /// GitHub told us a pull request opened, reopened or gained a commit. There is
    /// no user behind it, so the clone and the check run both act as the app. See
    /// <c>.design/github-integration-phase2.md</c> (#627).
    /// </summary>
    public const string PullRequest = "pull_request";
}

/// <summary>The lifecycle states a <see cref="ProjectBuild"/> moves through.</summary>
public static class ProjectBuildStatus
{
    /// <summary>Created and enqueued; the worker hasn't started cloning yet.</summary>
    public const string Queued = "queued";

    /// <summary>The worker is cloning / compiling / ingesting.</summary>
    public const string Building = "building";

    /// <summary>At least one extension compiled and the release ingested. Deliverables are downloadable.</summary>
    public const string Ready = "ready";

    /// <summary>The build failed as a whole. <see cref="ProjectBuild.FailureMessage"/> says why.</summary>
    public const string Failed = "failed";
}
