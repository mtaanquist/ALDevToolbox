using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read- and write-side service for the curated application-version catalogue
/// introduced in Milestone P2.4. The catalogue is a flat list edited the same
/// way as the well-known dependency catalogue: the admin submits the entire
/// ordered list, the service reconciles it row-by-row by primary key, and rows
/// missing from the input are soft-deleted so existing template foreign keys
/// stay resolvable.
/// </summary>
public class ApplicationVersionService
{
    private static readonly Regex KeyRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);
    private static readonly Regex ApplicationVersionRegex = new(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.Compiled);
    private static readonly Regex RuntimeFormatRegex = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ILogger<ApplicationVersionService> _logger;

    public ApplicationVersionService(AppDbContext db, ILogger<ApplicationVersionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns every active (non-deleted) catalogue entry in ordering order.
    /// Drives the user-facing select on the New Workspace / New Extension forms.
    /// </summary>
    public Task<List<ApplicationVersion>> GetActiveAsync(bool includeDeprecated = false, CancellationToken ct = default)
    {
        var query = _db.ApplicationVersions
            .AsNoTracking()
            .Where(a => a.DeletedAt == null);
        if (!includeDeprecated)
        {
            query = query.Where(a => !a.Deprecated);
        }
        return query
            .OrderBy(a => a.Ordering)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns every catalogue row, including soft-deleted ones. Drives the
    /// admin editor.
    /// </summary>
    public Task<List<ApplicationVersion>> GetAllForAdminAsync(CancellationToken ct = default)
    {
        return _db.ApplicationVersions
            .AsNoTracking()
            .OrderBy(a => a.DeletedAt == null ? 0 : 1)
            .ThenBy(a => a.Ordering)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    /// <summary>Lookup by stable URL-safe key. Used by the seed/TOML round-trip.</summary>
    public Task<ApplicationVersion?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        return _db.ApplicationVersions
            .AsNoTracking()
            .Where(a => a.Key == key)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Replaces the catalogue with the supplied list. Existing rows match on
    /// <see cref="ApplicationVersionInput.Id"/>; rows whose id is null are
    /// inserted; rows missing from the input are soft-deleted (so templates
    /// pointing at them keep resolving for the audit log).
    /// </summary>
    public async Task SaveAsync(IReadOnlyList<ApplicationVersionInput> inputs, CancellationToken ct = default)
    {
        await ValidateAsync(inputs, ct);

        var existing = await _db.ApplicationVersions.ToListAsync(ct);
        var existingById = existing.ToDictionary(e => e.Id);
        var inputIds = inputs.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();

        var now = DateTime.UtcNow;

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var key = input.Key.Trim();
            var name = input.Name.Trim();
            var application = input.Application.Trim();
            var runtime = input.Runtime.Trim();

            if (input.Id is int id && existingById.TryGetValue(id, out var row))
            {
                row.Key = key;
                row.Name = name;
                row.Application = application;
                row.Runtime = runtime;
                row.Ordering = i;
                row.Deprecated = input.Deprecated;
                // Re-saving a row implicitly restores it from soft-delete.
                row.DeletedAt = null;
                row.UpdatedAt = now;
            }
            else
            {
                _db.ApplicationVersions.Add(new ApplicationVersion
                {
                    Key = key,
                    Name = name,
                    Application = application,
                    Runtime = runtime,
                    Ordering = i,
                    Deprecated = input.Deprecated,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        // Soft-delete rows that fell off the input list. Hard-delete would
        // cascade to NULL on every template's FK, but we want the audit log
        // to retain the row's identity so a curious admin can chase what
        // happened to a referenced version. Already-deleted rows are left
        // alone so we don't churn DeletedAt timestamps.
        foreach (var row in existing)
        {
            if (!inputIds.Contains(row.Id) && row.DeletedAt is null)
            {
                row.DeletedAt = now;
                row.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved application-version catalogue: {Count} active entries.",
            inputs.Count);
    }

    private async Task ValidateAsync(IReadOnlyList<ApplicationVersionInput> inputs, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-load existing rows so we can check for cross-row key collisions
        // against rows that aren't in the input (soft-deleted entries).
        var existing = await _db.ApplicationVersions
            .AsNoTracking()
            .Select(a => new { a.Id, a.Key })
            .ToListAsync(ct);
        var existingByKey = existing.ToDictionary(
            e => e.Key,
            e => e.Id,
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var key = input.Key?.Trim() ?? string.Empty;
            var name = input.Name?.Trim() ?? string.Empty;
            var application = input.Application?.Trim() ?? string.Empty;
            var runtime = input.Runtime?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(key))
            {
                errors[$"Entries[{i}].Key"] = "Key is required.";
            }
            else if (!KeyRegex.IsMatch(key))
            {
                errors[$"Entries[{i}].Key"] = "Key must contain only lowercase letters, digits, and hyphens.";
            }
            else if (!seenKeys.Add(key))
            {
                errors[$"Entries[{i}].Key"] = $"Duplicate key '{key}'.";
            }
            else if (existingByKey.TryGetValue(key, out var ownerId) && ownerId != input.Id)
            {
                errors[$"Entries[{i}].Key"] =
                    $"A different application-version row already uses key '{key}'.";
            }

            if (string.IsNullOrEmpty(name))
            {
                errors[$"Entries[{i}].Name"] = "Name is required.";
            }

            if (string.IsNullOrEmpty(application))
            {
                errors[$"Entries[{i}].Application"] = "Application version is required.";
            }
            else if (!ApplicationVersionRegex.IsMatch(application))
            {
                errors[$"Entries[{i}].Application"] =
                    "Must be a four-part version (e.g. 24.0.0.0).";
            }

            if (string.IsNullOrEmpty(runtime))
            {
                errors[$"Entries[{i}].Runtime"] = "Runtime is required.";
            }
            else if (!RuntimeFormatRegex.IsMatch(runtime))
            {
                errors[$"Entries[{i}].Runtime"] =
                    "Runtime must be a number, optionally with one minor part (e.g. 15 or 15.2).";
            }
        }

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }
}

/// <summary>
/// One row submitted by the admin application-version editor. <see cref="Id"/>
/// is null for new rows; otherwise it carries the persisted primary key so the
/// service can reconcile the existing row in place.
/// </summary>
public record ApplicationVersionInput(
    int? Id,
    string Key,
    string Name,
    string Application,
    string Runtime,
    bool Deprecated);
