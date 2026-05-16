using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ALDevToolbox.Services;

/// <summary>
/// Computes per-organisation database disk usage by combining
/// <c>pg_total_relation_size</c> and <c>pg_indexes_size</c> over the tables
/// enumerated in <see cref="TenantTableCatalog"/>, prorated by row count.
///
/// The result is an approximation: PostgreSQL does not track per-tenant
/// bytes natively. Tables with unusually wide rows for a single org can
/// over- or under-shoot reality. For the SiteAdmin storage view and the
/// quota guard this is accurate enough; if it becomes a billing dispute
/// surface, switch to schema-per-tenant or per-table TOAST inspection.
/// </summary>
public sealed class DatabaseUsageService
{
    private readonly AppDbContext _db;
    private readonly SystemSettingsService _systemSettings;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<DatabaseUsageService> _logger;

    public DatabaseUsageService(
        AppDbContext db,
        SystemSettingsService systemSettings,
        IOrganizationContext orgContext,
        ILogger<DatabaseUsageService> logger)
    {
        _db = db;
        _systemSettings = systemSettings;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>Per-org usage row for the SiteAdmin storage page.</summary>
    public sealed record OrgUsageRow(
        int OrganizationId,
        string Name,
        string Slug,
        bool IsSystem,
        long LogicalBytes,
        long IndexBytes,
        long TotalBytes,
        long BillableBytes,
        int? EffectiveQuotaMb,
        int? OrgOverrideMb,
        int? SystemDefaultMb,
        decimal Multiplier,
        double? UsedFraction);

    /// <summary>
    /// Cross-organisation usage listing. SiteAdmin-only — bypasses the EF
    /// query filter by reading the orgs table with
    /// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>.
    /// </summary>
    public async Task<IReadOnlyList<OrgUsageRow>> ListAsync(CancellationToken ct)
    {
        var settings = await _systemSettings.GetViewAsync(ct);
        var multiplier = settings.IndexSizeMultiplier;
        var systemDefault = settings.DefaultStorageQuotaMb;

        var orgs = await _db.Organizations.IgnoreQueryFilters()
            .OrderBy(o => o.IsSystem ? 0 : 1).ThenBy(o => o.Name)
            .Select(o => new { o.Id, o.Name, o.Slug, o.IsSystem, o.StorageQuotaMb })
            .ToListAsync(ct);

        var tableSizes = await ReadTableSizesAsync(ct);
        var perOrgCounts = await ReadPerOrgRowCountsAsync(orgs.Select(o => o.Id).ToList(), ct);

        var rows = new List<OrgUsageRow>(orgs.Count);
        foreach (var org in orgs)
        {
            long logical = 0, index = 0;
            foreach (var table in TenantTableCatalog.AllTenantedTables)
            {
                if (!tableSizes.TryGetValue(table, out var size)) continue;
                if (size.TotalRows == 0) continue;
                if (!perOrgCounts.TryGetValue((org.Id, table), out var orgRows) || orgRows == 0) continue;

                var share = (double)orgRows / size.TotalRows;
                logical += (long)(size.LogicalBytes * share);
                index += (long)(size.IndexBytes * share);
            }

            var total = logical + index;
            var billable = logical + (long)(index * (double)multiplier);
            var effectiveQuota = org.StorageQuotaMb ?? systemDefault;
            double? usedFraction = effectiveQuota is { } q && q > 0
                ? (double)billable / (q * 1024d * 1024d)
                : null;

            rows.Add(new OrgUsageRow(
                org.Id, org.Name, org.Slug, org.IsSystem,
                logical, index, total, billable,
                effectiveQuota, org.StorageQuotaMb, systemDefault,
                multiplier, usedFraction));
        }

        return rows;
    }

    /// <summary>Usage row for the current organisation (sidebar bar + dashboard tile).</summary>
    public async Task<OrgUsageRow?> GetForCurrentOrgAsync(CancellationToken ct)
    {
        var orgId = _orgContext.CurrentOrganizationId;
        if (orgId is null) return null;

        var settings = await _systemSettings.GetViewAsync(ct);
        var org = await _db.Organizations.IgnoreQueryFilters()
            .Where(o => o.Id == orgId.Value)
            .Select(o => new { o.Id, o.Name, o.Slug, o.IsSystem, o.StorageQuotaMb })
            .FirstOrDefaultAsync(ct);
        if (org is null) return null;

        var tableSizes = await ReadTableSizesAsync(ct);
        var perOrgCounts = await ReadPerOrgRowCountsAsync([orgId.Value], ct);

        long logical = 0, index = 0;
        foreach (var table in TenantTableCatalog.AllTenantedTables)
        {
            if (!tableSizes.TryGetValue(table, out var size)) continue;
            if (size.TotalRows == 0) continue;
            if (!perOrgCounts.TryGetValue((orgId.Value, table), out var orgRows) || orgRows == 0) continue;
            var share = (double)orgRows / size.TotalRows;
            logical += (long)(size.LogicalBytes * share);
            index += (long)(size.IndexBytes * share);
        }

        var total = logical + index;
        var billable = logical + (long)(index * (double)settings.IndexSizeMultiplier);
        var effectiveQuota = org.StorageQuotaMb ?? settings.DefaultStorageQuotaMb;
        double? usedFraction = effectiveQuota is { } q && q > 0
            ? (double)billable / (q * 1024d * 1024d)
            : null;

        return new OrgUsageRow(
            org.Id, org.Name, org.Slug, org.IsSystem,
            logical, index, total, billable,
            effectiveQuota, org.StorageQuotaMb, settings.DefaultStorageQuotaMb,
            settings.IndexSizeMultiplier, usedFraction);
    }

    /// <summary>
    /// Persists a per-organisation quota override. SiteAdmin only. Setting
    /// the value to <see langword="null"/> reverts to the system default.
    /// </summary>
    public async Task SetOrgQuotaAsync(int organizationId, int? quotaMb, CancellationToken ct)
    {
        if (quotaMb is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quotaMb), "Quota must be non-negative.");
        }
        var org = await _db.Organizations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct)
            ?? throw new InvalidOperationException($"Organization {organizationId} not found.");
        org.StorageQuotaMb = quotaMb;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("SiteAdmin set storage quota for {OrgSlug} to {QuotaMb} MB.",
            org.Slug, quotaMb?.ToString() ?? "(default)");
    }

    private async Task<Dictionary<string, (long LogicalBytes, long IndexBytes, long TotalRows)>>
        ReadTableSizesAsync(CancellationToken ct)
    {
        // pg_class.reltuples is the planner's row estimate — fast but stale
        // until ANALYZE runs. Adequate for prorating disk bytes; the goal is
        // billing-relevant, not row-exact.
        var sql = """
            SELECT c.relname AS table_name,
                   pg_indexes_size(c.oid) AS index_bytes,
                   pg_total_relation_size(c.oid) - pg_indexes_size(c.oid) AS logical_bytes,
                   c.reltuples::bigint AS estimated_rows
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            """;

        var result = new Dictionary<string, (long, long, long)>();
        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var indexBytes = reader.GetInt64(1);
            var logicalBytes = reader.GetInt64(2);
            var rows = reader.GetInt64(3);
            result[name] = (Math.Max(0, logicalBytes), Math.Max(0, indexBytes), Math.Max(0, rows));
        }
        return result;
    }

    private async Task<Dictionary<(int OrgId, string Table), long>> ReadPerOrgRowCountsAsync(
        IReadOnlyList<int> orgIds, CancellationToken ct)
    {
        var counts = new Dictionary<(int, string), long>();
        if (orgIds.Count == 0) return counts;

        var connection = _db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);

        // Direct organization_id tables.
        foreach (var table in TenantTableCatalog.AllTenantedTables
                     .Where(TenantTableCatalog.TablesWithDirectOrgColumn.Contains))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT organization_id, COUNT(*) FROM {table} "
                              + "WHERE organization_id = ANY(@ids) GROUP BY organization_id";
            var param = cmd.CreateParameter();
            param.ParameterName = "@ids";
            ((NpgsqlParameter)param).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer;
            param.Value = orgIds.ToArray();
            cmd.Parameters.Add(param);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (await reader.IsDBNullAsync(0, ct)) continue;
                counts[(reader.GetInt32(0), table)] = reader.GetInt64(1);
            }
        }

        // Auth-adjacent tables joined through users.
        foreach (var (table, userIdColumn) in TenantTableCatalog.TablesLinkedThroughUser)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT u.organization_id, COUNT(*) FROM {table} t "
                              + $"JOIN users u ON u.id = t.{userIdColumn} "
                              + "WHERE u.organization_id = ANY(@ids) GROUP BY u.organization_id";
            var param = cmd.CreateParameter();
            param.ParameterName = "@ids";
            ((NpgsqlParameter)param).NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer;
            param.Value = orgIds.ToArray();
            cmd.Parameters.Add(param);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (await reader.IsDBNullAsync(0, ct)) continue;
                counts[(reader.GetInt32(0), table)] = reader.GetInt64(1);
            }
        }

        return counts;
    }

    private static async Task EnsureOpenAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }
    }
}
