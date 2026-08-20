namespace ALDevToolbox.Components.Pages.ObjectExplorer;

/// <summary>
/// The two-letter badge an AL object kind wears in the explorer tree (the
/// handoff's <c>.okind</c>) and the tint that goes with it.
///
/// The letters are the app's own search prefixes, uppercased: what you type
/// into the Object Explorer's search box to scope a query (<c>te:</c>,
/// <c>c:</c>) is what you read off the tree. That mapping lives in
/// <see cref="Services.ObjectExplorer.ObjectSearchRanking"/> and the two must
/// stay in step — <c>ObjectKindGlyphTests</c> asserts it.
///
/// The handoff's own sample uses <c>TE</c> and <c>PE</c> for the two
/// extensions, which the search prefixes reproduce exactly; it draws tables as
/// <c>TB</c>, codeunits as <c>CU</c> and reports as <c>RE</c>, where the
/// prefixes say <c>T</c>, <c>C</c> and <c>R</c>. Taking the prefixes across the
/// board keeps one alphabet in the tool instead of two, and stops <c>RE</c>
/// meaning "report" here and "report extension" in the search box.
///
/// EXTENDING WHEN MICROSOFT ADDS A NEW OBJECT KIND: add the prefix to
/// <c>ObjectSearchRanking.KindPrefixes</c> first (that's what makes it
/// searchable), then add the same short form here. A kind with no short prefix
/// gets no entry and no badge — the legacy C/AL kinds (<c>form</c>,
/// <c>dataport</c>) are the current example. Those rows draw the generic file
/// icon instead, which is honest; inventing letters no search box accepts is
/// not.
/// </summary>
public static class ObjectKindGlyph
{
    /// <summary>
    /// Short badge for an object kind, or an empty string when the kind is
    /// one we have no letters for.
    /// </summary>
    public static string For(string? kind) => (kind ?? string.Empty).ToLowerInvariant() switch
    {
        "table" => "T",
        "page" => "P",
        "codeunit" => "C",
        "report" => "R",
        "query" => "Q",
        "xmlport" => "X",
        "enum" => "E",
        "interface" => "I",
        "permissionset" => "PS",
        "controladdin" => "CA",
        "tableextension" => "TE",
        "pageextension" => "PE",
        "reportextension" => "RE",
        "enumextension" => "EE",
        "permissionsetextension" => "PSE",
        "menusuite" => "MS",
        "profile" => "PR",
        _ => string.Empty,
    };

    /// <summary>
    /// Tint modifier for the badge. The handoff colours four families —
    /// tables, pages, codeunits and reports — and leaves everything else on
    /// the default grey. An extension is tinted as the thing it extends.
    /// </summary>
    public static string TintClass(string? kind) => (kind ?? string.Empty).ToLowerInvariant() switch
    {
        "table" or "tableextension" => "okind--tab",
        "page" or "pageextension" => "okind--pag",
        "codeunit" => "okind--cod",
        "report" or "reportextension" => "okind--rep",
        _ => string.Empty,
    };
}
