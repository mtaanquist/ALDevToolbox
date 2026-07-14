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
