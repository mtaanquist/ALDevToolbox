using System.ComponentModel;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
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
/// project owner / org Admin / an assigned team, via <c>ProjectAccess</c>); this class
/// only translates its exceptions into <see cref="McpException"/>. All reads are
/// org-scoped by the EF query filter and project-scoped by the same authority — a
/// Private project the caller has no grant on is absent from every list here and
/// unresolvable by id. Scheduling a future delivery stays a web-only surface for now. See
/// <c>.design/saas-delivery.md</c> ("MCP parity").
/// </summary>
[McpServerToolType]
public sealed class DeliveryTools
{
    private readonly DeliveryService _deliveries;
    private readonly ReleasePipelineService _releasePipelines;
    private readonly GitHubReleaseService _githubReleases;
    private readonly ArtifactService _artifacts;

    public DeliveryTools(
        DeliveryService deliveries,
        ReleasePipelineService releasePipelines,
        GitHubReleaseService githubReleases,
        ArtifactService artifacts)
    {
        _deliveries = deliveries;
        _releasePipelines = releasePipelines;
        _githubReleases = githubReleases;
        _artifacts = artifacts;
    }

    [McpServerTool(Name = "list_release_pipelines", ReadOnly = true)]
    [Description("Lists the release pipelines you can see in the organisation — each is a named 'release this build pipeline to this Business Central environment' target. Returns each pipeline's id, name, its owning solution (id and name), its source build pipeline, the target environment (name, Production/Sandbox type, company, and whether it is still present in Business Central), when installs run (its deployment schedule), and schema sync mode. Pipelines under a private solution you are not on the team for are not listed. Use an id with publish_build (to release a build) or list_deliveries (to see its history).")]
    public async Task<IReadOnlyList<ReleasePipelineRow>> ListReleasePipelinesAsync(
        [Description("Optional solution id to list only that solution's release pipelines.")] int? solutionId = null,
        CancellationToken ct = default)
    {
        try
        {
            return await _releasePipelines.ListReleasePipelinesAsync(solutionId, ct);
        }
        catch (ProjectAccessDeniedException)
        {
            // Same answer as an id that isn't there — see
            // ReleasePipelineService.EnsureReleasePipelineExistsAsync.
            throw new McpException($"Solution {solutionId} does not exist in this organisation.");
        }
    }

    [McpServerTool(Name = "list_deliveries", ReadOnly = true)]
    [Description("Lists a release pipeline's deliveries, newest first, with per-app outcomes. Each delivery returns its id, status ('scheduled'/'claimed'/'uploading'/'installing'/'deployed'/'failed'/'cancelled'/'handed_off', the last meaning Business Central accepted the apps and will install them on its own schedule), the build it published, scheduled/started/finished times, who triggered it, whether it was scheduled outside the environment's update window, any failure message, and each app's install result. Use it to track a publish_build call to completion.")]
    public async Task<IReadOnlyList<DeliveryHistoryRow>> ListDeliveriesAsync(
        [Description("Release pipeline id (from list_release_pipelines).")] int releasePipelineId,
        CancellationToken ct = default)
    {
        await _releasePipelines.EnsureReleasePipelineExistsAsync(releasePipelineId, ct);
        return await _deliveries.ListDeliveryHistoryAsync(releasePipelineId, ct);
    }

    [McpServerTool(Name = "publish_build", ReadOnly = false, Idempotent = false)]
    [Description("Releases a successful build to its release pipeline's Business Central environment NOW — uploads and installs the build's .app files via the automation API. The build must be a 'ready' build of the release pipeline's source build pipeline. Publishing runs in the background; this returns the new delivery's id immediately, which you poll with list_deliveries for progress (uploading → installing → deployed/failed). To schedule for later, or to release to a Production target that needs an extra confirmation, use the web UI. Requires the solution owner or an org admin.")]
    public async Task<PublishBuildResult> PublishBuildAsync(
        [Description("Release pipeline id (from list_release_pipelines) — carries the target environment and modes.")] int releasePipelineId,
        [Description("Build id to publish (from list_pipeline_builds / list_solution_builds) — must be a 'ready' build of this pipeline's source build pipeline.")] int buildId,
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
            throw new McpException("You don't have permission to release this solution's builds — you must be the solution owner or an org admin.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Couldn't release that build: " + string.Join("; ", ex.Errors.Values));
        }
    }

    [McpServerTool(Name = "list_github_releases", ReadOnly = true)]
    [Description("Lists the GitHub releases a release pipeline can install, newest first, with each release's tag, title, publication date and the app files attached to it. Only works for a release pipeline whose apps come from a repository's GitHub releases - one that releases a build pipeline's builds is refused, and you should use list_pipeline_builds for that. Releases with no app files attached cannot be installed. Requires the solution owner or an org admin.")]
    public async Task<IReadOnlyList<GitHubReleaseOption>> ListGitHubReleasesAsync(
        [Description("Release pipeline id (from list_release_pipelines).")] int releasePipelineId,
        CancellationToken ct = default)
    {
        await _releasePipelines.EnsureReleasePipelineExistsAsync(releasePipelineId, ct);
        try
        {
            return await _githubReleases.ListReleasesAsync(releasePipelineId, ct);
        }
        catch (ProjectAccessDeniedException)
        {
            throw new McpException("You don't have permission to read this solution's releases — you must be the solution owner or an org admin.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Couldn't list the releases: " + string.Join("; ", ex.Errors.Values));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused to list the releases: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            // No GitHub App on this deployment. An agent gets the same sentence a
            // person sees, rather than a stack trace it can do nothing with.
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "stage_github_release", ReadOnly = false, Idempotent = true)]
    [Description("Downloads the app files attached to one GitHub release and records them as a build, so publish_build can install them into the release pipeline's Business Central environment. Nothing is installed yet — this only fetches the files. Staging the same release twice returns the build already recorded rather than fetching it again. Refused when the release pipeline does not draw from GitHub releases, when the tag no longer exists, or when the release has no app files attached. Requires the solution owner or an org admin.")]
    public async Task<BuildRow> StageGitHubReleaseAsync(
        [Description("Release pipeline id (from list_release_pipelines) — says which repository the release is read from.")] int releasePipelineId,
        [Description("The release's tag, exactly as list_github_releases reports it (for example 'v1.2.3.0').")] string tag,
        CancellationToken ct = default)
    {
        await _releasePipelines.EnsureReleasePipelineExistsAsync(releasePipelineId, ct);
        try
        {
            var buildId = await _githubReleases.StageReleaseAsync(releasePipelineId, tag, ct);
            return await _artifacts.GetBuildRowAsync(buildId, ct)
                ?? throw new McpException($"Release {tag} was staged as build {buildId}, but the build could not be read back.");
        }
        catch (ProjectAccessDeniedException)
        {
            throw new McpException("You don't have permission to release this solution's builds — you must be the solution owner or an org admin.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Couldn't stage that release: " + string.Join("; ", ex.Errors.Values));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

/// <summary>The outcome of a <c>publish_build</c> call — the new delivery id and how to track it.</summary>
public sealed record PublishBuildResult(int DeliveryId, string Message);
