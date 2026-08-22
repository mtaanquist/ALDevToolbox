using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Pins the anonymous <c>POST /api/compare/diff</c> the editable Compare tool
/// hits from source-viewer.js: it must serve without auth, return real JSON
/// arrays (not double-encoded strings) so the client can apply them directly,
/// report the identical case, and refuse an over-cap paste with a friendly
/// error rather than a stack trace.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class CompareEndpointTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public CompareEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Diff_of_two_texts_serves_anonymously_with_arrays_and_summary()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "a\nb\nc", right = "a\nx\nc\nd" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Raw JSON arrays, not re-encoded strings.
        root.GetProperty("right").GetProperty("diff").ValueKind.Should().Be(JsonValueKind.Array);
        var summary = root.GetProperty("summary");
        summary.GetProperty("modified").GetInt32().Should().Be(1);
        summary.GetProperty("added").GetInt32().Should().Be(1);
        summary.GetProperty("identical").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// The inline layout's document rides every response (#581). It has to,
    /// on this tool: the two panes ARE the input, so the unified text is a
    /// function of what the reader typed a keystroke ago — unlike the Object
    /// Explorer's file diff, where it can be baked into the page once.
    ///
    /// Shipped as one block beside the two sides rather than fetched on the
    /// switch, so the layout tabs never wait on a round-trip and the two
    /// renderings can never be of different text.
    /// </summary>
    [Fact]
    public async Task The_response_carries_the_inline_document_for_the_same_texts()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "a\nOLD\nc", right = "a\nNEW\nc" });

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var inline = doc.RootElement.GetProperty("inline");

        // Old above new — the one case where an aligned row becomes two.
        inline.GetProperty("content").GetString()!.Split('\n')
            .Should().Equal("a", "OLD", "NEW", "c");

        foreach (var name in new[] { "rows", "gutters", "collapse", "wordDiff" })
        {
            inline.GetProperty(name).ValueKind.Should().Be(JsonValueKind.Array,
                because: $"the client applies {name} directly, so it must not arrive double-encoded");
        }
    }

    /// <summary>
    /// The document carries the unchanged runs even when they are far outside
    /// the context window, because the bands hide them client-side and put them
    /// back on a click. Dropping them server-side is what left the Object
    /// Explorer's inline bands inert before #585.
    /// </summary>
    [Fact]
    public async Task The_inline_document_keeps_the_lines_its_bands_hide()
    {
        var left = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line{i}"));
        var right = left.Replace("line15", "CHANGED");

        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left, right });

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var inline = doc.RootElement.GetProperty("inline");

        var content = inline.GetProperty("content").GetString()!;
        content.Should().Contain("line1\n", "a hidden line is still in the document");
        content.Should().Contain("line30");

        var bands = inline.GetProperty("collapse");
        bands.GetArrayLength().Should().Be(2, "one run hidden above the change and one below");
        foreach (var band in bands.EnumerateArray())
        {
            band.TryGetProperty("from", out var from).Should().BeTrue(
                because: "a band with nothing behind it is not a control, and both of these hide a run");
            from.GetInt32().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Identical_texts_report_identical()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "same\ntext", right = "same\ntext" });

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = doc.RootElement.GetProperty("summary");
        summary.GetProperty("identical").GetBoolean().Should().BeTrue();
        summary.GetProperty("modified").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Oversize_input_returns_a_friendly_error()
    {
        using var client = _factory.CreateClient();
        var huge = new string('a', PiperTransform.MaxInputLength + 1);
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = huge, right = "b" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("too large");
    }
}
