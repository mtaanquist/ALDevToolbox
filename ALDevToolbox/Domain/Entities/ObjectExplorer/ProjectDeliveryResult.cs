namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// The outcome of publishing one <c>.app</c> within a <see cref="ProjectDelivery"/> —
/// the per-app analogue of <see cref="ProjectBuildResult"/>. Records the app's
/// identity, the BC <c>extensionUpload</c> id the run created for it, the deployment
/// result, and a short secret-free message. Org-scoped (denormalised from the parent
/// delivery). See <c>.design/saas-delivery.md</c> ("Delivery").
/// </summary>
public class ProjectDeliveryResult
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the delivery). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectDeliveryId { get; set; }
    public ProjectDelivery? ProjectDelivery { get; set; }

    /// <summary>Publish order (dependency order), 0-based — so the history reads in the order the apps were sent.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// The extension's app.json <c>id</c> (GUID string), when known. Null in this slice:
    /// a build's <see cref="ProjectBuildArtifact"/> records the app's name + version but
    /// not its app.json id, and the publish + deployment-status match on name + version.
    /// Reserved so a later slice can backfill it (e.g. from the build's release modules).
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>The extension's display name (app.json <c>name</c>).</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>The published version (app.json <c>version</c>).</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>The BC <c>extensionUpload</c> id created for this app, once the upload is started. Null before that / on an early failure.</summary>
    public string? ExtensionUploadId { get; set; }

    /// <summary>Per-app lifecycle. See <see cref="ProjectDeliveryResultStatus"/>.</summary>
    public string Status { get; set; } = ProjectDeliveryResultStatus.Pending;

    /// <summary>A short, secret-free message — the BC deployment status detail, or the failure reason.</summary>
    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The per-app states within a delivery.</summary>
public static class ProjectDeliveryResultStatus
{
    /// <summary>Queued behind earlier apps; not started.</summary>
    public const string Pending = "pending";

    /// <summary>The <c>.app</c> bytes are being uploaded.</summary>
    public const string Uploading = "uploading";

    /// <summary>Uploaded; BC is installing it.</summary>
    public const string Installing = "installing";

    /// <summary>Installed successfully.</summary>
    public const string Completed = "completed";

    /// <summary>This app failed to upload or install.</summary>
    public const string Failed = "failed";

    /// <summary>Not attempted because an earlier app in the run failed.</summary>
    public const string Skipped = "skipped";
}
