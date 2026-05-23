namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Coarse size/shape tag for a <see cref="ALDevToolbox.Domain.Entities.Recipe"/>.
/// Used by the cookbook browser to filter the list and by the type badge on
/// each card. The Cookbook search ignores this field — agents and humans
/// type free-text queries; the type chip-row is a post-filter on the
/// returned results.
/// </summary>
public enum RecipeType
{
    /// <summary>A small pattern, typically one or two files; the historical "snippet".</summary>
    Snippet = 0,

    /// <summary>A few related files solving one problem — e.g. an event-subscriber pair, or a setup table + page + install codeunit.</summary>
    Pattern = 1,

    /// <summary>A near-complete feature, multiple files spanning several namespaces under one top-level namespace.</summary>
    Module = 2,
}
