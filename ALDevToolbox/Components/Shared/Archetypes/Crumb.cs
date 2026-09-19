namespace ALDevToolbox.Components.Shared.Archetypes;

/// <summary>
/// One step of a page's breadcrumb trail. A step with an <paramref name="Href"/> is a
/// link; the last step, the page itself, has none and renders as plain text.
/// </summary>
public sealed record Crumb(string Label, string? Href = null);
