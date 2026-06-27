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
    /// Creates an ingesting project Release for the pipeline <paramref name="pipelineId"/>
    /// and queues its build. The build compiles the pipeline's saved extension
    /// selection — copied onto the <see cref="ProjectBuild"/> row as a run-time
    /// snapshot, so the worker (and a restart-resumed job) compile the same subset
    /// even if the pipeline is later edited. Throws <see cref="PlanValidationException"/>
    /// when the pipeline/project is gone (or the project has no repositories) so the
    /// trigger UI can show the reason inline.
    /// </summary>
    public async Task<int> StartBuildAsync(int pipelineId, CancellationToken ct = default)
    {
        var pipeline = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new
            {
                p.ProjectId,
                p.RequestedAppIdsJson,
                ProjectName = p.Project!.Name,
                OwnerId = p.Project.CreatedByUserId,
                RepoCount = p.Project.Repositories.Count,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Pipeline"] = "This pipeline no longer exists.",
            });

        // Only the owner or an org Admin may trigger a build. See .design/artifacts.md.
        await _access.EnsureCanManageAsync(pipeline.OwnerId, ct).ConfigureAwait(false);

        if (pipeline.RepoCount == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Pipeline"] = "Add at least one repository to this project before building.",
            });
        }

        // Clean provisional label — just the project name. The build state shows
        // in the release's Status column ("Building…"), not the label, and
        // ProjectBuildService rewrites this to "{Project} on BC {Major}.{Minor}"
        // once the target version is known. Project-kind labels aren't unique
        // (the release id is their identity), so a concurrent rebuild doesn't
        // collide. See .design/object-explorer-project-builds.md.
        var metadata = new ReleaseImportMetadata(
            Label: pipeline.ProjectName,
            Kind: "project",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            ProjectName: pipeline.ProjectName);
        var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);

        // The first-class build row, linked to its pipeline and the release it
        // produces (the Object Explorer hook). The worker flips its status
        // building -> ready/failed and fills the commit set, changelog, logs, and
        // deliverables. The selection is snapshotted from the pipeline so editing
        // the pipeline later doesn't rewrite this build's history. See
        // .design/artifacts.md.
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing a project build.");
        var now = DateTime.UtcNow;
        _db.OeProjectBuilds.Add(new ProjectBuild
        {
            OrganizationId = orgId,
            ProjectId = pipeline.ProjectId,
            PipelineId = pipelineId,
            StartedByUserId = _orgContext.CurrentUserId,
            ReleaseId = releaseId,
            Status = ProjectBuildStatus.Queued,
            RequestedAppIdsJson = pipeline.RequestedAppIdsJson,
            StartedAt = now,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var identity = CaptureIdentity();
        var source = new ReleaseImportSource.ProjectBuild(pipeline.ProjectId);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, StoreSymbolReference: false, jobRowId), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued project build for {Project} (pipeline {PipelineId}, project {ProjectId}, release {ReleaseId}).",
            pipeline.ProjectName, pipelineId, pipeline.ProjectId, releaseId);
        return releaseId;
    }

    private AmbientOrganizationScope.OrganizationIdentity CaptureIdentity() => new(
        OrganizationId: _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing a project build."),
        UserId: _orgContext.CurrentUserId,
        IsSiteAdmin: _orgContext.IsSiteAdmin,
        IsSystemOrganization: _orgContext.IsSystemOrganization);
}
