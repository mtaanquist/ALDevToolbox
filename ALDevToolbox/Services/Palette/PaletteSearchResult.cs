using System.Text.Json.Serialization;

namespace ALDevToolbox.Services.Palette;

/// <summary>
/// What <c>GET /palette/search</c> answers, and the contract the palette script
/// in the browser is written against. <b>The JSON property names below are that
/// contract</b> — they are spelled out rather than left to the serializer's
/// naming policy, because a rename here silently empties the palette rather than
/// failing anything.
///
/// <para>The groups are already ranked, capped and in display order, and empty
/// groups are omitted — the script renders what it is given.</para>
/// </summary>
/// <param name="Groups">Ranked, capped, in display order. Empty when nothing matched.</param>
/// <param name="Top">
/// The single row lifted above every group, or null. Only an exact match on a
/// short name gets here, and it is <em>not</em> repeated inside its own group.
/// </param>
public sealed record PaletteSearchResult(
    [property: JsonPropertyName("groups")] IReadOnlyList<PaletteResultGroup> Groups,
    [property: JsonPropertyName("top")] PaletteResultItem? Top)
{
    /// <summary>Nothing matched, or the query was too short to run — the same answer either way.</summary>
    public static readonly PaletteSearchResult Empty = new([], null);
}

/// <summary>One source's results, under its heading.</summary>
/// <param name="Id">The source's id: <c>solutions</c>, <c>environments</c>, ...</param>
/// <param name="Label">The heading a user reads.</param>
/// <param name="Items">At least one — an empty group is omitted from the response.</param>
public sealed record PaletteResultGroup(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("items")] IReadOnlyList<PaletteResultItem> Items);

/// <summary>
/// One row. Carries no icon and no class names: the browser clones the
/// <c>&lt;template&gt;</c> Razor rendered for this <paramref name="Kind"/> and
/// fills in the text and the link, which keeps the design system in one place.
///
/// <para>It also carries neither <see cref="PaletteCandidate.ShortName"/> nor
/// <see cref="PaletteCandidate.SearchOnly"/>. The second of those is the point:
/// a candidate may be <em>found</em> by a customer's Voice account number or
/// tenant id, and this record is where that stops — the row says which field
/// matched, never what was in it.</para>
/// </summary>
public sealed record PaletteResultItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("href")] string Href)
{
    internal static PaletteResultItem From(PaletteCandidate candidate) =>
        new(candidate.Kind, candidate.Title, candidate.Subtitle, candidate.Href);
}
