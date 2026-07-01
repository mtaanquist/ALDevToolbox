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

    /// <summary>Set when a refresh no longer returns this environment (the customer deleted it). Cleared if it reappears. The row is retained so any release pipeline pointing at it can show "no longer present" rather than break.</summary>
    public DateTime? MissingSince { get; set; }

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
