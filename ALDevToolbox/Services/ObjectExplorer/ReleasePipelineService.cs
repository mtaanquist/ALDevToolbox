using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

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
            .Select(r => new { OwnerId = r.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanManageAsync(owner.OwnerId, ct);
    }

    /// <summary>
    /// Active release pipelines for the current org, optionally scoped to one project,
    /// each with its target environment and source build-pipeline name resolved for
    /// display. Ordered by name.
    /// </summary>
    public async Task<List<ReleasePipelineRow>> ListReleasePipelinesAsync(int? projectId = null, CancellationToken ct = default)
    {
        var query = _db.OeReleasePipelines.AsNoTracking().Where(r => r.DeletedAt == null);
        if (projectId is { } pid) query = query.Where(r => r.ProjectId == pid);

        return await query
            .OrderBy(r => r.Name)
            .Select(r => new ReleasePipelineRow(
                r.Id,
                r.ProjectId,
                r.Name,
                r.BuildPipelineId,
                r.BuildPipeline!.Name,
                r.ProjectEnvironmentId,
                r.ProjectEnvironment!.Name,
                r.ProjectEnvironment.Type,
                r.ProjectEnvironment.CompanyName,
                r.ProjectEnvironment.MissingSince != null,
                r.VersionMode,
                r.SchemaSyncMode))
            .ToListAsync(ct);
    }

    /// <summary>A single active release pipeline, or null when not found in this org.</summary>
    public async Task<ReleasePipeline?> GetReleasePipelineAsync(int id, CancellationToken ct = default)
    {
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
            VersionMode = v.VersionMode,
            SchemaSyncMode = v.SchemaSyncMode,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.OeReleasePipelines.Add(pipeline);
        await _db.SaveChangesAsync(ct);

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
        pipeline.VersionMode = v.VersionMode;
        pipeline.SchemaSyncMode = v.SchemaSyncMode;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
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
        await _access.EnsureCanManageAsync(ownerId, ct);

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
    private async Task<(string Name, string VersionMode, string SchemaSyncMode)> ValidateAsync(
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
        await _access.EnsureCanManageAsync(owner.CreatedByUserId, ct);

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

        // Target environment: must belong to the same project, and must have a
        // company selected — a delivery publishes into a company, so a release
        // pipeline pointing at a company-less environment can't run.
        var environment = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == input.ProjectEnvironmentId && e.ProjectId == input.ProjectId)
            .Select(e => new { e.CompanyId })
            .FirstOrDefaultAsync(ct);
        if (environment is null)
        {
            errors["ProjectEnvironmentId"] = "Choose a target environment.";
        }
        else if (environment.CompanyId is null)
        {
            errors["ProjectEnvironmentId"] = "This environment doesn't have a company selected yet. Pick its company on the Business Central connection page, then come back.";
        }

        var versionMode = string.IsNullOrWhiteSpace(input.VersionMode) ? ReleaseVersionMode.CurrentVersion : input.VersionMode;
        if (!ReleaseVersionMode.IsValid(versionMode))
        {
            errors["VersionMode"] = "Choose how the upload targets the version.";
        }

        var schemaSyncMode = string.IsNullOrWhiteSpace(input.SchemaSyncMode) ? SchemaSyncMode.Add : input.SchemaSyncMode;
        if (!SchemaSyncMode.IsValid(schemaSyncMode))
        {
            errors["SchemaSyncMode"] = "Choose a schema sync mode.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        return (name, versionMode, schemaSyncMode);
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
    string VersionMode,
    string SchemaSyncMode);

/// <summary>List-row projection of a release pipeline with its source and target resolved for display.</summary>
public sealed record ReleasePipelineRow(
    int Id,
    int ProjectId,
    string Name,
    int BuildPipelineId,
    string BuildPipelineName,
    int ProjectEnvironmentId,
    string EnvironmentName,
    string EnvironmentType,
    string? CompanyName,
    bool EnvironmentMissing,
    string VersionMode,
    string SchemaSyncMode);
