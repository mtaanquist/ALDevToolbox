using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Coordinates starting a project build: create the <c>ingesting</c> project
/// Release row synchronously (so it shows in the list immediately) and enqueue a
/// <see cref="ReleaseImportSource.ProjectBuild"/> job for the worker to clone /
/// compile / ingest off-thread. Mirrors <see cref="ArtifactReleaseImporter"/>; the
/// heavy lifting lives in <see cref="ProjectBuildService"/>, run by
/// <see cref="ReleaseImportWorker"/>.
///
/// <para>
/// The Release starts with a provisional label — <c>"{Project} (building…)"</c> —
/// because the real BC version isn't known until the build reads the repos'
/// <c>app.json</c>. The build service finalises the label once it resolves the
/// target version. Always <c>project</c> kind.
/// </para>
/// </summary>
public sealed class ProjectBuildImporter
{
    private readonly ReleaseImportService _importer;
    private readonly ReleaseImportQueue _queue;
    private readonly PersistedImportJobs _persistedJobs;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<ProjectBuildImporter> _logger;

    public ProjectBuildImporter(
        ReleaseImportService importer,
        ReleaseImportQueue queue,
        PersistedImportJobs persistedJobs,
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        ILogger<ProjectBuildImporter> logger)
    {
        _importer = importer;
        _queue = queue;
        _persistedJobs = persistedJobs;
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// Creates an ingesting project Release for <paramref name="projectId"/> and
    /// queues its build. <paramref name="selectedAppIds"/> is the set of app-id GUIDs
    /// the user picked in the "New build" dialog; <c>null</c> (or empty) means build
    /// every discovered extension. The selection is persisted on the
    /// <see cref="ProjectBuild"/> row so the worker (and a restart-resumed job)
    /// compiles the same subset. Throws <see cref="PlanValidationException"/> when the
    /// project doesn't exist (or has no repositories) so the trigger UI can show the
    /// reason inline.
    /// </summary>
    public async Task<int> StartBuildAsync(int projectId, IReadOnlyList<string>? selectedAppIds = null, CancellationToken ct = default)
    {
        var project = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new { c.Name, c.CreatedByUserId, RepoCount = c.Repositories.Count })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Project"] = "This project no longer exists.",
            });

        // Only the owner or an org Admin may trigger a build. See .design/artifacts.md.
        await _access.EnsureCanManageAsync(project.CreatedByUserId, ct).ConfigureAwait(false);

        if (project.RepoCount == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Project"] = "Add at least one repository to this project before building.",
            });
        }

        // Clean provisional label — just the project name. The build state shows
        // in the release's Status column ("Building…"), not the label, and
        // ProjectBuildService rewrites this to "{Project} on BC {Major}.{Minor}"
        // once the target version is known. Project-kind labels aren't unique
        // (the release id is their identity), so a concurrent rebuild of the same
        // project doesn't collide. See .design/object-explorer-project-builds.md.
        var metadata = new ReleaseImportMetadata(
            Label: project.Name,
            Kind: "project",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            ProjectName: project.Name);
        var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);

        // The first-class build row, linked to the release it produces (the Object
        // Explorer hook). The worker flips its status building -> ready/failed and
        // fills the commit set, changelog, logs, and deliverables. See
        // .design/artifacts.md.
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing a project build.");
        var now = DateTime.UtcNow;
        // null/empty selection = build everything (the default). A non-empty pick is
        // stored as a JSON array of app-ids; ProjectBuildService reads it back off the
        // build row and narrows the discovered set before compiling.
        var requestedAppIdsJson = selectedAppIds is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(selectedAppIds)
            : null;
        _db.OeProjectBuilds.Add(new ProjectBuild
        {
            OrganizationId = orgId,
            ProjectId = projectId,
            StartedByUserId = _orgContext.CurrentUserId,
            ReleaseId = releaseId,
            Status = ProjectBuildStatus.Queued,
            RequestedAppIdsJson = requestedAppIdsJson,
            StartedAt = now,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var identity = CaptureIdentity();
        var source = new ReleaseImportSource.ProjectBuild(projectId);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, StoreSymbolReference: false, jobRowId), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued project build for {Project} (project {ProjectId}, release {ReleaseId}).",
            project.Name, projectId, releaseId);
        return releaseId;
    }

    private AmbientOrganizationScope.OrganizationIdentity CaptureIdentity() => new(
        OrganizationId: _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing a project build."),
        UserId: _orgContext.CurrentUserId,
        IsSiteAdmin: _orgContext.IsSiteAdmin,
        IsSystemOrganization: _orgContext.IsSystemOrganization);
}
