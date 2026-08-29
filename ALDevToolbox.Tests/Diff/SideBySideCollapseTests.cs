using ALDevToolbox.Services.Diff;
using DiffPlex.DiffBuilder;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Diff;

/// <summary>
/// Covers collapsing the unchanged stretches of a SIDE-BY-SIDE diff (#579's
/// harder half). The inline view gets the same effect by never emitting those
/// lines; here both panes hold real files, so the lines have to be hidden — and
/// hidden in a way that leaves the two panes facing each other.
///
/// That is the invariant everything below is really about: <b>the two panes
/// must hide the same number of rows.</b> They are kept in step by blank filler
/// rows measured against the full text, so a pane that hides one row more than
/// its counterpart puts every line beneath the gap opposite the wrong line —
/// silently, and worse the further down you read. What makes it work is that a
/// collapsed run is unchanged on BOTH sides by construction, so its rows pair
/// one-to-one; the line numbers differ between panes but the row count cannot.
///
/// The other thing that fails quietly is the pairing itself. Expanding is a
/// pair operation, so a region's index has to mean the same thing in both
/// panes — mismatch them and clicking a band opens one pane's gap and some
/// other gap in the other.
/// </summary>
public sealed class SideBySideCollapseTests
{
    /// <summary>Twenty lines, one changed in the middle, plus an insertion further down.</summary>
    private static (string Left, string Right) Sample()
    {
        var left = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var right = left
            .Replace("line10\n", "CHANGED\n")
            .Replace("line30\n", "line30\nINSERTED\n");
        return (left, right);
    }

    [Fact]
    public void Both_panes_hide_the_same_number_of_rows()
    {
        var (left, right) = Sample();
        var (old, neu) = SideBySideCollapse.Build(SideBySideDiffBuilder.Diff(left, right));

        old.Should().HaveCountGreaterThan(0);
        neu.Should().HaveCount(old.Count, "the panes collapse as a pair or not at all");

        foreach (var (o, n) in old.Zip(neu))
        {
            o.Index.Should().Be(n.Index, "an index is how the two panes recognise the same band");
            var hiddenOld = o.From is int of && o.To is int ot ? ot - of + 1 : 0;
            var hiddenNew = n.From is int nf && n.To is int nt ? nt - nf + 1 : 0;
            hiddenNew.Should().Be(hiddenOld,
                because: "a collapsed run is unchanged on both sides, so its rows pair one-to-one — "
                         + "hide a different count and every line below faces the wrong counterpart");
        }
    }

    [Fact]
    public void The_hidden_range_is_in_each_panes_own_line_numbers()
    {
        // The right side gains a line at 31, so from that point its numbers run
        // one ahead of the left's. A band below the insertion must say so.
        var (left, right) = Sample();
        var (old, neu) = SideBySideCollapse.Build(SideBySideDiffBuilder.Diff(left, right));

        var pairs = old.Zip(neu).Where(p => p.First.From is not null).ToList();
        pairs.Should().NotBeEmpty();
        pairs.Should().Contain(p => p.First.From != p.Second.From,
            because: "an insertion above a band shifts the right pane's numbering past it");
    }

    [Fact]
    public void The_bands_hide_the_stretches_between_the_changes()
    {
        var (left, right) = Sample();
        var (old, _) = SideBySideCollapse.Build(SideBySideDiffBuilder.Diff(left, right));

        // Lines 1-6 are more than three from the first change, so they go.
        old.Should().Contain(r => r.From == 1 && r.To == 6);
        // Nothing kept can also be hidden.
        var hidden = old.Where(r => r.From is not null)
            .SelectMany(r => Enumerable.Range(r.From!.Value, r.To!.Value - r.From.Value + 1)).ToList();
        hidden.Should().NotContain(10, "the changed line is the point");
        hidden.Should().NotContain(9, "…and its context");
        hidden.Should().NotContain(13);
        hidden.Should().OnlyHaveUniqueItems("no line is hidden by two bands");
    }

    [Fact]
    public void The_first_band_hides_nothing_when_the_diff_opens_on_a_change()
    {
        // Change on line 1: there is no run above it to stand in for the
        // banner, but the handoff still shows one.
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";
        var (old, neu) = SideBySideCollapse.Build(
            SideBySideDiffBuilder.Diff(left, left.Replace("line1\n", "CHANGED\n")));

        var first = old[0];
        first.From.Should().BeNull();
        first.Before.Should().Be(1, "a band that hides nothing has to anchor above a line");
        first.Header.Should().StartWith("@@ -1,");
        neu[0].Before.Should().Be(1);
    }

    [Fact]
    public void The_run_past_the_last_change_says_what_it_is_hiding()
    {
        // Nothing follows it, so there is no hunk to announce.
        var left = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var (old, _) = SideBySideCollapse.Build(
            SideBySideDiffBuilder.Diff(left, left.Replace("line5\n", "CHANGED\n")));

        var last = old[^1];
        last.From.Should().NotBeNull();
        last.Header.Should().MatchRegex(@"^\.\.\. \d+ unchanged lines$");
    }

    [Fact]
    public void One_hidden_line_is_not_called_lines()
    {
        var left = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}")) + "\n";
        // The change at line 6 keeps 3..9; line 10 and the trailing empty line
        // are what is left, and lines 1-2 go at the top.
        var (old, _) = SideBySideCollapse.Build(
            SideBySideDiffBuilder.Diff(left, left.Replace("line6\n", "CHANGED\n")));

        old.Where(r => r.Header.StartsWith("..."))
            .Should().OnlyContain(r => !r.Header.EndsWith("1 unchanged lines"));
    }

    [Fact]
    public void An_identical_pair_collapses_nothing()
    {
        var text = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var (old, neu) = SideBySideCollapse.Build(SideBySideDiffBuilder.Diff(text, text));

        old.Should().BeEmpty("a file with no changes read as a failure to load when it collapsed to nothing");
        neu.Should().BeEmpty();
    }

    [Fact]
    public void A_diff_with_no_room_to_collapse_still_banners_its_hunk()
    {
        var (old, neu) = SideBySideCollapse.Build(SideBySideDiffBuilder.Diff("a\nb\n", "a\nX\n"));

        old.Should().ContainSingle().Which.From.Should().BeNull();
        neu.Should().ContainSingle();
    }

    [Fact]
    public void The_banner_names_the_declaration_around_the_change()
    {
        var left = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var model = SideBySideDiffBuilder.Diff(left, left.Replace("line20\n", "CHANGED\n"));

        var (old, _) = SideBySideCollapse.Build(model, [
            new UnifiedDiffSerializer.Declaration(1, 40, "TheWholeObject"),
            new UnifiedDiffSerializer.Declaration(18, 25, "TheProcedure"),
        ]);

        old.Should().Contain(r => r.Header.EndsWith("TheProcedure"),
            because: "the hunk's first line is context three rows earlier and can sit outside it");
    }

    [Fact]
    public void The_serialised_form_omits_the_nulls()
    {
        var (left, right) = Sample();
        var (old, _) = SideBySideCollapse.Serialize(SideBySideDiffBuilder.Diff(left, right));

        old.Should().StartWith("[{").And.Contain("\"header\"");
        old.Should().NotContain("null", "a band either hides lines or anchors above one, never both");
    }
}
