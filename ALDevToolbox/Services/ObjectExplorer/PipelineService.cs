using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// CRUD over <see cref="Pipeline"/> — the named build configurations that belong to
/// a <see cref="Project"/>. A pipeline owns the extension selection a build runs
/// (<see cref="Pipeline.RequestedAppIdsJson"/>); a project has many. Management
/// rights come from the parent project's owner via <see cref="ProjectAccess"/>.
/// Org-scoped via the EF query filter; mutations run inside an authenticated request.
/// Validation throws <see cref="PlanValidationException"/> with field-keyed errors.
/// See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class PipelineService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<PipelineService> _logger;

    public PipelineService(AppDbContext db, IOrganizationContext orgContext, ProjectAccess access, ILogger<PipelineService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; pipeline mutation called outside an authenticated request.");

    /// <summary>
    /// True when the current user may manage <paramref name="pipelineId"/> — i.e. they
    /// may manage its parent project (owner or org Admin / SiteAdmin). False when the
    /// pipeline no longer exists.
    /// </summary>
    public async Task<bool> CanManageAsync(int pipelineId, CancellationToken ct = default)
    {
        var owner = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new { OwnerId = p.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanManageAsync(owner.OwnerId, ct);
    }

    /// <summary>Active pipelines for the current org, optionally scoped to one project, ordered by name.</summary>
    public async Task<List<Pipeline>> ListPipelinesAsync(int? projectId = null, CancellationToken ct = default)
    {
        var query = _db.OePipelines.AsNoTracking().Where(p => p.DeletedAt == null);
        if (projectId is { } pid) query = query.Where(p => p.ProjectId == pid);
        return await query.OrderBy(p => p.Name).ToListAsync(ct);
    }

    /// <summary>A single active pipeline with its project, or null when not found in this org.</summary>
    public async Task<Pipeline?> GetPipelineAsync(int id, CancellationToken ct = default)
    {
        return await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == id && p.DeletedAt == null)
            .Include(p => p.Project)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Creates a pipeline under a project. Returns the new id.</summary>
    public async Task<int> CreatePipelineAsync(PipelineInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, selectionJson) = await ValidateAsync(input, existingId: null, ct);

        var now = DateTime.UtcNow;
        var pipeline = new Pipeline
        {
            OrganizationId = orgId,
            ProjectId = input.ProjectId,
            CreatedByUserId = _orgContext.CurrentUserId,
            Name = name,
            RequestedAppIdsJson = selectionJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.OePipelines.Add(pipeline);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created pipeline {PipelineId} ({Name}) for project {ProjectId}.",
            pipeline.Id, name, input.ProjectId);
        return pipeline.Id;
    }

    /// <summary>Updates a pipeline's name and extension selection.</summary>
    public async Task UpdatePipelineAsync(int id, PipelineInput input, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Name", "This pipeline no longer exists.");

        // Validate against the pipeline's own project (input.ProjectId is ignored on
        // update — a pipeline can't move between projects).
        var (name, selectionJson) = await ValidateAsync(input with { ProjectId = pipeline.ProjectId }, existingId: id, ct);

        pipeline.Name = name;
        pipeline.RequestedAppIdsJson = selectionJson;
        pipeline.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Updated pipeline {PipelineId} ({Name}).", pipeline.Id, name);
    }

    /// <summary>Soft-deletes a pipeline. Its past builds stay reachable (their pipeline_id is nulled by the FK).</summary>
    public async Task SoftDeletePipelineAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var pipeline = await _db.OePipelines
            .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
            ?? throw Validation("Name", "This pipeline no longer exists.");

        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == pipeline.ProjectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(ownerId, ct);

        pipeline.DeletedAt = DateTime.UtcNow;
        pipeline.UpdatedAt = pipeline.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted pipeline {PipelineId}.", id);
    }

    /// <summary>
    /// Validates the input against its project (which must exist and be manageable)
    /// and the per-project name uniqueness rule. Returns the normalised name and the
    /// selection serialised to JSON (null = build everything). Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors otherwise.
    /// </summary>
    private async Task<(string Name, string? SelectionJson)> ValidateAsync(PipelineInput input, int? existingId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        // The parent project must exist in this org and be manageable by the user.
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == input.ProjectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            throw Validation("Project", "Choose a project for this pipeline.");
        }
        await _access.EnsureCanManageAsync(owner.CreatedByUserId, ct);

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Give the pipeline a name.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else
        {
            // Per-project name uniqueness among active rows (the DB enforces it via a
            // case-insensitive lower(name) index too); pre-check for a friendly error.
            var clash = await _db.OePipelines.AsNoTracking()
                .AnyAsync(p => p.DeletedAt == null
                               && p.ProjectId == input.ProjectId
                               && p.Id != (existingId ?? 0)
                               && p.Name.ToLower() == name.ToLower(), ct);
            if (clash)
            {
                errors["Name"] = "Another pipeline in this project already uses this name.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        // null/empty selection = build everything (the default), stored as a null column.
        var selectionJson = input.SelectedAppIds is { Count: > 0 }
            ? JsonSerializer.Serialize(input.SelectedAppIds)
            : null;
        return (name, selectionJson);
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>Form-post shape for a pipeline: its project, name, and the extensions it compiles (null/empty = all).</summary>
public sealed record PipelineInput(
    int ProjectId,
    string Name,
    IReadOnlyList<string>? SelectedAppIds);

/// <summary>A project choice for the "New pipeline" dialog's project picker.</summary>
public sealed record PipelineProjectOption(int Id, string Name);
