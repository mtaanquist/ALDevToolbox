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

    // LoginAttempt is intentionally excluded: it's append-only telemetry written
    // on every login/forgot-password attempt and is already its own record. An
    // audit row for each insert would just duplicate the table.
    //
    // Single source of truth — both the audit gate and the entity-type
    // discriminator read from this dictionary (#78). Adding a new audited
    // entity needs one entry here, not two.
    // NOTE: only int-keyed entities may be added here. AuditLogEntry.EntityId is
    // an int and the EntityId stamps below narrow the "Id" value to int. The
    // long-keyed types (TranslationMemory*, every Object Explorer entity) would
    // need EntityId widened to long before they could be audited. #400
    private static readonly IReadOnlyDictionary<Type, AuditEntityType> AuditedTypeMap =
        new Dictionary<Type, AuditEntityType>
        {
            [typeof(RuntimeTemplate)] = AuditEntityType.RuntimeTemplate,
            [typeof(WorkspaceExtension)] = AuditEntityType.WorkspaceExtension,
            [typeof(WorkspaceExtensionFolder)] = AuditEntityType.WorkspaceExtensionFolder,
            [typeof(WorkspaceExtensionFile)] = AuditEntityType.WorkspaceExtensionFile,
            [typeof(WorkspaceExtensionDependency)] = AuditEntityType.WorkspaceExtensionDependency,
            [typeof(ModuleExtensionFolder)] = AuditEntityType.ModuleExtensionFolder,
            [typeof(ModuleExtensionFile)] = AuditEntityType.ModuleExtensionFile,
            [typeof(RuntimeTemplateDefaultModule)] = AuditEntityType.RuntimeTemplateDefaultModule,
            [typeof(Module)] = AuditEntityType.Module,
            [typeof(ModuleDependency)] = AuditEntityType.ModuleDependency,
            [typeof(WellKnownDependency)] = AuditEntityType.WellKnownDependency,
            [typeof(ApplicationVersion)] = AuditEntityType.ApplicationVersion,
            [typeof(User)] = AuditEntityType.User,
            [typeof(SignupRequest)] = AuditEntityType.SignupRequest,
            [typeof(OrganizationSettings)] = AuditEntityType.OrganizationSettings,
            [typeof(OrganizationAsset)] = AuditEntityType.OrganizationAsset,
            [typeof(OrganizationFile)] = AuditEntityType.OrganizationFile,
            [typeof(SystemSettings)] = AuditEntityType.SystemSettings,
            [typeof(Backup)] = AuditEntityType.Backup,
            [typeof(Invite)] = AuditEntityType.Invite,
            [typeof(Recipe)] = AuditEntityType.Recipe,
            [typeof(RecipeFile)] = AuditEntityType.RecipeFile,
            [typeof(RecipeSuggestion)] = AuditEntityType.RecipeSuggestion,
            [typeof(RecipeSuggestionFile)] = AuditEntityType.RecipeSuggestionFile,
            [typeof(PersonalAccessToken)] = AuditEntityType.PersonalAccessToken,
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
            if (!AuditedTypeMap.ContainsKey(entry.Entity.GetType()))
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
                    // about to disappear. Only apply this filter to entity types that
                    // actually declare an UpdatedAt property — otherwise the predicate
                    // collapses to "no properties modified" and accidentally suppresses
                    // legitimate single-field updates on other entities.
                    if (entry.State == EntityState.Modified
                        && entry.Metadata.FindProperty(nameof(RuntimeTemplate.UpdatedAt)) is not null
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
                        // Convert (not unbox-cast) so a long-keyed entity added to
                        // AuditedTypeMap by mistake degrades to an overflow at the
                        // edge instead of an InvalidCastException unboxing long→int. #400
                        EntityId = Convert.ToInt32(entry.OriginalValues["Id"]!),
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
                // See the EntityId note above — Convert, not unbox-cast. #400
                EntityId = Convert.ToInt32(addition.Entry.CurrentValues["Id"]!),
                Action = AuditAction.Created,
                SnapshotJson = null,
            });
        }

        await ctx.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Builds the JSON "before" snapshot for a modified or deleted row. Parent
    /// entities (template, module, extension) inline their child collection's
    /// pre-save state so an investigator can read one snapshot row instead of
    /// joining several. <see cref="WorkspaceExtensionFile"/> rows replace their
    /// (potentially large) <c>Content</c> column with a SHA-256 hash so the
    /// audit log doesn't inflate with every AL file edit — see
    /// <c>.design/domain-model.md</c>.
    /// </summary>
    private static string BuildOriginalSnapshot(EntityEntry entry, List<EntityEntry> allEntries)
    {
        var snapshot = OriginalValuesToDict(entry);
        var parentId = (int)entry.OriginalValues["Id"]!;

        switch (entry.Entity)
        {
            case RuntimeTemplate:
                snapshot["extensions"] = CollectChildren<WorkspaceExtension>(allEntries, "TemplateId", parentId);
                break;
            case WorkspaceExtension:
                snapshot["folders"] = CollectChildren<WorkspaceExtensionFolder>(allEntries, "WorkspaceExtensionId", parentId);
                snapshot["dependencies"] = CollectChildren<WorkspaceExtensionDependency>(allEntries, "WorkspaceExtensionId", parentId);
                break;
            case WorkspaceExtensionFolder:
                snapshot["files"] = CollectChildren<WorkspaceExtensionFile>(allEntries, "WorkspaceExtensionFolderId", parentId);
                break;
            case ModuleExtensionFolder:
                snapshot["files"] = CollectChildren<ModuleExtensionFile>(allEntries, "ModuleExtensionFolderId", parentId);
                break;
            case Module:
                snapshot["dependencies"] = CollectChildren<ModuleDependency>(allEntries, "ModuleId", parentId);
                snapshot["extension_folders"] = CollectChildren<ModuleExtensionFolder>(allEntries, "ModuleId", parentId);
                break;
            case Recipe:
                snapshot["files"] = CollectChildren<RecipeFile>(allEntries, "RecipeId", parentId);
                break;
            case RecipeSuggestion:
                snapshot["files"] = CollectChildren<RecipeSuggestionFile>(allEntries, "RecipeSuggestionId", parentId);
                break;
        }

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    /// <summary>
    /// Pre-save snapshot of every modified-or-deleted child of <typeparamref name="TChild"/>
    /// pointing at <paramref name="parentId"/> via <paramref name="fkName"/>,
    /// ordered by the child's <c>Ordering</c> column. Six places used to
    /// inline this; one method now.
    /// </summary>
    private static List<Dictionary<string, object?>> CollectChildren<TChild>(
        IReadOnlyList<EntityEntry> entries, string fkName, int parentId) =>
        entries
            .Where(e => e.Entity is TChild
                        && e.State != EntityState.Added
                        && (int)e.OriginalValues[fkName]! == parentId)
            .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
            .Select(OriginalValuesToDict)
            .ToList();

    /// <summary>
    /// Materialises an entry's original values into a dictionary, replacing
    /// <see cref="WorkspaceExtensionFile.Content"/> with a SHA-256 hash so the audit log
    /// stays compact even when files contain large AL bodies. The encrypted
    /// SMTP password on <see cref="SystemSettings"/> is replaced with a fixed
    /// sentinel so the audit log never captures ciphertext history (which
    /// would leak structure of the protected blob).
    /// </summary>
    private static Dictionary<string, object?> OriginalValuesToDict(EntityEntry entry)
    {
        var dict = new Dictionary<string, object?>();
        var hashContent = entry.Entity is WorkspaceExtensionFile or ModuleExtensionFile or OrganizationFile;
        var hashRecipeContent = entry.Entity is RecipeFile or RecipeSuggestionFile;
        var hashAssetBytes = entry.Entity is OrganizationAsset;
        var redactSmtpPassword = entry.Entity is SystemSettings;
        // OrganizationSettings carries several encrypted secrets (the MT API key
        // and the repository-access PATs) that must never land in audit history.
        var redactOrgSecrets = entry.Entity is OrganizationSettings;
        foreach (var property in entry.OriginalValues.Properties)
        {
            var value = entry.OriginalValues[property.Name];
            if (hashContent && property.Name == nameof(WorkspaceExtensionFile.Content) && value is string s)
            {
                dict["ContentSha256"] = Sha256(s);
            }
            else if (hashRecipeContent && property.Name == nameof(RecipeFile.Content) && value is string sc)
            {
                dict["ContentSha256"] = Sha256(sc);
            }
            else if (hashAssetBytes && property.Name == nameof(OrganizationAsset.Content) && value is byte[] bytes)
            {
                dict["ContentSha256"] = Sha256Bytes(bytes);
            }
            else if (redactSmtpPassword && property.Name == nameof(SystemSettings.SmtpPasswordEncrypted))
            {
                dict[property.Name] = value is null ? null : "[redacted]";
            }
            else if (redactOrgSecrets && property.Name is
                         nameof(OrganizationSettings.MachineTranslationApiKeyEncrypted)
                         or nameof(OrganizationSettings.AzureDevOpsPatEncrypted)
                         or nameof(OrganizationSettings.GitHubPatEncrypted))
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

    private static AuditEntityType MapEntityType(Type t) =>
        AuditedTypeMap.TryGetValue(t, out var kind)
            ? kind
            : throw new InvalidOperationException($"Entity type {t.Name} is not audited.");

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
