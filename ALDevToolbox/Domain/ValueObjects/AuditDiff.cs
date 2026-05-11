using System.Text.Json;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// One field-level difference between two audit snapshots. The path is a
/// dotted / bracketed key (e.g. <c>Defaults.Publisher</c>, <c>Folders[2].Path</c>)
/// so the diff viewer can render a flat table that an admin can scan for the
/// change they care about.
/// </summary>
public sealed record AuditDiffEntry(
    string Path,
    AuditDiffKind Kind,
    string? BeforeDisplay,
    string? AfterDisplay,
    bool BeforeRedacted = false,
    bool AfterRedacted = false);

/// <summary>How a single field changed between two audit snapshots.</summary>
public enum AuditDiffKind
{
    /// <summary>Field exists only in the "after" snapshot.</summary>
    Added,
    /// <summary>Field exists only in the "before" snapshot.</summary>
    Removed,
    /// <summary>Field exists in both, with a different value.</summary>
    Changed,
    /// <summary>Field exists in both with the same value. Hidden from the default view.</summary>
    Unchanged,
}

/// <summary>
/// Computes a flat field-level diff between two audit-log snapshot JSON
/// strings. Each entry is keyed by its dotted / bracketed path so the diff
/// viewer can render a single scannable table (see
/// <c>.design/milestones.md</c> Milestone 20). Designed to be exercised in
/// isolation by xUnit so the diff contract stays auditable without a Blazor
/// host.
/// </summary>
public static class AuditDiff
{
    /// <summary>
    /// The fixed sentinel <see cref="ALDevToolbox.Data.AuditInterceptor"/> writes
    /// in place of redacted column values (e.g. SMTP password).
    /// </summary>
    public const string RedactedSentinel = "[redacted]";

    /// <summary>
    /// Property names whose value is a hash of the redacted bytes. The diff
    /// viewer never shows the hash itself; it renders <c>&lt;redacted&gt;</c>
    /// per the milestone contract.
    /// </summary>
    private static readonly HashSet<string> RedactedHashKeys = new(StringComparer.Ordinal)
    {
        "ContentSha256",
    };

    /// <summary>
    /// Diffs two JSON snapshots and returns one <see cref="AuditDiffEntry"/>
    /// per leaf path that differs. Unchanged paths are omitted. A null /
    /// empty snapshot is treated as "absent": every path in the other
    /// snapshot is reported as Added or Removed accordingly. Invalid JSON
    /// throws <see cref="JsonException"/> — callers parse user-shaped data
    /// before calling, so this only fires on a malformed audit row.
    /// </summary>
    public static IReadOnlyList<AuditDiffEntry> Compute(string? beforeJson, string? afterJson)
    {
        var before = ParseObject(beforeJson);
        var after = ParseObject(afterJson);
        var results = new List<AuditDiffEntry>();
        Walk(string.Empty, before, after, results);
        return results;
    }

