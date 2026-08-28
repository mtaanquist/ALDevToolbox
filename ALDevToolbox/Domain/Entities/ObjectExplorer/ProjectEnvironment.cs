namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A Business Central SaaS environment belonging to a <see cref="Project"/>
/// (customer), fetched from the BC Admin Center API and cached so release
/// pipelines can target it without re-typing a name. Refresh is a <em>stable
/// upsert</em> keyed by <c>(ProjectId, Name)</c> — the row id and the picked
/// <see cref="CompanyId"/> survive a refresh so a release pipeline's FK never
/// dangles. An environment the customer has since deleted is not hard-removed
/// (a release pipeline may still point at it); it is stamped
/// <see cref="MissingSince"/> and surfaced as "no longer present". Org-scoped.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public class ProjectEnvironment
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>The environment name (e.g. <c>Production</c>) — keys the automation API URL and, with <see cref="ProjectId"/>, identifies the row across refreshes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Environment type as reported by the Admin Center API (e.g. <c>Production</c> / <c>Sandbox</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The chosen company's GUID, fetched from the automation API for this environment. Null until a company is picked. Preserved across refreshes.</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>The chosen company's display name, for showing the selection without a re-fetch. Null until a company is picked.</summary>
    public string? CompanyName { get; set; }

    /// <summary>When this environment was last seen in a fetch.</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>Set when a refresh no longer returns this environment (the customer deleted it). Cleared if it reappears. The row is retained so any release pipeline pointing at it can show "no longer present" rather than break. Distinct from <see cref="SoftDeletedOn"/>: a soft-deleted environment still comes back from the API, a hard-deleted one vanishes from it.</summary>
    public DateTime? MissingSince { get; set; }

    // ── Fetched detail from the Admin Center API ──────────────────────────────
    // All nullable and all rewritten by every refresh. Enum-ish values are stored
    // verbatim as the API returned them — Microsoft's casing differs per endpoint,
    // so nothing here is normalised. Rows fetched before these columns existed carry
    // nulls until the next Refresh.

    /// <summary>The environment's display name in the admin center. Often equals <see cref="Name"/>.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>The application family the environment belongs to, verbatim from the API. Used to address it in later admin-center calls instead of assuming one.</summary>
    public string? ApplicationFamily { get; set; }

    /// <summary>Lifecycle status (<c>Active</c>, <c>Upgrading</c>, <c>SoftDeleted</c>, ...). A delivery is refused when this isn't publishable; see <c>BcEnvironmentStatus</c>.</summary>
    public string? Status { get; set; }

    /// <summary>When <see cref="Status"/> was last read. A status is only as good as its age, and the delivery run re-reads it live before uploading.</summary>
    public DateTime? StatusFetchedAt { get; set; }

    /// <summary>The environment's country/localisation code (e.g. <c>DK</c>).</summary>
    public string? CountryCode { get; set; }

    /// <summary>The Entra tenant the environment actually lives in — catches a connection pointed at the wrong tenant.</summary>
    public Guid? AadTenantId { get; set; }

    /// <summary>Deep link to the environment's web client, behind the "Open in Business Central" action.</summary>
    public string? WebClientLoginUrl { get; set; }

    /// <summary>Azure region the environment runs in.</summary>
    public string? LocationName { get; set; }

    /// <summary>Azure geography. The by-name response omits it, so a live re-read leaves the cached value alone.</summary>
    public string? GeoName { get; set; }

    /// <summary>The update ring the environment is on.</summary>
    public string? RingName { get; set; }

    /// <summary>How AppSource app updates are applied to this environment.</summary>
    public string? AppSourceAppsUpdateCadence { get; set; }

    /// <summary>The environment's Business Central version.</summary>
    public string? Version { get; set; }

    /// <summary>Start of the grace period before Microsoft enforces the next major update.</summary>
    public DateTime? GracePeriodStartDate { get; set; }

    /// <summary>When Microsoft starts enforcing the next update.</summary>
    public DateTime? EnforcedUpdatePeriodStartDate { get; set; }

    /// <summary>When the customer soft-deleted the environment. It still returns from the API until hard deletion; see <see cref="MissingSince"/>.</summary>
    public DateTime? SoftDeletedOn { get; set; }

    /// <summary>When the soft-deleted environment is scheduled to be removed for good.</summary>
    public DateTime? HardDeletePendingOn { get; set; }

    /// <summary>Why the environment was deleted, as reported by the API.</summary>
    public string? DeleteReason { get; set; }

    /// <summary>
    /// Start of the recurring daily <em>update window</em> — the time of day this
    /// environment prefers to receive deliveries, in the project's
    /// <see cref="Project.BcTimeZone"/>. Mirrors BC's own admin-center environment
    /// update window. <c>null</c> (with <see cref="UpdateWindowEnd"/>) means "no window
    /// — deliver any time" (the normal Sandbox case). It is a <strong>default, not a
    /// lock</strong>: it seeds the prefilled schedule time; the user can override.
    /// User config, preserved across refreshes. See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public TimeOnly? UpdateWindowStart { get; set; }

    /// <summary>End of the daily update window (may wrap past midnight, e.g. 22:00–06:00). Null together with <see cref="UpdateWindowStart"/> = no window.</summary>
    public TimeOnly? UpdateWindowEnd { get; set; }
}
