namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One run of a <see cref="ReleasePipeline"/> — the analogue of a
/// <see cref="ProjectBuild"/> for the publish side. Created when a user releases a
/// specific build to the release pipeline's target environment; the worker then
/// uploads, installs, and polls each app via the BC automation API. The target
/// details (environment, company, modes) are <em>snapshotted</em> at creation so
/// later edits to the release pipeline don't rewrite history. Org-scoped via the
/// standard query filter. Scheduling (a future date+time, the cancel/claim race) is
/// a later slice; in this slice a delivery is created and enqueued to run
/// immediately. See <c>.design/saas-delivery.md</c> ("Delivery").
/// </summary>
public class ProjectDelivery
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project (customer) — denormalised from the release pipeline so the worker can resolve the BC credentials without a join. Rides the project's lifecycle.</summary>
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>The release pipeline this is a run of (target + modes source).</summary>
    public int ReleasePipelineId { get; set; }
    public ReleasePipeline? ReleasePipeline { get; set; }

    /// <summary>The build whose <c>.app</c> artifacts are published. Restricted: a build that's been released can't be hard-removed out from under its delivery history.</summary>
    public int ProjectBuildId { get; set; }
    public ProjectBuild? ProjectBuild { get; set; }

    /// <summary>
    /// The user who triggered the release. The worker runs under this user's captured
    /// identity. Nullable (<c>ON DELETE SET NULL</c>) so a delivery outlives the account.
    /// </summary>
    public int? TriggeredByUserId { get; set; }
    public User? TriggeredByUser { get; set; }

    // ── Snapshot of the target at creation (immune to later release-pipeline edits) ──

    /// <summary>The target environment name (keys the automation API URL).</summary>
    public string EnvironmentName { get; set; } = string.Empty;

    /// <summary>The target company id inside the environment (the apps install into this company).</summary>
    public Guid CompanyId { get; set; }

    /// <summary>The version target (automation API <c>extensionUpload.schedule</c>). One of <see cref="ReleaseVersionMode"/>.</summary>
    public string VersionMode { get; set; } = ReleaseVersionMode.CurrentVersion;

    /// <summary>The schema-sync mode (automation API <c>schemaSyncMode</c>). One of <see cref="SchemaSyncMode"/>.</summary>
    public string SchemaSyncMode { get; set; } = Entities.ObjectExplorer.SchemaSyncMode.Add;

    // ── Schedule + lifecycle ──

    /// <summary>The UTC instant the delivery is due to run. In this slice it's set to "now" (immediate); scheduling a future time is a later slice.</summary>
    public DateTime ScheduledFor { get; set; }

    /// <summary>Set when the worker atomically claims the row (status <c>scheduled</c> → <c>claimed</c>), after which it's no longer cancellable.</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>Set when the publish actually starts (first upload).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Set when the delivery reaches a terminal state (<c>deployed</c> / <c>failed</c> / <c>cancelled</c>).</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Lifecycle state. See <see cref="ProjectDeliveryStatus"/>.</summary>
    public string Status { get; set; } = ProjectDeliveryStatus.Scheduled;

    /// <summary>A short, secret-free reason when the delivery failed as a whole.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// A secret-free, human-readable log of the publish run (the per-step outcomes and
    /// trimmed API responses) for diagnostics. Never contains the token or the client
    /// secret. Null until the run starts.
    /// </summary>
    public string? DiagnosticsLog { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Per-app outcomes, in publish order.</summary>
    public ICollection<ProjectDeliveryResult> Results { get; set; } = new List<ProjectDeliveryResult>();
}

/// <summary>
/// The lifecycle states a <see cref="ProjectDelivery"/> moves through:
/// <c>scheduled → claimed → uploading → installing → deployed | failed</c>, plus
/// <c>scheduled → cancelled</c>. The transitions out of <c>scheduled</c> are atomic
/// compare-and-set so a claim and a cancel can't both win (the cancel surface is a
/// later slice; the claim is here).
/// </summary>
public static class ProjectDeliveryStatus
{
    /// <summary>Created and due; the worker hasn't claimed it yet. The only cancellable state.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The worker has taken the row; it's committed to running.</summary>
    public const string Claimed = "claimed";

    /// <summary>Uploading the <c>.app</c> bytes to the environment.</summary>
    public const string Uploading = "uploading";

    /// <summary>The apps are uploaded; BC is installing them (the deployment-status poll).</summary>
    public const string Installing = "installing";

    /// <summary>Every app installed successfully.</summary>
    public const string Deployed = "deployed";

    /// <summary>The delivery failed. <see cref="ProjectDelivery.FailureMessage"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Cancelled before a worker claimed it.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The states from which no further work happens.</summary>
    public static bool IsTerminal(string status) => status is Deployed or Failed or Cancelled;
}
