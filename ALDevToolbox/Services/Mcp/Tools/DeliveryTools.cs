using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools over SaaS delivery — the agent-facing parallel of the Releases web tool.
/// A release pipeline is the reusable "where + how" of a deploy (a build pipeline's
/// artifacts → one Business Central environment); a delivery is one run of it. Agents
/// can list release pipelines, release a successful build now, and read a pipeline's
/// delivery history with per-app outcomes. Publishing runs asynchronously in the same
/// in-process worker the web "Release now" uses, so <c>publish_build</c> returns a
/// delivery id to poll with <c>list_deliveries</c> rather than blocking to completion.
/// Access-gating and validation come from <see cref="DeliveryService"/> itself (the
/// project owner / org Admin, via <c>ProjectAccess</c>); this class only translates its
/// exceptions into <see cref="McpException"/>. All reads are org-scoped by the EF query
/// filter. Scheduling a future delivery stays a web-only surface for now. See
/// <c>.design/saas-delivery.md</c> ("MCP parity").
/// </summary>
[McpServerToolType]
public sealed class DeliveryTools
{
    private readonly DeliveryService _deliveries;
    private readonly ReleasePipelineService _releasePipelines;
    private readonly AppDbContext _db;

    public DeliveryTools(DeliveryService deliveries, ReleasePipelineService releasePipelines, AppDbContext db)
    {
        _deliveries = deliveries;
        _releasePipelines = releasePipelines;
        _db = db;
    }

    [McpServerTool(Name = "list_release_pipelines", ReadOnly = true)]
    [Description("Lists the organisation's release pipelines — each is a named 'release this build pipeline to this Business Central environment' target. Returns each pipeline's id, name, its source build pipeline, the target environment (name, Production/Sandbox type, company), version mode, and schema sync mode. Use an id with publish_build (to release a build) or list_deliveries (to see its history).")]
    public async Task<IReadOnlyList<ReleasePipelineRow>> ListReleasePipelinesAsync(
        [Description("Optional project id to list only that project's release pipelines.")] int? projectId = null,
        CancellationToken ct = default) =>
        await _releasePipelines.ListReleasePipelinesAsync(projectId, ct);

    [McpServerTool(Name = "list_deliveries", ReadOnly = true)]
    [Description("Lists a release pipeline's deliveries, newest first, with per-app outcomes. Each delivery returns its id, status ('scheduled'/'claimed'/'uploading'/'installing'/'deployed'/'failed'/'cancelled'), the build it published, scheduled/started/finished times, who triggered it, whether it was scheduled outside the environment's update window, any failure message, and each app's install result. Use it to track a publish_build call to completion.")]
    public async Task<IReadOnlyList<DeliveryHistoryRow>> ListDeliveriesAsync(
        [Description("Release pipeline id (from list_release_pipelines).")] int releasePipelineId,
        CancellationToken ct = default)
    {
        await EnsureReleasePipelineExistsAsync(releasePipelineId, ct);
        return await _deliveries.ListDeliveryHistoryAsync(releasePipelineId, ct);
    }

    [McpServerTool(Name = "publish_build", ReadOnly = false, Idempotent = false)]
    [Description("Releases a successful build to its release pipeline's Business Central environment NOW — uploads and installs the build's .app files via the automation API. The build must be a 'ready' build of the release pipeline's source build pipeline. Publishing runs in the background; this returns the new delivery's id immediately, which you poll with list_deliveries for progress (uploading → installing → deployed/failed). To schedule for later, or to release to a Production target that needs an extra confirmation, use the web UI. Requires the project owner or an org admin.")]
    public async Task<PublishBuildResult> PublishBuildAsync(
        [Description("Release pipeline id (from list_release_pipelines) — carries the target environment and modes.")] int releasePipelineId,
        [Description("Build id to publish (from list_pipeline_builds / list_project_builds) — must be a 'ready' build of this pipeline's source build pipeline.")] int buildId,
        CancellationToken ct = default)
    {
        try
        {
            var deliveryId = await _deliveries.ReleaseBuildNowAsync(releasePipelineId, buildId, ct);
            return new PublishBuildResult(
                deliveryId,
                "Delivery queued. Poll list_deliveries with this release pipeline id to watch it upload, install, and deploy (or fail).");
        }
        catch (ProjectAccessDeniedException)
        {
            throw new McpException("You don't have permission to release this project's builds — you must be the project owner or an org admin.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Couldn't release that build: " + string.Join("; ", ex.Errors.Values));
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>Throws a friendly <see cref="McpException"/> when the id isn't an active release pipeline in this org (org-scoped by the query filter), instead of silently returning an empty history.</summary>
    private async Task EnsureReleasePipelineExistsAsync(int releasePipelineId, CancellationToken ct)
    {
        var exists = await _db.OeReleasePipelines.AsNoTracking()
            .AnyAsync(r => r.Id == releasePipelineId && r.DeletedAt == null, ct);
        if (!exists)
        {
            throw new McpException($"Release pipeline {releasePipelineId} was not found. Call list_release_pipelines to see available pipelines.");
        }
    }
}

/// <summary>The outcome of a <c>publish_build</c> call — the new delivery id and how to track it.</summary>
public sealed record PublishBuildResult(int DeliveryId, string Message);
