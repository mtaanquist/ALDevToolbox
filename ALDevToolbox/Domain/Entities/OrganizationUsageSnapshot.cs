namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Pre-computed per-organisation storage footprint, refreshed on a schedule by
/// <c>UsageSnapshotScheduler</c>. Computing usage live means a sequential
/// <c>COUNT(*)</c> over every tenanted table (including the multi-million-row
/// Object Explorer tables), which on a populated tenant costs hundreds of
/// milliseconds — far too expensive to run on every authenticated navigation,
/// which is what the sidebar <c>StorageBar</c> used to do. This row caches the
/// measured bytes so the bar (and the SiteAdmin storage page) read a single
/// indexed lookup instead.
///
/// <para>
/// Stores only the *measured* quantities (logical + index bytes); the billable
/// size, effective quota and used fraction are derived at read time from the
/// current <c>system_settings</c> / org quota, so a quota change takes effect
/// immediately without waiting for the next recompute.
/// </para>
///
/// <para>
/// Cross-org infrastructure, not tenant content: like <see cref="Backup"/> it
/// carries no query filter and is reached only through
/// <c>DatabaseUsageService</c>. The scheduler writes it off-request (no org
/// context), so it must not depend on the tenant filter.
/// </para>
/// </summary>
public class OrganizationUsageSnapshot
{
    /// <summary>FK to the organisation, and the primary key — one snapshot per org.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Measured logical (user-data) bytes, prorated by row share. See <c>DatabaseUsageService</c>.</summary>
    public long LogicalBytes { get; set; }

    /// <summary>Measured index/metadata bytes, prorated by row share.</summary>
    public long IndexBytes { get; set; }

    /// <summary>UTC instant this row was last recomputed. Surfaced as "updated N ago" on the SiteAdmin page.</summary>
    public DateTime ComputedAt { get; set; }
}
