namespace ALDevToolbox.Domain.Navigation;

/// <summary>
/// One person's arrangement of the sidebar: the order of its groups, and within
/// each group the order of its items (issue #956). Read from the
/// <c>aldt-nav-order</c> cookie that <c>wwwroot/nav-arrange.js</c> writes, so
/// the server can render the sidebar in that order on the first paint.
///
/// <para>It is a preference, not a permission. <see cref="Apply"/> only ever
/// orders the keys it is handed, so a tool the org has switched off or the
/// person's role cannot see stays out whatever the saved order says, and a key
/// the saved order has never heard of - a tool that appeared later - lands at
/// the end of its list in shipped order rather than being lost.</para>
///
/// <para>The cookie format is the one <see cref="Serialize"/> writes:
/// <c>groups|group:items|group:items</c>, each list comma-separated, e.g.
/// <c>deliver,text,build|deliver:pipelines,solutions,teams</c>. Anything that
/// does not parse is ignored, so a mangled cookie falls back to the shipped
/// order instead of failing the render.</para>
/// </summary>
public sealed record SidebarOrder(
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Items)
{
    /// <summary>Name of the cookie the browser keeps the arrangement in.</summary>
    public const string CookieName = "aldt-nav-order";

    /// <summary>No saved arrangement: everything renders in shipped order.</summary>
    public static SidebarOrder Default { get; } = new(
        Array.Empty<string>(),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    // Far above any real sidebar, so a hostile or corrupted cookie cannot make
    // the render do unbounded work.
    private const int MaxKeysPerList = 64;
    private const int MaxLists = 32;

    /// <summary>The saved order of one group's items, or empty when none was saved.</summary>
    public IReadOnlyList<string> ItemsOf(string group) =>
        Items.TryGetValue(group, out var items) ? items : Array.Empty<string>();

    /// <summary>
    /// Orders <paramref name="available"/> by <paramref name="saved"/>: keys the saved
    /// list names come first, in its order; the rest follow in the order they were
    /// given, which is the shipped order. Saved keys that are not available are
    /// dropped, and so are repeats.
    /// </summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> available, IReadOnlyList<string> saved)
    {
        if (saved.Count == 0) return available;

        var result = new List<string>(available.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var present = new HashSet<string>(available, StringComparer.Ordinal);

        foreach (var key in saved)
        {
            if (present.Contains(key) && placed.Add(key)) result.Add(key);
        }
        foreach (var key in available)
        {
            if (placed.Add(key)) result.Add(key);
        }
        return result;
    }

    /// <summary>
    /// Reads the cookie value. Never throws: an empty, garbled or oversized value
    /// yields <see cref="Default"/> or whatever part of it did parse.
    /// </summary>
    public static SidebarOrder Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096) return Default;

        string value;
        try
        {
            value = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return Default;
        }

        var parts = value.Split('|');
        var groups = Keys(parts[0]);
        var items = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var part in parts.Skip(1).Take(MaxLists))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var group = part[..colon].Trim();
            if (!IsKey(group)) continue;
            items[group] = Keys(part[(colon + 1)..]);
        }

        return new SidebarOrder(groups, items);
    }

    /// <summary>
    /// Writes an arrangement in the cookie format. The sidebar also renders the
    /// shipped order this way, so "Reset to default" can restore it in place.
    /// </summary>
    public static string Serialize(IEnumerable<(string Group, IEnumerable<string> Items)> groups)
    {
        var list = groups.ToList();
        var parts = new List<string> { string.Join(',', list.Select(g => g.Group)) };
        parts.AddRange(list.Select(g => g.Group + ":" + string.Join(',', g.Items)));
        return string.Join('|', parts);
    }

    private static List<string> Keys(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsKey)
            .Take(MaxKeysPerList)
            .ToList();

    /// <summary>Keys are lower-case slugs; anything else did not come from the sidebar.</summary>
    private static bool IsKey(string key) =>
        key.Length is > 0 and <= 40
        && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
