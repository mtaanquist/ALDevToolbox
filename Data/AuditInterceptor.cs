using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ALDevToolbox.Data;

/// <summary>
/// Writes an <see cref="AuditLogEntry"/> for every change to the entities listed in
/// <see cref="AuditedTypes"/>. The interceptor is scoped (it shares the lifetime of the
/// <see cref="AppDbContext"/> it intercepts) so per-request state — the pending list of
/// created entities — never leaks between concurrent SaveChanges calls.
///
/// Rationale and snapshot rules: see <c>.design/auth-and-audit.md</c>. Modified and
/// deleted rows snapshot their <c>OriginalValues</c> before the save. Created rows
/// don't have a "before", so their <c>SnapshotJson</c> is null and the row is written
/// in <see cref="SavedChangesAsync"/> after the database has assigned the primary key.
/// </summary>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly HashSet<Type> AuditedTypes = new()
    {
        typeof(RuntimeTemplate),
        typeof(TemplateFolder),
        typeof(Module),
        typeof(ModuleDependency),
        typeof(WellKnownDependency),
    };

    private readonly IHttpContextAccessor _http;
    private List<PendingAddition> _pendingAdditions = new();

    public AuditInterceptor(IHttpContextAccessor http)
    {
        _http = http;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var ctx = eventData.Context;
        if (ctx is null)
        {
            return new ValueTask<InterceptionResult<int>>(result);
        }

        // Reset on every save so we never carry rows across two SaveChanges calls.
        _pendingAdditions = new List<PendingAddition>();

        var changedBy = ResolveChangedBy();
        var timestamp = DateTime.UtcNow;
        var entries = ctx.ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            if (!AuditedTypes.Contains(entry.Entity.GetType()))
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    // We can't write the audit row yet — the primary key hasn't been
                    // assigned. Stash the entry and emit the row in SavedChangesAsync.
                    _pendingAdditions.Add(new PendingAddition(entry, timestamp, changedBy));
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    var action = entry.State == EntityState.Modified
                        ? AuditAction.Updated
                        : AuditAction.Deleted;
                    var snapshot = BuildOriginalSnapshot(entry, entries);
                    ctx.Add(new AuditLogEntry
                    {
                        Timestamp = timestamp,
                        ChangedBy = changedBy,
                        EntityType = MapEntityType(entry.Entity.GetType()),
                        EntityId = (int)entry.OriginalValues["Id"]!,
                        Action = action,
                        SnapshotJson = snapshot,
                    });
                    break;
            }
        }

        return new ValueTask<InterceptionResult<int>>(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        // Take a local snapshot and replace the field. The follow-up SaveChanges below
        // re-enters SavingChangesAsync which resets the field; iterating against a
        // captured local keeps that interaction safe.
        var pending = _pendingAdditions;
        _pendingAdditions = new List<PendingAddition>();

        if (pending.Count == 0)
        {
            return result;
        }

        var ctx = eventData.Context;
        if (ctx is null)
        {
            return result;
        }

        foreach (var addition in pending)
        {
            ctx.Add(new AuditLogEntry
            {
                Timestamp = addition.Timestamp,
                ChangedBy = addition.ChangedBy,
                EntityType = MapEntityType(addition.Entry.Entity.GetType()),
                EntityId = (int)addition.Entry.CurrentValues["Id"]!,
                Action = AuditAction.Created,
                SnapshotJson = null,
            });
        }

        await ctx.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Builds the JSON "before" snapshot for a modified or deleted row. Parent
    /// entities (template, module) inline their child collection's pre-save state so
    /// an investigator can read one snapshot row instead of joining several.
    /// </summary>
    private static string BuildOriginalSnapshot(EntityEntry entry, List<EntityEntry> allEntries)
    {
        var snapshot = OriginalValuesToDict(entry);

        if (entry.Entity is RuntimeTemplate)
        {
            var templateId = (int)entry.OriginalValues["Id"]!;
            snapshot["folders"] = allEntries
                .Where(e => e.Entity is TemplateFolder
                            && e.State != EntityState.Added
                            && (int)e.OriginalValues["TemplateId"]! == templateId)
                .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
                .Select(OriginalValuesToDict)
                .ToList();
        }
        else if (entry.Entity is Module)
        {
            var moduleId = (int)entry.OriginalValues["Id"]!;
            snapshot["dependencies"] = allEntries
                .Where(e => e.Entity is ModuleDependency
                            && e.State != EntityState.Added
                            && (int)e.OriginalValues["ModuleId"]! == moduleId)
                .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
                .Select(OriginalValuesToDict)
                .ToList();
        }

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static Dictionary<string, object?> OriginalValuesToDict(EntityEntry entry)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var property in entry.OriginalValues.Properties)
        {
            dict[property.Name] = entry.OriginalValues[property.Name];
        }
        return dict;
    }

    private static AuditEntityType MapEntityType(Type t)
    {
        if (t == typeof(RuntimeTemplate)) return AuditEntityType.RuntimeTemplate;
        if (t == typeof(TemplateFolder)) return AuditEntityType.TemplateFolder;
        if (t == typeof(Module)) return AuditEntityType.Module;
        if (t == typeof(ModuleDependency)) return AuditEntityType.ModuleDependency;
        if (t == typeof(WellKnownDependency)) return AuditEntityType.WellKnownDependency;
        throw new InvalidOperationException($"Entity type {t.Name} is not audited.");
    }

    private string ResolveChangedBy()
    {
        var name = _http.HttpContext?.User?.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private sealed record PendingAddition(EntityEntry Entry, DateTime Timestamp, string ChangedBy);
}
