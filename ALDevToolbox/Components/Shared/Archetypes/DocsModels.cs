namespace ALDevToolbox.Components.Shared.Archetypes;

/// <summary>
/// One "On this page" link on a <see cref="DocsPage"/>. <paramref name="Anchor"/> is the
/// heading's id without the hash; <paramref name="Sub"/> indents it under the entry above.
/// </summary>
public sealed record DocsTocEntry(string Label, string Anchor, bool Sub = false);
