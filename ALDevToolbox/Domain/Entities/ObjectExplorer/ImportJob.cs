using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// Durable record of one queued / running / completed release import. Backs the
/// in-memory <see cref="Services.ObjectExplorer.ReleaseImportQueue"/> so a
/// container restart doesn't strand work: the startup reconciler re-enqueues
/// every <c>queued</c> / <c>running</c> row whose payload survived (URL
/// imports) and marks the rest <c>failed</c> with an explanation so the admin
/// knows to re-submit.
///
/// <para>
/// Staged-zip jobs reference a temp file under the container's local
/// <c>/tmp</c>, which is gone after a restart. We persist these rows anyway so
/// the audit trail is complete and the failure message can name the cause;
/// they're never resumed automatically.
/// </para>
/// </summary>
public class ImportJob
{
    public long Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// The Release row this job is ingesting into. Created in <c>ingesting</c>
    /// state at the same time as this row so both reach <c>ready</c> /
    /// <c>failed</c> together.
    /// </summary>
    public int ReleaseId { get; set; }

    /// <summary>
    /// User who submitted the import. Carried separately from <see cref="OrganizationId"/>
    /// so the worker can re-enter the submitter's <see cref="Services.AmbientOrganizationScope"/>
    /// after a restart — same identity model the in-memory queue captures today.
    /// </summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SiteAdmin flag at submit time. Mirrors <see cref="Services.AmbientOrganizationScope.OrganizationIdentity"/>.</summary>
    public bool IsSiteAdmin { get; set; }

    /// <summary>System-org flag at submit time. Mirrors <see cref="Services.AmbientOrganizationScope.OrganizationIdentity"/>.</summary>
    public bool IsSystemOrganization { get; set; }

    /// <summary>One of <c>url</c>, <c>staged_zip</c>. See <see cref="Services.ObjectExplorer.ReleaseImportSource"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Customer to build for <c>kind=customer_build</c>. The id is the whole
    /// payload — a restart re-clones HEAD and rebuilds — so unlike the staged
    /// uploads, customer builds resume cleanly. Null for every other kind.
    /// </summary>
    public int? CustomerId { get; set; }

    /// <summary>Download URL for <c>kind=url</c>. Validated against the allow-list at submit time.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Container-local temp path for <c>kind=staged_zip</c>. Not portable across restarts.</summary>
    public string? StagedZipPath { get; set; }

    /// <summary>True when the staged zip is a DVD subset (Applications/Extensions + System.app).</summary>
    public bool? StagedIsDvd { get; set; }

    /// <summary>Mirrors <c>storeSymbolReference</c> on <see cref="Services.ObjectExplorer.ReleaseImportService.ProcessReleaseAsync"/>.</summary>
    public bool StoreSymbolReference { get; set; }

    /// <summary>One of <c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c>.</summary>
    public string Status { get; set; } = "queued";

    /// <summary>Reason text on <c>failed</c> rows. Surfaced on the admin page.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
