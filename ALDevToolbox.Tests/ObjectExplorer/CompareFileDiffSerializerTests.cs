using ALDevToolbox.Services.ObjectExplorer;
using DiffPlex.DiffBuilder;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pure-function coverage for the diff payload that <c>OeCompareFile.razor</c>
/// hands to the source-viewer JS via <c>data-diff</c>. The non-trivial part
/// is that DiffPlex's <c>SideBySideDiffModel</c> pads each pane with
/// <c>Imaginary</c> placeholder rows to align with the opposite side. The
/// CodeMirror viewer renders only the actual source content (no imaginaries),
/// so the emitted line numbers must be source positions — not pane indices —
/// or the decorations drift as soon as one side has more insertions than the
/// other has deletions above that point.
/// </summary>
public sealed class CompareFileDiffSerializerTests
{
    [Fact]
    public void Serialises_inserted_lines_with_their_source_position_on_the_new_side()
    {
        const string left =
            "line1\n" +
            "line2\n" +
            "line3\n" +
            "line4\n";
        const string right =
            "line1\n" +
            "NEW_A\n" +
            "NEW_B\n" +
            "line2\n" +
            "line3\n" +
            "line4\n";

        var model = SideBySideDiffBuilder.Diff(left, right);
        var json = CompareFileDiffSerializer.SerializeSide(model.NewText);

        // The inserted lines NEW_A and NEW_B live at source positions 2 and 3
        // in the right pane. The OLD pane has two Imaginary rows at those
        // indices, so a pane-index encoding would have shifted the numbers.
        json.Should().Contain("\"line\":2").And.Contain("\"line\":3");
        json.Should().Contain("\"kind\":\"inserted\"");
    }

    [Fact]
    public void Skips_imaginary_rows_on_the_left_side_so_deletes_keep_source_positions()
    {
        // Right inserts two lines near the top, then a deletion happens
        // further down on the left. Without skipping imaginaries on the
        // left side, the delete would land on the wrong line in CodeMirror.
        const string left =
            "alpha\n" +
            "beta\n" +
            "gamma\n" +
            "DELETED\n" +
            "delta\n";
        const string right =
            "alpha\n" +
            "INS1\n" +
            "INS2\n" +
            "beta\n" +
            "gamma\n" +
            "delta\n";

        var model = SideBySideDiffBuilder.Diff(left, right);
        var leftJson = CompareFileDiffSerializer.SerializeSide(model.OldText);

        // DELETED is source line 4 on the left, regardless of how many
        // imaginaries were injected above it to align with the right pane.
        leftJson.Should().Contain("\"line\":4");
        leftJson.Should().Contain("\"kind\":\"deleted\"");
        leftJson.Should().NotContain("\"kind\":\"imaginary\"",
            "imaginary rows are placeholders on the diff pane with no counterpart " +
            "in the rendered source — emitting them would decorate the wrong line");
    }

    [Fact]
    public void Omits_unchanged_rows_to_keep_the_payload_small()
    {
        const string left = "a\nb\nc\nd\n";
        const string right = "a\nb\nc\nd\n";

        var model = SideBySideDiffBuilder.Diff(left, right);
        var json = CompareFileDiffSerializer.SerializeSide(model.NewText);

        json.Should().Be("[]");
    }

    [Fact]
    public void Maps_change_types_to_lowercase_kind_strings()
    {
        const string left  = "keep\nold\n";
        const string right = "keep\nnew\n";

        var model = SideBySideDiffBuilder.Diff(left, right);
        var leftJson = CompareFileDiffSerializer.SerializeSide(model.OldText);
        var rightJson = CompareFileDiffSerializer.SerializeSide(model.NewText);

        // Single-line replace at the bottom: DiffPlex classifies as either
        // a paired Modified or a Deleted/Inserted pair depending on the
        // similarity threshold. Either is acceptable; both kinds are
        // lowercase strings the JS understands.
        var combined = leftJson + rightJson;
        combined.Should().MatchRegex("\"kind\":\"(modified|deleted|inserted)\"");
    }

    [Fact]
    public void Summarize_reports_identical_when_no_changes()
    {
        var model = SideBySideDiffBuilder.Diff("a\nb\nc\n", "a\nb\nc\n");
        var summary = CompareFileDiffSerializer.Summarize(model);

        summary.Identical.Should().BeTrue();
        summary.Total.Should().Be(0);
    }

    [Fact]
    public void Summarize_counts_a_pure_insertion()
    {
        // One line appended on the new side, nothing removed.
        var model = SideBySideDiffBuilder.Diff("a\nb\n", "a\nb\nc\n");
        var summary = CompareFileDiffSerializer.Summarize(model);

        summary.Added.Should().Be(1);
        summary.Removed.Should().Be(0);
        summary.Modified.Should().Be(0);
        summary.Identical.Should().BeFalse();
    }

    [Fact]
    public void Summarize_counts_a_pure_deletion()
    {
        // One line removed on the new side, nothing added.
        var model = SideBySideDiffBuilder.Diff("a\nb\nc\n", "a\nc\n");
        var summary = CompareFileDiffSerializer.Summarize(model);

        summary.Removed.Should().Be(1);
        summary.Added.Should().Be(0);
        summary.Modified.Should().Be(0);
    }
}
