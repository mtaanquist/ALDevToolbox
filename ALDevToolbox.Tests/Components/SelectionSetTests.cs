using ALDevToolbox.Components.Shared;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The selection behind the Environments list and the Upgrades page (#985). The named
/// user is somebody on the upgrade team assembling eight customers for one evening's
/// slot, finding each by short name: a search must never untick what they already
/// found, the header checkbox must only reach the rows it sits over, and the page must
/// be able to say how much of the selection is off screen.
/// </summary>
public sealed class SelectionSetTests
{
    [Fact]
    public void A_tick_off_screen_stays_ticked_and_the_count_says_how_many_are_shown()
    {
        var selection = new SelectionSet();
        foreach (var id in new[] { 1, 2, 3 }) selection.Set(id, true);

        // A search now shows only row 4.
        selection.Set(4, true);

        selection.Count.Should().Be(4);
        selection.Summary([4]).Should().Be("4 selected, 1 shown");
        selection.Summary([1, 2, 3, 4, 5]).Should().Be("4 selected", "every tick is on screen");
    }

    [Fact]
    public void The_header_box_ticks_and_unticks_only_the_rows_shown()
    {
        var selection = new SelectionSet();
        selection.Set(1, true);

        selection.ToggleShown([2, 3], on: true);
        selection.Ids.Should().BeEquivalentTo([1, 2, 3]);
        selection.AllShownPicked([2, 3]).Should().BeTrue();

        selection.ToggleShown([2, 3], on: false);
        selection.Ids.Should().BeEquivalentTo([1], "the tick off screen is not the header box's to take");
        selection.AllShownPicked([2, 3]).Should().BeFalse();
        selection.SomeShownPicked([2, 3]).Should().BeFalse("a tick off screen does not put a dash in the box");
    }

    [Fact]
    public void The_header_box_shows_a_dash_only_when_some_of_the_rows_shown_are_ticked()
    {
        var selection = new SelectionSet();
        selection.Set(2, true);

        selection.SomeShownPicked([2, 3]).Should().BeTrue();
        selection.AllShownPicked([2, 3]).Should().BeFalse();
        selection.AllShownPicked([2]).Should().BeTrue();
        selection.SomeShownPicked([2]).Should().BeFalse();
        selection.AllShownPicked([]).Should().BeFalse("an empty table has nothing to tick");
    }

    [Fact]
    public void Only_a_row_that_has_gone_loses_its_tick()
    {
        var selection = new SelectionSet();
        selection.ToggleShown([1, 2, 3], on: true);

        selection.Prune([1, 3, 4]);

        selection.Ids.Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public void Show_selected_shows_every_ticked_row_whatever_the_filters_say()
    {
        var all = new[] { 1, 2, 3, 4, 5 };
        var selection = new SelectionSet();
        selection.Set(1, true);
        selection.Set(4, true);
        var filters = ("fab", "Production");

        selection.ShowSelected(true, filters);

        selection.ShowSelectedOnly.Should().BeTrue();
        selection.Shown([5], all, id => id, filters).Should().Equal(1, 4);
    }

    [Fact]
    public void Changing_a_filter_ends_show_selected_and_keeps_the_ticks()
    {
        var all = new[] { 1, 2, 3 };
        var selection = new SelectionSet();
        selection.Set(1, true);
        selection.ShowSelected(true, ("fab", ""));

        selection.Shown([2, 3], all, id => id, ("fabr", "")).Should().Equal(2, 3);

        selection.ShowSelectedOnly.Should().BeFalse();
        selection.Contains(1).Should().BeTrue();
    }

    [Fact]
    public void Show_selected_ends_with_the_last_tick_and_does_not_come_back_with_the_next()
    {
        var selection = new SelectionSet();
        selection.Set(1, true);
        selection.ShowSelected(true, "all");

        selection.Set(1, false);
        selection.ShowSelectedOnly.Should().BeFalse();

        selection.Set(2, true);
        selection.ShowSelectedOnly.Should().BeFalse("a new selection starts on the filtered table");
    }

    [Fact]
    public void Clear_unticks_everything_on_screen_or_not_and_leaves_show_selected()
    {
        var selection = new SelectionSet();
        selection.ToggleShown([1, 2], on: true);
        selection.ShowSelected(true, "all");

        selection.Clear();

        selection.Count.Should().Be(0);
        selection.ShowSelectedOnly.Should().BeFalse();
    }
}
