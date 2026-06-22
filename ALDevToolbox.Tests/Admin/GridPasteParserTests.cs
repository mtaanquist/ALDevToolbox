using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.Admin;

/// <summary>
/// Pins the TSV parsing rules behind the Excel-style paste on the row-table
/// admin editors (catalogue, application versions): tab/newline splitting,
/// the single trailing-newline trim spreadsheets append, CRLF normalisation,
/// and per-cell trimming.
/// </summary>
public sealed class GridPasteParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_or_null_input_yields_an_empty_grid(string? raw)
    {
        GridPasteParser.Parse(raw).Should().BeEmpty();
    }

    [Fact]
    public void Splits_tabs_into_cells_and_newlines_into_rows()
    {
        var grid = GridPasteParser.Parse("a\tb\tc\nd\te\tf");

        grid.Should().HaveCount(2);
        grid[0].Should().Equal("a", "b", "c");
        grid[1].Should().Equal("d", "e", "f");
    }

    [Fact]
    public void A_single_trailing_newline_is_dropped_rather_than_making_an_empty_row()
    {
        // A range copied from a spreadsheet ends with exactly one newline.
        var grid = GridPasteParser.Parse("a\tb\n");

        grid.Should().HaveCount(1);
        grid[0].Should().Equal("a", "b");
    }

    [Fact]
    public void Crlf_line_endings_are_normalised()
    {
        var grid = GridPasteParser.Parse("a\tb\r\nc\td\r\n");

        grid.Should().HaveCount(2);
        grid[0].Should().Equal("a", "b");
        grid[1].Should().Equal("c", "d");
    }

    [Fact]
    public void Cells_are_trimmed_of_surrounding_whitespace()
    {
        var grid = GridPasteParser.Parse("  bc-2026-w1 \t Business Central 2026 ");

        grid.Should().HaveCount(1);
        grid[0].Should().Equal("bc-2026-w1", "Business Central 2026");
    }

    [Fact]
    public void A_single_value_parses_as_one_cell()
    {
        // The JS layer decides not to intercept single values, but the parser
        // itself stays agnostic and round-trips a lone value.
        var grid = GridPasteParser.Parse("28.0.0.0");

        grid.Should().HaveCount(1);
        grid[0].Should().Equal("28.0.0.0");
    }
}
