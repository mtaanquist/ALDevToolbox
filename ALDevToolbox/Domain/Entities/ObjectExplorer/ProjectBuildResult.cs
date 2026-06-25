namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One extension's outcome from a project build. A project Release can ingest
/// several apps compiled from the project's repos; this row records, per app,
/// whether it compiled and ingested or failed (and why). The manage page reads
/// these to show a per-app build report and a "partial" badge when some apps
/// failed while others made it into the Release. See
/// <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public class ProjectBuildResult
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project <see cref="Release"/> this build produced. Rows are reaped with the release.</summary>
    public int ReleaseId { get; set; }
    public Release? Release { get; set; }

    /// <summary>The extension's app.json <c>name</c> (best-effort label for the report).</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>The extension's app.json <c>id</c> (GUID), or empty when the manifest couldn't be read.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>One of <c>compiled</c>, <c>ingested</c>, <c>failed</c>. See <see cref="ProjectBuildResultStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Human-readable detail — the compiler/clone error on a <c>failed</c> row, else null.</summary>
    public string? Message { get; set; }

    /// <summary>The clone URL of the repository this extension was built from. Null when the source repo couldn't be determined.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>The full Git commit SHA the extension was built from (HEAD of the cloned repo at build time). Provenance for a future Artifacts surface.</summary>
    public string? CommitSha { get; set; }

    /// <summary>The committer date of <see cref="CommitSha"/> (UTC). Lets the UI show how fresh the built source was.</summary>
    public DateTime? CommitDate { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>The lifecycle states a <see cref="ProjectBuildResult"/> row can hold.</summary>
public static class ProjectBuildResultStatus
{
    /// <summary>The app compiled to a <c>.app</c> but ingest hasn't been confirmed (transient).</summary>
    public const string Compiled = "compiled";

    /// <summary>The app compiled and its <c>.app</c> was handed to the Release importer.</summary>
    public const string Ingested = "ingested";

    /// <summary>The app could not be cloned, resolved, or compiled. <see cref="ProjectBuildResult.Message"/> says why.</summary>
    public const string Failed = "failed";
}
