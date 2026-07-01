namespace ALDevToolbox.Domain.Tools;

/// <summary>
/// The tools that appear in the sidebar's Tools section and can be switched
/// off site-wide (by a SiteAdmin) or per-organisation (by an org Admin). The
/// enum name is the stable identifier persisted in
/// <c>system_settings.disabled_tools</c> / <c>organizations.disabled_tools</c>
/// and carried on the <c>org_disabled_tools</c> auth claim — don't rename a
/// member without a data migration. Home is deliberately absent: it's the
/// dashboard and is always available.
/// </summary>
public enum ToolKey
{
    Piper,
    Templates,
    Cookbook,
    ObjectExplorer,
    Projects,
    Pipelines,
    Releases,
    Translator,
    Mcp,
}

/// <summary>
/// Static description of one toggleable tool: its display name, a one-line
/// blurb for the settings pages, and the URL prefixes its end-user pages live
/// under (used by the route-access gate to 404 a disabled tool). Admin /
/// authoring routes (<c>/admin/*</c>) are deliberately excluded — disabling a
/// tool hides its end-user surface but leaves content authoring reachable.
/// </summary>
public sealed record ToolDescriptor(
    ToolKey Key,
    string Name,
    string Blurb,
    IReadOnlyList<string> RoutePrefixes);

/// <summary>
/// Single source of truth for the toggleable tools. Feeds the sidebar, the
/// route-access gate, and both settings pages so the list lives in one place.
/// Pure data — no HTTP or EF types — so it can be referenced from the domain,
/// the middleware, and the Razor components alike.
/// </summary>
public static class ToolCatalog
{
    /// <summary>In sidebar order. <see cref="ToolKey.Mcp"/> is last, matching the nav.</summary>
    public static readonly IReadOnlyList<ToolDescriptor> All = new[]
    {
        new ToolDescriptor(ToolKey.Piper, "Piper",
            "Convert pasted values into piped strings, SQL lists and other formats.",
            new[] { "/piper" }),
        new ToolDescriptor(ToolKey.Templates, "Templates",
            "Browse templates and generate AL workspaces and extensions.",
            new[] { "/templates" }),
        new ToolDescriptor(ToolKey.Cookbook, "Cookbook",
            "Browse reusable AL code recipes.",
            new[] { "/cookbook" }),
        new ToolDescriptor(ToolKey.ObjectExplorer, "Object Explorer",
            "Explore Business Central objects, fields and references.",
            new[] { "/object-explorer" }),
        new ToolDescriptor(ToolKey.Projects, "Projects",
            "Create and manage AL projects.",
            new[] { "/projects" }),
        new ToolDescriptor(ToolKey.Pipelines, "Pipelines",
            "Build AL projects and track their build pipelines.",
            new[] { "/pipelines", "/artifacts" }),
        new ToolDescriptor(ToolKey.Releases, "Releases",
            "Publish builds and deliver them to Business Central environments.",
            new[] { "/releases" }),
        new ToolDescriptor(ToolKey.Translator, "Translator",
            "Translate AL apps and manage XLIFF files.",
            new[] { "/translator" }),
        new ToolDescriptor(ToolKey.Mcp, "MCP",
            "Let AI coding assistants connect to your tools.",
            new[] { "/tools/mcp" }),
    };

    /// <summary>Look up a descriptor by key. Throws if the key is somehow unmapped (it always is).</summary>
    public static ToolDescriptor Describe(ToolKey key) =>
        All.First(t => t.Key == key);

    /// <summary>
    /// Parses a set of persisted/claim tool names into <see cref="ToolKey"/>s,
    /// silently dropping anything that no longer maps to a member. Tolerant by
    /// design: an old name left in the DB or on a stale cookie after a tool is
    /// removed must not throw on every request.
    /// </summary>
    public static HashSet<ToolKey> ParseDisabled(IEnumerable<string>? names)
    {
        var set = new HashSet<ToolKey>();
        if (names is null) return set;
        foreach (var name in names)
        {
            if (Enum.TryParse<ToolKey>(name, ignoreCase: false, out var key) && Enum.IsDefined(key))
            {
                set.Add(key);
            }
        }
        return set;
    }

    /// <summary>Renders tool keys to their stable string names for storage / the claim.</summary>
    public static List<string> Format(IEnumerable<ToolKey> keys) =>
        keys.Select(k => k.ToString()).ToList();
}
