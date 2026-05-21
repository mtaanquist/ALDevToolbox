using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.Snippets;

/// <summary>
/// Sanity coverage for <see cref="MarkdownRenderer"/>: empty input yields
/// empty output, headings / fenced code render, and raw HTML in the source
/// is stripped at the parser level so a pasted script tag can't escape into
/// a published snippet's instructions section.
/// </summary>
public sealed class MarkdownRendererTests
{
    private readonly MarkdownRenderer _sut = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Render_returns_empty_for_blank_input(string? input)
    {
        _sut.Render(input).Value.Should().BeEmpty();
    }

    [Fact]
    public void Render_emits_heading_for_markdown_h2()
    {
        var html = _sut.Render("## Setup").Value;
        html.Should().Contain("<h2");
        html.Should().Contain("Setup");
    }

    [Fact]
    public void Render_strips_raw_script_tag_from_source()
    {
        var html = _sut.Render("Hello <script>alert(1)</script> world").Value;
        html.Should().NotContain("<script");
        // The literal text from the source is preserved (with the < / > escaped),
        // so authors can still see the offending payload — they just can't
        // execute it as HTML.
        html.Should().Contain("Hello");
        html.Should().Contain("world");
    }

    [Fact]
    public void Render_supports_fenced_code()
    {
        var html = _sut.Render("```al\nfield(1; \"Foo\"; Text[50]) { }\n```").Value;
        html.Should().Contain("<pre");
        html.Should().Contain("<code");
    }
}
