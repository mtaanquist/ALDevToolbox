using ALDevToolbox.Services.Diff;
using DiffPlex.DiffBuilder;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Diff;

/// <summary>
/// Pure-function coverage for the diff payload that <c>OeCompareFile.razor</c>
/// and the Diff tool hand to the source-viewer JS via <c>data-diff</c>. The non-trivial part
/// is that DiffPlex's <c>SideBySideDiffModel</c> pads each pane with
/// <c>Imaginary</c> placeholder rows to align with the opposite side. The
/// CodeMirror viewer renders only the actual source content (no imaginaries),
/// so the emitted line numbers must be source positions — not pane indices —
/// or the decorations drift as soon as one side has more insertions than the
/// other has deletions above that point.
/// </summary>
public sealed class SideBySideDiffSerializerTests
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
        var json = SideBySideDiffSerializer.SerializeSide(model.NewText);

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
        var leftJson = SideBySideDiffSerializer.SerializeSide(model.OldText);

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
        var json = SideBySideDiffSerializer.SerializeSide(model.NewText);

        json.Should().Be("[]");
    }

    [Fact]
    public void Maps_change_types_to_lowercase_kind_strings()
    {
        const string left  = "keep\nold\n";
        const string right = "keep\nnew\n";

        var model = SideBySideDiffBuilder.Diff(left, right);
        var leftJson = SideBySideDiffSerializer.SerializeSide(model.OldText);
        var rightJson = SideBySideDiffSerializer.SerializeSide(model.NewText);

        // Single-line replace at the bottom: DiffPlex classifies as either
        // a paired Modified or a Deleted/Inserted pair depending on the
        // similarity threshold. Either is acceptable; both kinds are
        // lowercase strings the JS understands.
        var combined = leftJson + rightJson;
        combined.Should().MatchRegex("\"kind\":\"(modified|deleted|inserted)\"");
    }

    [Fact]
    public void SerializeFillers_pads_the_left_side_where_the_right_inserts_lines()
    {
        // Right inserts two lines near the top. The left pane is "missing"
        // those two lines, so it needs a filler of size 2 before the line that
        // follows them (line2, at left source position 2).
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

        var leftFillers = SideBySideDiffSerializer.SerializeFillers(model.OldText);
        leftFillers.Should().Contain("\"before\":2").And.Contain("\"size\":2");

        // The right side is the longer one here — nothing to pad.
        var rightFillers = SideBySideDiffSerializer.SerializeFillers(model.NewText);
        rightFillers.Should().Be("[]");
    }

    [Fact]
    public void SerializeFillers_pads_the_right_side_where_the_left_deletes_lines()
    {
        // Left has two lines the right doesn't — symmetric to the insertion
        // case, so the right pane gets the filler this time.
        const string left =
            "alpha\n" +
            "GONE_A\n" +
            "GONE_B\n" +
            "beta\n";
        const string right =
            "alpha\n" +
            "beta\n";

        var model = SideBySideDiffBuilder.Diff(left, right);

        var rightFillers = SideBySideDiffSerializer.SerializeFillers(model.NewText);
        rightFillers.Should().Contain("\"before\":2").And.Contain("\"size\":2");

        var leftFillers = SideBySideDiffSerializer.SerializeFillers(model.OldText);
        leftFillers.Should().Be("[]");
    }

    [Fact]
    public void SerializeFillers_anchors_a_trailing_gap_past_the_last_source_line()
    {
        // Right appends two lines past the end of the left file. There's no
        // following real row on the left to anchor to, so the gap uses the
        // sentinel `before = lineCount + 1` (left has 2 lines → before 3).
        const string left = "a\nb\n";
        const string right = "a\nb\nc\nd\n";

        var model = SideBySideDiffBuilder.Diff(left, right);

        var leftFillers = SideBySideDiffSerializer.SerializeFillers(model.OldText);
        leftFillers.Should().Contain("\"before\":3").And.Contain("\"size\":2");
    }

    [Fact]
    public void SerializeFillers_emits_nothing_for_identical_files()
    {
        var model = SideBySideDiffBuilder.Diff("a\nb\nc\n", "a\nb\nc\n");

        SideBySideDiffSerializer.SerializeFillers(model.OldText).Should().Be("[]");
        SideBySideDiffSerializer.SerializeFillers(model.NewText).Should().Be("[]");
    }

    [Fact]
    public void SerializeWordDiff_emits_the_changed_word_ranges_of_a_modified_line()
    {
        // A one-word edit inside an otherwise identical line: only the word
        // run that differs should be emitted, with 1-based columns and an
        // exclusive `to`. "keep" occupies columns 1-4 on both sides and must
        // NOT appear in the payload.
        const string left  = "keep old tail\n";
        const string right = "keep new tail\n";

        var model = SideBySideDiffBuilder.Diff(left, right);
        var leftJson = SideBySideDiffSerializer.SerializeWordDiff(model.OldText);
        var rightJson = SideBySideDiffSerializer.SerializeWordDiff(model.NewText);

        // DiffPlex only sub-diffs Modified lines; a single-line replace can
        // classify as Deleted/Inserted instead, in which case both payloads
        // are legitimately empty (the whole line is the change).
        if (leftJson == "[]" && rightJson == "[]") return;

        // "old" / "new" start at column 6 (after "keep "). The unchanged
        // prefix must not be marked.
        leftJson.Should().Contain("\"line\":1").And.Contain("\"from\":6");
        rightJson.Should().Contain("\"line\":1").And.Contain("\"from\":6");
        leftJson.Should().NotContain("\"from\":1");
        rightJson.Should().NotContain("\"from\":1");
    }

    [Fact]
    public void SerializeWordDiff_emits_nothing_for_identical_files()
    {
        var model = SideBySideDiffBuilder.Diff("a\nb\n", "a\nb\n");

        SideBySideDiffSerializer.SerializeWordDiff(model.OldText).Should().Be("[]");
        SideBySideDiffSerializer.SerializeWordDiff(model.NewText).Should().Be("[]");
    }

    [Fact]
    public void Summarize_reports_identical_when_no_changes()
    {
        var model = SideBySideDiffBuilder.Diff("a\nb\nc\n", "a\nb\nc\n");
        var summary = SideBySideDiffSerializer.Summarize(model);

        summary.Identical.Should().BeTrue();
        summary.Total.Should().Be(0);
    }

    [Fact]
    public void Summarize_counts_a_pure_insertion()
    {
        // One line appended on the new side, nothing removed.
        var model = SideBySideDiffBuilder.Diff("a\nb\n", "a\nb\nc\n");
        var summary = SideBySideDiffSerializer.Summarize(model);

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
        var summary = SideBySideDiffSerializer.Summarize(model);

        summary.Removed.Should().Be(1);
        summary.Added.Should().Be(0);
        summary.Modified.Should().Be(0);
    }
}
