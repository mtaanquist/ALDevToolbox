using System.Text;
using ALDevToolbox.Services.Cal;
using FluentAssertions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// Splitter coverage against a byte-preserved Windows-1252 / CRLF slice of a
/// real NAV C/AL export. Pins the object boundaries, brace-counting through
/// strings and the report's RDLDATA, and the 1252 decode.
/// </summary>
public sealed class CalObjectSplitterTests
{
    // The fixture is a real finsql "Export to text" — classic NAV exports use
    // the OEM codepage (850 for Western Europe), not 1252.
    private static readonly Encoding Cp850 = CodePagesEncoding();

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(850);
    }

    internal static string FixturePath()
        => Path.Combine(AppContext.BaseDirectory, "Cal", "Fixtures", "CalSample.txt");

    internal static List<CalObjectBlock> SplitFixture()
    {
        using var fs = File.OpenRead(FixturePath());
        return CalObjectSplitter.Split(fs, Cp850).ToList();
    }

    [Fact]
    public void Splits_each_object_with_correct_header()
    {
        var blocks = SplitFixture();

        blocks.Select(b => (b.TypeKeyword, b.Id, b.Name)).Should().Equal(
            ("Table", 18, "Customer"),
            ("Report", 6, "Trial Balance"),
            ("XMLport", 9171, "Import/Export Permission Sets"),
            ("MenuSuite", 1030, "Dept - Country"),
            ("Page", 9305, "Sales Order List"),
            ("Query", 104, "Sales Orders by Sales Person"));
    }

    [Fact]
    public void Each_block_is_brace_balanced_and_starts_with_OBJECT()
    {
        foreach (var b in SplitFixture())
        {
            b.RawText.TrimStart().Should().StartWith("OBJECT ");
            b.RawText.Count(c => c == '{').Should().Be(b.RawText.Count(c => c == '}'),
                because: $"{b.TypeKeyword} {b.Id} should be brace-balanced even with RDLDATA / strings");
            b.RawText.TrimEnd().Should().EndWith("}");
        }
    }

    [Fact]
    public void Report_with_rdldata_is_not_split_apart()
    {
        // The Trial Balance report carries an RDLDATA blob (embedded XML with
        // its own braces/quotes); it must remain one block.
        var report = SplitFixture().Single(b => b.TypeKeyword == "Report");
        report.RawText.Should().Contain("RDLDATA");
        report.RawText.Should().Contain("DATASET");
    }

    [Fact]
    public void Decodes_windows_1252_high_bytes()
    {
        // The Customer table's Danish captions contain 'ø'/'å'/'æ'. A correct
        // 1252 decode yields the single Unicode char, not a replacement.
        var customer = SplitFixture().First();
        customer.RawText.Should().NotContain("�");
        customer.RawText.Should().ContainAny("ø", "å", "æ", "Ø", "Å", "Æ");
    }

    [Fact]
    public void Skips_truncated_trailing_object_with_warning()
    {
        const string txt = "OBJECT Codeunit 1 Good\r\n{\r\n  CODE\r\n  {\r\n  }\r\n}\r\n"
                         + "OBJECT Codeunit 2 Truncated\r\n{\r\n  CODE\r\n  {\r\n";
        string? warning = null;
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(txt));
        var blocks = CalObjectSplitter.Split(ms, Encoding.ASCII, w => warning = w).ToList();

        blocks.Should().ContainSingle();
        blocks[0].Id.Should().Be(1);
        warning.Should().Contain("Truncated").And.Contain("Codeunit 2");
    }
}
