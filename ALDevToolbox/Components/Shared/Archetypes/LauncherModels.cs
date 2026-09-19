namespace ALDevToolbox.Components.Shared.Archetypes;

/// <summary>One headed run of tiles on a <see cref="LauncherPage"/>. A group with no tiles is not rendered.</summary>
public sealed record LauncherGroup(string Label, IReadOnlyList<LauncherTile> Tiles);

/// <summary>
/// One tool tile. <paramref name="Meta"/> is the optional count line; a
/// <paramref name="Locked"/> tile shows the sign-in pill and links to the login page
/// with <paramref name="Href"/> as the return URL instead of to the tool.
/// </summary>
public sealed record LauncherTile(string Title, string Text, string Icon, string Href, string? Meta = null, bool Locked = false);
