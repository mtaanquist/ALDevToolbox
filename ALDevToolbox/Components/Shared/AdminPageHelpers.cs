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
    /// Title-cases the first letter of a single word — used to turn the
    /// lower-case bulk-action verbs ("disable", "promote") into modal copy
    /// like "Disable 3 users?".
    /// </summary>
    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
