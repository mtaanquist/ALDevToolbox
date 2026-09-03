using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// CRUD over <see cref="ReleasePipeline"/> — the reusable "where + how" of a deploy
/// that draws a <see cref="Pipeline"/> (build) pipeline's artifacts and targets one
/// <see cref="ProjectEnvironment"/>. A build pipeline can feed several release
/// pipelines (build-once-deploy-many). Management rights come from the parent
/// project's owner via <see cref="ProjectAccess"/>. Org-scoped via the EF query
/// filter; mutations run inside an authenticated request. Validation throws
/// <see cref="PlanValidationException"/> with field-keyed errors. Scheduling a
/// delivery and the publish flow itself land in a later slice. See
/// <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ReleasePipelineService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<ReleasePipelineService> _logger;

    public ReleasePipelineService(AppDbContext db, IOrganizationContext orgContext, ProjectAccess access, ILogger<ReleasePipelineService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; release-pipeline mutation called outside an authenticated request.");

    /// <summary>
    /// True when the current user may manage <paramref name="releasePipelineId"/> — i.e.
    /// they may manage its parent project (owner or org Admin / SiteAdmin). False when
    /// the release pipeline no longer exists.
    /// </summary>
    public async Task<bool> CanManageAsync(int releasePipelineId, CancellationToken ct = default)
    {
        var owner = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId && r.DeletedAt == null)
            .Select(r => new { r.ProjectId, OwnerId = r.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanManageAsync(owner.ProjectId, owner.OwnerId, ct);
    }

    /// <summary>
    /// Active release pipelines for the current org, optionally scoped to one project,
    /// each with its target environment and source build-pipeline name resolved for
    /// display. Ordered by name.
    /// </summary>
    public async Task<List<ReleasePipelineRow>> ListReleasePipelinesAsync(int? projectId = null, CancellationToken ct = default)
    {
        var query = _db.OeReleasePipelines.AsNoTracking().Where(r => r.DeletedAt == null);
        if (projectId is { } pid)
        {
            await _access.EnsureCanViewAsync(pid, ct);
            query = query.Where(r => r.ProjectId == pid);
        }
        else
        {
            // A release pipeline inherits its project's visibility.
            var visible = ProjectAccess.VisibleProjectPredicate(await _access.GetSnapshotAsync(ct));
            query = query.Where(r => _db.OeProjects.Where(visible).Any(v => v.Id == r.ProjectId));
        }

        return await query
            .OrderBy(r => r.Name)
            .Select(r => new ReleasePipelineRow(
                r.Id,
                r.ProjectId,
                r.Project!.Name,
                r.Name,
                r.BuildPipelineId,
                r.BuildPipeline!.Name,
                r.ProjectEnvironmentId,
                r.ProjectEnvironment!.Name,
                r.ProjectEnvironment.Type,
                r.ProjectEnvironment.MissingSince != null,
                r.DeploymentSchedule,
                r.SchemaSyncMode))
            .ToListAsync(ct);
    }

    /// <summary>A single active release pipeline, or null when not found in this org.</summary>
    public async Task<ReleasePipeline?> GetReleasePipelineAsync(int id, CancellationToken ct = default)
    {
        await EnsureCanViewReleasePipelineAsync(id, ct);
        return await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == id && r.DeletedAt == null)
            .Include(r => r.Project)
            .Include(r => r.BuildPipeline)
            .Include(r => r.ProjectEnvironment)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Creates a release pipeline under a project. Returns the new id.</summary>
    public async Task<int> CreateReleasePipelineAsync(ReleasePipelineInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var v = await ValidateAsync(input, existingId: null, ct);

        var now = DateTime.UtcNow;
        var pipeline = new ReleasePipeline
        {
            OrganizationId = orgId,
            ProjectId = input.ProjectId,
            CreatedByUserId = _orgContext.CurrentUserId,
            Name = v.Name,
            BuildPipelineId = input.BuildPipelineId,
            ProjectEnvironmentId = input.ProjectEnvironmentId,
            DeploymentSchedule = v.DeploymentSchedule,
            SchemaSyncMode = v.SchemaSyncMode,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.OeReleasePipelines.Add(pipeline);
        await SaveTranslatingNameClashAsync(ct);

        _logger.LogInformation("Created release pipeline {ReleasePipelineId} ({Name}) for project {ProjectId} → environment {EnvironmentId}.",
            pipeline.Id, v.Name, input.ProjectId, input.ProjectEnvironmentId);
        return pipeline.Id;
    }

    /// <summary>Updates a release pipeline's name, source, target, and modes.</summary>
    public async Task UpdateReleasePipelineAsync(int id, ReleasePipelineInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OeReleasePipelines
            .FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct)
            ?? throw Validation("Name", "This release pipeline no longer exists.");

        // A release pipeline can't move between projects; validate against its own.
        var v = await ValidateAsync(input with { ProjectId = pipeline.ProjectId }, existingId: id, ct);

        pipeline.Name = v.Name;
        pipeline.BuildPipelineId = input.BuildPipelineId;
        pipeline.ProjectEnvironmentId = input.ProjectEnvironmentId;
        pipeline.DeploymentSchedule = v.DeploymentSchedule;
        pipeline.SchemaSyncMode = v.SchemaSyncMode;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await SaveTranslatingNameClashAsync(ct);
        _logger.LogInformation("Updated release pipeline {ReleasePipelineId} ({Name}).", pipeline.Id, v.Name);
    }

    /// <summary>Soft-deletes a release pipeline.</summary>
    public async Task SoftDeleteReleasePipelineAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OeReleasePipelines
            .FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct)
            ?? throw Validation("Name", "This release pipeline no longer exists.");

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == pipeline.ProjectId)
            .Select(p => p.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(pipeline.ProjectId, ownerId, ct);

        pipeline.DeletedAt = DateTime.UtcNow;
        pipeline.UpdatedAt = pipeline.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted release pipeline {ReleasePipelineId}.", id);
    }

    /// <summary>
    /// Validates the input against its project (which must exist and be manageable),
    /// the per-project name uniqueness rule, the source build pipeline and target
    /// environment (both must belong to the same project, and the environment must
    /// have a company picked so a delivery can actually publish), and the version /
    /// schema-sync modes. Returns the normalised values. Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors otherwise.
    /// </summary>
    private async Task<(string Name, string DeploymentSchedule, string SchemaSyncMode)> ValidateAsync(
        ReleasePipelineInput input, int? existingId, CancellationToken ct)
    {
        // The parent project must exist in this org and be manageable by the user.
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == input.ProjectId && p.DeletedAt == null)
            .Select(p => new { p.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            throw Validation("Project", "Choose a project for this release pipeline.");
        }
        await _access.EnsureCanManageAsync(input.ProjectId, owner.CreatedByUserId, ct);

        var errors = new Dictionary<string, string>();

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Give the release pipeline a name.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else
        {
            var clash = await _db.OeReleasePipelines.AsNoTracking()
                .AnyAsync(r => r.DeletedAt == null
                               && r.ProjectId == input.ProjectId
                               && r.Id != (existingId ?? 0)
                               && r.Name.ToLower() == name.ToLower(), ct);
            if (clash)
            {
                errors["Name"] = "Another release pipeline in this project already uses this name.";
            }
        }

        // Source build pipeline: must be an active pipeline in the same project.
        var buildPipelineOk = await _db.OePipelines.AsNoTracking()
            .AnyAsync(p => p.Id == input.BuildPipelineId
                           && p.DeletedAt == null
                           && p.ProjectId == input.ProjectId, ct);
        if (!buildPipelineOk)
        {
            errors["BuildPipelineId"] = "Choose a build pipeline to release from.";
        }

        // Target environment: must belong to the same project, must still be there, and
        // must be in a state that can take an install. The status is the cached one — the
        // live re-read happens when a delivery actually runs — so this is the same refusal
        // the user would hit later, just earlier and while they can still change it.
        var environment = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == input.ProjectEnvironmentId && e.ProjectId == input.ProjectId)
            .Select(e => new { e.Name, e.Status, Missing = e.MissingSince != null })
            .FirstOrDefaultAsync(ct);
        if (environment is null)
        {
            errors["ProjectEnvironmentId"] = "Choose a target environment.";
        }
        else if (environment.Missing)
        {
            errors["ProjectEnvironmentId"] =
                $"'{environment.Name}' is no longer present in Business Central. Refresh the environments on the project's Business Central page, then come back.";
        }
        else if (BcEnvironmentStatus.RefusalMessage(environment.Name, environment.Status) is { } statusRefusal)
        {
            errors["ProjectEnvironmentId"] = statusRefusal;
        }

        // Only the wire values Business Central still accepts pass. A pipeline saved
        // under the retired upload API stores wording this one rejects, so re-saving such
        // a pipeline means picking again rather than silently carrying the old value over.
        var deploymentSchedule = string.IsNullOrWhiteSpace(input.DeploymentSchedule)
            ? BcDeploymentSchedule.Immediate
            : input.DeploymentSchedule;
        if (!BcDeploymentSchedule.Pickable.Contains(deploymentSchedule))
        {
            errors["DeploymentSchedule"] = "Choose when installs should run.";
        }

        var schemaSyncMode = string.IsNullOrWhiteSpace(input.SchemaSyncMode) ? BcSyncMode.Add : input.SchemaSyncMode;
        if (!BcSyncMode.IsValid(schemaSyncMode))
        {
            errors["SchemaSyncMode"] = "Choose a schema sync setting.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        return (name, deploymentSchedule, schemaSyncMode);
    }

    /// <summary>
    /// Gates a release-pipeline-keyed read on its project's visibility. One that
    /// doesn't exist passes; the read below returns nothing on its own.
    /// </summary>
    private async Task EnsureCanViewReleasePipelineAsync(int releasePipelineId, CancellationToken ct)
    {
        var projectId = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId)
            .Select(r => (int?)r.ProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is { } id) await _access.EnsureCanViewAsync(id, ct);
    }

    /// <summary>
    /// Saves, turning the name-uniqueness backstop into the same field-keyed error
    /// the pre-check gives. The pre-check reads before this writes, so two
    /// concurrent saves can leave one to be caught by the case-insensitive unique
    /// index — and that has to read as an inline message, not a 500. See #702.
    /// </summary>
    private async Task SaveTranslatingNameClashAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw Validation("Name", "Another release pipeline in this project already uses this name.");
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>Form-post shape for a release pipeline: project, name, source build pipeline, target environment, and modes.</summary>
public sealed record ReleasePipelineInput(
    int ProjectId,
    string Name,
    int BuildPipelineId,
    int ProjectEnvironmentId,
    string DeploymentSchedule,
    string SchemaSyncMode);

/// <summary>List-row projection of a release pipeline with its source and target resolved for display.</summary>
public sealed record ReleasePipelineRow(
    int Id,
    int ProjectId,
    /// <summary>
    /// The owning project's name. Added when the Releases browser moved onto the
    /// list archetype: its table needs a Project column, and until this existed
    /// the page could only link the literal word "Project".
    /// </summary>
    string ProjectName,
    string Name,
    int BuildPipelineId,
    string BuildPipelineName,
    int ProjectEnvironmentId,
    string EnvironmentName,
    string EnvironmentType,
    bool EnvironmentMissing,
    string DeploymentSchedule,
    string SchemaSyncMode);
