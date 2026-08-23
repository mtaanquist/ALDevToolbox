using System.Text.Json;

namespace ALDevToolbox.Services.Diff;

/// <summary>
/// One band in a collapsed compare pane. Either it stands in for hidden lines
/// (<see cref="From"/>..<see cref="To"/>, and clicking it brings them back),
/// or it hides nothing and simply announces the hunk below it — which is what
/// the banner above a diff that starts at line 1 does.
///
/// <para>Shared by both compare renderings, and deliberately so: the two used
/// to collapse by different mechanisms, which is how the inline view ended up
/// unable to expand anything at all (#585). Side-by-side hid its unchanged
/// runs client-side and could bring them back; inline never emitted them into
/// its document, so its bands were inert. Both now ship every line and hide the
/// same way, off this one shape.</para>
///
/// <para><see cref="From"/> / <see cref="To"/> / <see cref="Before"/> are line
/// numbers <b>in the document the pane holds</b>. For a side-by-side pane that
/// is a real file, so they are its own line numbers; for the inline pane the
/// document is synthesised, so they are row numbers within it and match neither
/// side's file. That difference is invisible to the client, which is the point
/// — it just hides the lines it is told to.</para>
/// </summary>
/// <param name="Index">Shared across the panes that must expand together.</param>
/// <param name="Header">The band's text.</param>
/// <param name="From">First hidden line, or null when nothing is hidden.</param>
/// <param name="To">Last hidden line.</param>
/// <param name="Before">Line the band sits above, when it hides nothing.</param>
public readonly record struct CollapseRegion(int Index, string Header, int? From, int? To, int? Before);

/// <summary>Shared JSON shape for the collapse payloads both compare views ship.</summary>
internal static class CollapseJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Serialize(IReadOnlyList<CollapseRegion> regions) =>
        JsonSerializer.Serialize(regions, Options);
}
