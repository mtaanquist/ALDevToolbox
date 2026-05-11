using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
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
    private static readonly JsonSerializerOptions JsonOptions = PersistenceJson.Options;

    private static readonly HashSet<Type> AuditedTypes = new()
    {
        typeof(RuntimeTemplate),
        typeof(TemplateFolder),
        typeof(TemplateFile),
        typeof(TemplateModuleFolder),
        typeof(TemplateModuleFile),
        typeof(RuntimeTemplateDefaultModule),
        typeof(Module),
        typeof(ModuleDependency),
        typeof(WellKnownDependency),
        typeof(ApplicationVersion),
        typeof(User),
        typeof(SignupRequest),
        typeof(OrganizationSettings),
        typeof(OrganizationAsset),
        typeof(OrganizationFile),
        typeof(SystemSettings),
        typeof(Backup),
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
        var changedByUserId = ResolveUserId();
        var organizationId = ResolveOrganizationId();
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
                    _pendingAdditions.Add(new PendingAddition(entry, timestamp, changedBy, changedByUserId, organizationId));
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    // The reconciliation services rewrite UpdatedAt unconditionally on
                    // every save, so an admin clicking Save with no real edits would
                    // otherwise add a noise row to the audit log. Treat "only UpdatedAt
                    // changed" as a no-op for audit purposes; deletions always pass
                    // through because their OriginalValues represent the row that's
                    // about to disappear.
                    if (entry.State == EntityState.Modified
                        && !entry.Properties.Any(p => p.IsModified && p.Metadata.Name != nameof(RuntimeTemplate.UpdatedAt)))
                    {
                        break;
                    }
                    var action = entry.State == EntityState.Modified
                        ? AuditAction.Updated
                        : AuditAction.Deleted;
                    var snapshot = BuildOriginalSnapshot(entry, entries);
                    ctx.Add(new AuditLogEntry
                    {
                        Timestamp = timestamp,
                        ChangedBy = changedBy,
                        ChangedByUserId = changedByUserId,
                        OrganizationId = ResolveEntityOrganizationId(entry, organizationId),
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
                ChangedByUserId = addition.ChangedByUserId,
                OrganizationId = ResolveEntityOrganizationId(addition.Entry, addition.OrganizationId),
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
    /// <see cref="TemplateFile"/> rows replace their (potentially large)
    /// <c>Content</c> column with a SHA-256 hash so the audit log doesn't
    /// inflate with every AL file edit — see <c>.design/domain-model.md</c>.
    /// </summary>
    private static string BuildOriginalSnapshot(EntityEntry entry, List<EntityEntry> allEntries)
    {
        var snapshot = OriginalValuesToDict(entry);

        if (entry.Entity is RuntimeTemplate)
        {
            var templateId = (int)entry.OriginalValues["Id"]!;
            var folders = allEntries
                .Where(e => e.Entity is TemplateFolder
                            && e.State != EntityState.Added
                            && (int)e.OriginalValues["TemplateId"]! == templateId)
                .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
                .ToList();
            snapshot["folders"] = folders.Select(folder =>
            {
                var folderDict = OriginalValuesToDict(folder);
                var folderId = (int)folder.OriginalValues["Id"]!;
                folderDict["files"] = allEntries
                    .Where(e => e.Entity is TemplateFile
                                && e.State != EntityState.Added
                                && (int)e.OriginalValues["TemplateFolderId"]! == folderId)
                    .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
                    .Select(OriginalValuesToDict)
                    .ToList();
                return folderDict;
            }).ToList();

            var moduleFolders = allEntries
                .Where(e => e.Entity is TemplateModuleFolder
                            && e.State != EntityState.Added
                            && (int)e.OriginalValues["TemplateId"]! == templateId)
                .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
                .ToList();
            snapshot["module_folders"] = moduleFolders.Select(folder =>
            {
                var folderDict = OriginalValuesToDict(folder);
                var folderId = (int)folder.OriginalValues["Id"]!;
                folderDict["files"] = allEntries
                    .Where(e => e.Entity is TemplateModuleFile
                                && e.State != EntityState.Added
                                && (int)e.OriginalValues["TemplateModuleFolderId"]! == folderId)
                    .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
                    .Select(OriginalValuesToDict)
                    .ToList();
                return folderDict;
            }).ToList();
        }
        else if (entry.Entity is TemplateFolder)
        {
            var folderId = (int)entry.OriginalValues["Id"]!;
            snapshot["files"] = allEntries
                .Where(e => e.Entity is TemplateFile
                            && e.State != EntityState.Added
                            && (int)e.OriginalValues["TemplateFolderId"]! == folderId)
                .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
                .Select(OriginalValuesToDict)
                .ToList();
        }
        else if (entry.Entity is TemplateModuleFolder)
        {
            var folderId = (int)entry.OriginalValues["Id"]!;
            snapshot["files"] = allEntries
                .Where(e => e.Entity is TemplateModuleFile
                            && e.State != EntityState.Added
                            && (int)e.OriginalValues["TemplateModuleFolderId"]! == folderId)
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

    /// <summary>
    /// Materialises an entry's original values into a dictionary, replacing
    /// <see cref="TemplateFile.Content"/> with a SHA-256 hash so the audit log
    /// stays compact even when files contain large AL bodies. The encrypted
    /// SMTP password on <see cref="SystemSettings"/> is replaced with a fixed
    /// sentinel so the audit log never captures ciphertext history (which
    /// would leak structure of the protected blob).
    /// </summary>
    private static Dictionary<string, object?> OriginalValuesToDict(EntityEntry entry)
    {
        var dict = new Dictionary<string, object?>();
        var hashContent = entry.Entity is TemplateFile or TemplateModuleFile or OrganizationFile;
        var hashAssetBytes = entry.Entity is OrganizationAsset;
        var redactSmtpPassword = entry.Entity is SystemSettings;
        foreach (var property in entry.OriginalValues.Properties)
        {
            var value = entry.OriginalValues[property.Name];
            if (hashContent && property.Name == nameof(TemplateFile.Content) && value is string s)
            {
                dict["ContentSha256"] = Sha256(s);
            }
            else if (hashAssetBytes && property.Name == nameof(OrganizationAsset.Content) && value is byte[] bytes)
            {
                dict["ContentSha256"] = Sha256Bytes(bytes);
            }
            else if (redactSmtpPassword && property.Name == nameof(SystemSettings.SmtpPasswordEncrypted))
            {
                dict[property.Name] = value is null ? null : "[redacted]";
            }
            else
            {
                dict[property.Name] = value;
            }
        }
        return dict;
    }

    private static string Sha256Bytes(byte[] value)
    {
        var bytes = SHA256.HashData(value);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static AuditEntityType MapEntityType(Type t)
    {
        if (t == typeof(RuntimeTemplate)) return AuditEntityType.RuntimeTemplate;
        if (t == typeof(TemplateFolder)) return AuditEntityType.TemplateFolder;
        if (t == typeof(TemplateFile)) return AuditEntityType.TemplateFile;
        if (t == typeof(TemplateModuleFolder)) return AuditEntityType.TemplateModuleFolder;
        if (t == typeof(TemplateModuleFile)) return AuditEntityType.TemplateModuleFile;
        if (t == typeof(RuntimeTemplateDefaultModule)) return AuditEntityType.RuntimeTemplateDefaultModule;
        if (t == typeof(Module)) return AuditEntityType.Module;
        if (t == typeof(ModuleDependency)) return AuditEntityType.ModuleDependency;
        if (t == typeof(WellKnownDependency)) return AuditEntityType.WellKnownDependency;
        if (t == typeof(ApplicationVersion)) return AuditEntityType.ApplicationVersion;
        if (t == typeof(User)) return AuditEntityType.User;
        if (t == typeof(SignupRequest)) return AuditEntityType.SignupRequest;
        if (t == typeof(OrganizationSettings)) return AuditEntityType.OrganizationSettings;
        if (t == typeof(OrganizationAsset)) return AuditEntityType.OrganizationAsset;
        if (t == typeof(OrganizationFile)) return AuditEntityType.OrganizationFile;
        if (t == typeof(SystemSettings)) return AuditEntityType.SystemSettings;
        if (t == typeof(Backup)) return AuditEntityType.Backup;
        throw new InvalidOperationException($"Entity type {t.Name} is not audited.");
    }

    /// <summary>
    /// Composes <c>"display_name &lt;email&gt;"</c> for the audit row when both
    /// claims are present, falling back to the display name alone, then to
    /// <c>"unknown"</c> for seed-time inserts (no HttpContext).
    /// </summary>
    private string ResolveChangedBy()
    {
        var principal = _http.HttpContext?.User;
        var name = principal?.Identity?.Name;
        var email = principal?.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email))
        {
            return $"{name} <{email}>";
        }
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private int? ResolveUserId()
    {
        var value = _http.HttpContext?.User?.FindFirst(HttpOrganizationContext.UserIdClaim)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    private int? ResolveOrganizationId()
    {
        var value = _http.HttpContext?.User?.FindFirst(HttpOrganizationContext.OrganizationIdClaim)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// Pulls the entity's own <c>OrganizationId</c> if it has one, falling
    /// back to the request-scoped value. Audited types like
    /// <see cref="AuditLogEntry"/>'s parent (<c>users</c> or seed-time
    /// inserts) may not have a column to read.
    /// </summary>
    private static int? ResolveEntityOrganizationId(EntityEntry entry, int? fallback)
    {
        var values = entry.State == EntityState.Added ? entry.CurrentValues : entry.OriginalValues;
        if (values.Properties.Any(p => p.Name == "OrganizationId"))
        {
            var value = values["OrganizationId"];
            if (value is int orgId && orgId > 0) return orgId;
        }
        return fallback;
    }

    private sealed record PendingAddition(
        EntityEntry Entry,
        DateTime Timestamp,
        string ChangedBy,
        int? ChangedByUserId,
        int? OrganizationId);
}
