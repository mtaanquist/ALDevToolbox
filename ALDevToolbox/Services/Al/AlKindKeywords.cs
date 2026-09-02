using System;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Shared translation between the kind hints extraction sites pass around
/// (AL type keywords like <c>Record</c> / <c>Codeunit</c>, the
/// <c>Database::</c> typed-literal prefix, or an already-canonical catalog
/// kind) and the catalog kind values stored on
/// <c>oe_module_objects.kind</c>.
///
/// Lives here rather than inside the resolver because the extractor's test
/// doubles must rank candidates exactly the way the production
/// <c>CatalogResolver</c> does — otherwise a kind-disambiguation bug
/// (issue #712) can't produce a failing extractor test.
/// </summary>
public static class AlKindKeywords
{
    /// <summary>
    /// Maps a caller's kind hint to a catalog kind, or null when there is
    /// no usable hint. Two keywords don't pass through unchanged:
    /// <c>Record</c> (the AL keyword for a table-typed variable) and
    /// <c>Database</c> (the typed-literal prefix in
    /// <c>Database::"Customer"</c>) both mean <c>table</c>. Everything
    /// else differs only in casing.
    /// </summary>
    public static string? MapKeywordToKind(string? keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return null;
        var lower = keyword.ToLowerInvariant();
        // `Database::"X"` and `Record X` both name a table. Before this
        // mapping existed, `Database` produced the non-existent catalog
        // kind "database", every candidate missed the exact-kind bucket,
        // and `Database::User` in Base App landed on the User CODEUNIT.
        if (lower is "record" or "database") return "table";
        return lower;
    }

    /// <summary>
    /// True for the extension kinds (<c>tableextension</c>,
    /// <c>pageextension</c>, …). A name shared between a base object and
    /// an extension of it should resolve to the base when the caller gave
    /// no kind hint.
    /// </summary>
    public static bool IsExtensionKind(string kind) =>
        kind.EndsWith("extension", StringComparison.OrdinalIgnoreCase);
}
