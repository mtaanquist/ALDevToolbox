using System.Text.Json;
using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Pure-text helpers shared across admin and site-admin Razor pages. Lives
/// here so the same copy of <c>PrettyJson</c>, <c>SoftDeleteStatus</c> and
/// <c>Capitalize</c> backs every call site (#80).
/// </summary>
public static class AdminPageHelpers
{
    /// <summary>
    /// Re-renders a JSON string with indentation for inline display in an
    /// audit snapshot. Returns the original string verbatim when the input
    /// isn't valid JSON — older audit rows or partial fragments shouldn't
    /// crash the page.
    /// </summary>
    public static string PrettyJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>
    /// Status label for a soft-deletable / deprecatable row. Deleted always
    /// wins over Deprecated; both fall through to Active.
    /// </summary>
    public static string SoftDeleteStatus(DateTime? deletedAt, bool deprecated) =>
        deletedAt is not null ? "Deleted"
        : deprecated ? "Deprecated"
        : "Active";

    /// <summary>
    /// The same three states as <see cref="SoftDeleteStatus"/>, lower-cased for
    /// <c>RowStateIcon</c>. An admin table shows state as the leading glyph plus
    /// the row's edge keyline, never as a word in a column of its own — see the
    /// component's own doc comment. Deleted still wins over Deprecated.
    /// </summary>
    public static string SoftDeleteState(DateTime? deletedAt, bool deprecated) =>
        deletedAt is not null ? "deleted"
        : deprecated ? "deprecated"
        : "active";

    /// <summary>
    /// Title-cases the first letter of a single word — used to turn the
    /// lower-case bulk-action verbs ("disable", "promote") into modal copy
    /// like "Disable 3 users?".
    /// </summary>
    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Past-tense verb for an audit row, so an entry reads as a sentence
    /// ("Mads changed this template") rather than a table cell. Shared by the
    /// per-entity history panel and the two global audit logs.
    /// </summary>
    public static string AuditVerb(AuditAction action) => action switch
    {
        AuditAction.Created => "created",
        AuditAction.Deleted => "deleted",
        _ => "changed",
    };

    /// <summary>
    /// The <c>.status-pill</c> variant for an audit action. Created is a good
    /// outcome, deleted is destructive, an update is neither — amber rather
    /// than green so a row of edits does not read as a row of successes.
    /// </summary>
    public static string AuditActionPill(AuditAction action) => action switch
    {
        AuditAction.Created => "status-pill--success",
        AuditAction.Deleted => "status-pill--danger",
        _ => "status-pill--warn",
    };

    /// <summary>
    /// Display label for an audited entity type. The pre-unification values are
    /// still reachable: historical rows written before
    /// <c>20260514000000_UnifyExtensions</c> carry them.
    /// </summary>
    public static string FriendlyAuditType(AuditEntityType type) => type switch
    {
        AuditEntityType.RuntimeTemplate => "Template",
        AuditEntityType.WorkspaceExtension => "Extension",
        AuditEntityType.WorkspaceExtensionFolder => "Extension folder",
        AuditEntityType.WorkspaceExtensionFile => "Extension file",
        AuditEntityType.WorkspaceExtensionDependency => "Extension dependency",
        AuditEntityType.ModuleExtensionFolder => "Module folder",
        AuditEntityType.ModuleExtensionFile => "Module file",
        AuditEntityType.TemplateFolder => "Template folder",
        AuditEntityType.TemplateFile => "Template file",
        AuditEntityType.TemplateModuleFolder => "Template module folder",
        AuditEntityType.TemplateModuleFile => "Template module file",
        AuditEntityType.RuntimeTemplateDefaultModule => "Template default module",
        AuditEntityType.Module => "Module",
        AuditEntityType.ModuleDependency => "Module dependency",
        AuditEntityType.WellKnownDependency => "Catalogue entry",
        AuditEntityType.ReleasePipeline => "Release pipeline",
        AuditEntityType.Project => "Project connection",
        _ => type.ToString(),
    };
}
