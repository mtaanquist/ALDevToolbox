using AwesomeAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// The hover card's heading, which is reassembled rather than read: the
/// importer stores a symbol's parameter list on its own, with no name, no
/// keyword and no return type in it. Every part of that line is therefore a
/// decision, and two of them have already been wrong in shipped code.
///
/// <b>It rendered as <c>()</c>.</b> <c>signature || name</c> means a symbol
/// that HAS a signature never shows its name, so the one line whose job is to
/// say which symbol you are hovering said nothing at all.
///
/// <b>A field rendered as <c>Amount Including VATDecimal</c>.</b> A field's
/// "signature" is its AL type, not a parameter list, so joining the two with
/// nothing between them runs two words together.
///
/// Visibility lives here rather than in the meta row, because it modifies the
/// member and not its location — and because in the meta row it was the first
/// thing cut short when a module name got long.
/// </summary>
public sealed class SymbolCardDeclarationTests
{
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";

    /// <summary>
    /// Three of the four visibilities are a keyword AL actually has. The
    /// fourth is not: AL has no <c>public</c> modifier — a procedure with none
    /// IS public — so the public case is a bare <c>procedure</c>. Writing
    /// "public procedure" would teach syntax that does not compile.
    /// </summary>
    [Theory]
    [InlineData("procedure", "`procedure ${name}${params}${returns}`")]
    [InlineData("local_procedure", "`local procedure ${name}${params}${returns}`")]
    [InlineData("internal_procedure", "`internal procedure ${name}${params}${returns}`")]
    [InlineData("protected_procedure", "`protected procedure ${name}${params}${returns}`")]
    public void Each_visibility_reads_as_the_al_keyword_for_it(string kind, string rendering)
    {
        var fn = Declaration();
        fn.Should().Contain($"case \"{kind}\":");
        fn.Should().Contain(rendering);
    }

    [Fact]
    public void No_invented_public_keyword()
    {
        Declaration().Should().NotContain("public procedure",
            because: "AL has no public modifier; an unmodified procedure is the public one");
    }

    [Fact]
    public void A_field_separates_its_name_from_its_type()
    {
        var fn = Declaration();
        fn.Should().Contain("case \"table_field\":");
        fn.Should().Contain("case \"page_field\":");
        fn.Should().Contain("`${name}: ${params}`",
            because: "a field's signature is its AL type, and `Amount Including VATDecimal` is what "
                     + "joining them with nothing looks like");
    }

    /// <summary>
    /// An event publisher is a procedure under an <c>[IntegrationEvent]</c>
    /// attribute. Calling it "procedure" in the heading would drop the only
    /// thing that makes it different from every other procedure, so it falls
    /// through to the bare form and the outline section names the kind.
    /// </summary>
    [Fact]
    public void Event_kinds_get_no_keyword()
    {
        var fn = Declaration();
        fn.Should().NotContain("event_publisher");
        fn.Should().NotContain("event_subscriber");
        fn.Should().Contain("default:", "they take the bare name + signature form");
    }

    [Fact]
    public void The_name_is_always_in_the_heading()
    {
        var fn = Declaration();
        fn.Should().NotContain("data.signature || data.name",
            because: "that is what rendered a card headed `()`");
        fn.Should().Contain("const name = data.name ?? \"\";");
    }

    [Fact]
    public void The_meta_row_no_longer_carries_the_visibility()
    {
        var js = Read(ViewerJs);
        js.Should().NotContain("accessOf",
            because: "visibility moved into the declaration line; a second copy would drift");
    }

    private static string Declaration() =>
        Between(Read(ViewerJs), "function declarationOf(data) {", "\n}\n");

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, $"'{start}' should exist");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"'{start}' should be a complete function");
        return text[from..to];
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
