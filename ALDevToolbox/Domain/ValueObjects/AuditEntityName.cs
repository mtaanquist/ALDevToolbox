using System.Text.Json;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Picks the human-readable name of an audited row — the "Sales Extensions" in
/// <c>changed the module Sales Extensions</c>. See issue #554: every audit row
/// used to name its primary key instead, which an admin has never seen and
/// cannot map to anything.
///
/// <para>The name is captured at <b>write</b> time by <c>AuditInterceptor</c>
/// into <c>audit_log.entity_name</c>, not looked up when the log is read, and
/// that is the point rather than an optimisation. An audit row should say what
/// the thing was called <i>when the change happened</i> — resolving the current
/// name would rewrite history every time something is renamed, and would say
/// nothing at all about a row that has since been deleted. It also keeps the
/// SiteAdmin console's cross-organisation log working without reaching across
/// the tenant fence to read another org's tables.</para>
///
/// <para>Rows written before that column existed fall back to
/// <see cref="FromSnapshot"/>, which reads the same fields out of the audit
/// row's own "before" snapshot. That covers historical updates and deletions;
/// historical <c>Created</c> rows carry no snapshot by design and stay
/// unnamed.</para>
/// </summary>
public static class AuditEntityName
{
    /// <summary>
    /// Cap on a stored name. A name is a label in a sentence, not the row — and
    /// one of the candidates below is a file path, which can be long.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// The first of these an audited entity actually has wins. Ordered by how
    /// well the field names the thing to a person, not by how common it is: a
    /// template has both <c>Key</c> and <c>Name</c>, and "CRONUS Standard"
    /// beats "bc26".
    ///
    /// <para><c>RelativePath</c> is deliberately <b>not</b> here, and that is
    /// the one entry worth knowing about. On a recipe file it looks like the
    /// better choice than <c>FileName</c> — until you read what it holds, which
    /// is the <i>folder</i> ("src/Sales", empty at the root), not the file. It
    /// shipped ahead of <c>FileName</c> in the first cut and rendered "changed
    /// the module file src/Posting", naming a directory for a change to a file
    /// inside it. Every other path-ish candidate here is the thing's own name:
    /// <c>WorkspaceExtensionFile.Path</c> is a basename, and
    /// <c>WorkspaceExtensionFolder.Path</c> is one segment.</para>
    ///
    /// <para>Deliberately a single shared list rather than a per-type map. All
    /// 27 audited types resolve correctly from it, and the four that resolve to
    /// nothing are right to: <c>RuntimeTemplateDefaultModule</c> is a join row
    /// with no name of its own, <c>OrganizationAsset</c> is the org's one logo,
    /// and <c>OrganizationSettings</c> / <c>SystemSettings</c> are singletons
    /// that the reading side already words as "the organisation settings".</para>
    ///
    /// <para>Nothing secret is on this list, and it is not a denylist doing that
    /// job by accident — an entity's secrets are named <c>*Encrypted</c> or
    /// <c>*Hash</c> and could never match. Keep it that way: add a field here
    /// only if you would put its value on the admin dashboard.</para>
    /// </summary>
    private static readonly string[] Candidates =
    {
        "Name",
        "DisplayName",
        "Title",
        "Key",
        "DepName",
        "LitName",
        "RefModuleKey",
        "FileName",
        "Path",
        "Email",
    };

    /// <summary>
    /// The candidate fields, in priority order. Exposed so tests can assert the
    /// audited types against the same list the pickers use rather than
    /// restating it.
    /// </summary>
    public static IReadOnlyList<string> CandidateFields => Candidates;

    /// <summary>
    /// Picks a name using <paramref name="lookup"/> to read a field by name.
    /// The lookup returns null both for "this entity has no such field" and for
    /// "the field is null", which are the same thing here.
    /// </summary>
    public static string? From(Func<string, string?> lookup)
    {
        foreach (var field in Candidates)
        {
            var value = lookup(field)?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value.Length > MaxLength ? value[..MaxLength] : value;
            }
        }

        return null;
    }

    /// <summary>
    /// The fallback for rows written before <c>entity_name</c> existed: the same
    /// fields, read out of the audit row's own "before" snapshot. Returns null
    /// for a <c>Created</c> row (no snapshot) and for malformed JSON — an audit
    /// page is the wrong place to throw.
    /// </summary>
    public static string? FromSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (doc.RootElement.ValueKind is not JsonValueKind.Object) return null;

            // Flattened into a case-insensitive map rather than read through
            // TryGetProperty, which is ordinal. The interceptor writes
            // PascalCase today, but this is a fallback for rows nobody is
            // writing any more - the one job it has is reading whatever an
            // older build happened to produce, and a casing change would make
            // it silently find nothing. Strings only: a name is a string, and
            // this keeps a numeric "Key" out of the sentence.
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.String)
                {
                    fields[property.Name] = property.Value.GetString();
                }
            }

            return From(field => fields.GetValueOrDefault(field));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
