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

    /// <summary>The branch built (provenance label). Default branch / HEAD for now — see "Out of scope" in the design doc.</summary>
    public string? Branch { get; set; }

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

    public DateTime StartedAt { get; set; }

    /// <summary>When the build reached a terminal state (<c>ready</c> / <c>failed</c>); null while in flight.</summary>
    public DateTime? FinishedAt { get; set; }

    public ICollection<ProjectBuildRepoCommit> RepoCommits { get; set; } = new List<ProjectBuildRepoCommit>();
    public ICollection<ProjectBuildCommit> Changelog { get; set; } = new List<ProjectBuildCommit>();
    public ICollection<ProjectBuildArtifact> Artifacts { get; set; } = new List<ProjectBuildArtifact>();
    public ICollection<ProjectBuildLog> Logs { get; set; } = new List<ProjectBuildLog>();
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