    private static Dictionary<string, JsonElement>? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        // The interceptor only ever writes objects at the top level; if we
        // ever get an array or scalar at the root, treat it as a single
        // pseudo-property keyed by the empty string.
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>
            {
                [string.Empty] = doc.RootElement.Clone(),
            };
        }
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }

    private static void Walk(
        string pathPrefix,
        Dictionary<string, JsonElement>? before,
        Dictionary<string, JsonElement>? after,
        List<AuditDiffEntry> results)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (before is not null) foreach (var k in before.Keys) keys.Add(k);
        if (after is not null) foreach (var k in after.Keys) keys.Add(k);

        foreach (var key in keys)
        {
            var path = string.IsNullOrEmpty(pathPrefix) ? key : $"{pathPrefix}.{key}";
            JsonElement beforeEl = default;
            JsonElement afterEl = default;
            var hasBefore = before is not null && before.TryGetValue(key, out beforeEl);
            var hasAfter = after is not null && after.TryGetValue(key, out afterEl);

            if (hasBefore && hasAfter)
            {
                DiffElement(path, key, beforeEl, afterEl, results);
            }
            else if (hasBefore)
            {
                EmitLeafSide(path, key, beforeEl, AuditDiffKind.Removed, results, beforeSide: true);
            }
            else
            {
                EmitLeafSide(path, key, afterEl, AuditDiffKind.Added, results, beforeSide: false);
            }
        }
    }

    private static void DiffElement(
        string path,
        string leafKey,
        JsonElement before,
        JsonElement after,
        List<AuditDiffEntry> results)
    {
        // Recurse into nested objects so the diff path identifies the leaf.
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
        {
            var beforeMap = ToDict(before);
            var afterMap = ToDict(after);
            Walk(path, beforeMap, afterMap, results);
            return;
        }

        // Arrays diff element-wise by index so reorderings surface as the
        // moves they are.
        if (before.ValueKind == JsonValueKind.Array && after.ValueKind == JsonValueKind.Array)
        {
            var beforeLen = before.GetArrayLength();
            var afterLen = after.GetArrayLength();
            var max = Math.Max(beforeLen, afterLen);
            for (var i = 0; i < max; i++)
            {
                var indexPath = $"{path}[{i}]";
                if (i < beforeLen && i < afterLen)
                {
                    DiffElement(indexPath, leafKey, before[i], after[i], results);
                }
                else if (i < beforeLen)
                {
                    EmitLeafSide(indexPath, leafKey, before[i], AuditDiffKind.Removed, results, beforeSide: true);
                }
                else
                {
                    EmitLeafSide(indexPath, leafKey, after[i], AuditDiffKind.Added, results, beforeSide: false);
                }
            }
            return;
        }

        var beforeRedacted = IsRedacted(leafKey, before);
        var afterRedacted = IsRedacted(leafKey, after);
        var beforeDisplay = Render(before);
        var afterDisplay = Render(after);
        if (string.Equals(beforeDisplay, afterDisplay, StringComparison.Ordinal))
        {
            // Suppress unchanged leaves from the materialised list.
            return;
        }

        results.Add(new AuditDiffEntry(
            path,
            AuditDiffKind.Changed,
            beforeDisplay,
            afterDisplay,
            beforeRedacted,
            afterRedacted));
    }

    private static void EmitLeafSide(
        string path,
        string leafKey,
        JsonElement value,
        AuditDiffKind kind,
        List<AuditDiffEntry> results,
        bool beforeSide)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Walk(path, beforeSide ? ToDict(value) : null, beforeSide ? null : ToDict(value), results);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            for (var i = 0; i < value.GetArrayLength(); i++)
            {
                var indexPath = $"{path}[{i}]";
                EmitLeafSide(indexPath, leafKey, value[i], kind, results, beforeSide);
            }
            return;
        }

        var redacted = IsRedacted(leafKey, value);
        var display = Render(value);
        results.Add(new AuditDiffEntry(
            path,
            kind,
            beforeSide ? display : null,
            beforeSide ? null : display,
            beforeSide && redacted,
            !beforeSide && redacted));
    }

    private static Dictionary<string, JsonElement> ToDict(JsonElement obj)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
        {
            dict[p.Name] = p.Value;
        }
        return dict;
    }

    private static bool IsRedacted(string leafKey, JsonElement value)
    {
        if (RedactedHashKeys.Contains(leafKey)) return true;
        if (value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), RedactedSentinel, StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static string? Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object => value.GetRawText(),
        JsonValueKind.Array => value.GetRawText(),
        _ => value.GetRawText(),
    };

    /// <summary>
    /// Format helper used by both the test assertions and the viewer. Returns
    /// <c>"&lt;empty&gt;"</c> for an absent side, <c>"&lt;redacted&gt;"</c>
    /// when the value was redacted at audit time, and the raw display
    /// otherwise.
    /// </summary>
    public static string FormatCell(string? display, bool redacted, bool absent)
    {
        if (absent) return "<empty>";
        if (redacted) return "<redacted>";
        return display ?? "<null>";
    }
}
