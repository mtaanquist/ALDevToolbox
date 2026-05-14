using ALDevToolbox.Components.Pages.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pins the file viewer inspector-panel navigation contract: pushing always
/// truncates forward history (browser back/forward semantics), popping just
/// moves the cursor without forgetting later entries until the next push,
/// and Reset returns the stack to a single outline view.
/// </summary>
public sealed class InspectorPanelHistoryTests
{
    [Fact]
    public void Starts_with_outline_view_at_cursor_zero()
    {
        var h = new InspectorPanelHistory();

        h.Stack.Should().ContainSingle();
        h.Stack[0].Should().BeOfType<OutlinePanelView>();
        h.Cursor.Should().Be(0);
        h.Current.Should().BeOfType<OutlinePanelView>();
    }

    [Fact]
    public void Push_appends_and_advances_cursor()
    {
        var h = new InspectorPanelHistory();

        h.Push(new ReferencesPanelView(SymbolId: 1, SymbolName: "Foo", SymbolKind: "procedure"));

        h.Stack.Should().HaveCount(2);
        h.Cursor.Should().Be(1);
        h.Current.Should().BeOfType<ReferencesPanelView>()
            .Which.SymbolName.Should().Be("Foo");
    }

    [Fact]
    public void PopTo_moves_cursor_without_shrinking_stack()
    {
        var h = new InspectorPanelHistory();
        h.Push(new ReferencesPanelView(1, "Foo", "procedure"));
        h.Push(new ReferencesPanelView(2, "Bar", "procedure"));

        h.PopTo(0);

        h.Cursor.Should().Be(0);
        h.Stack.Should().HaveCount(3); // outline + Foo + Bar still in history
        h.Current.Should().BeOfType<OutlinePanelView>();
    }

    [Fact]
    public void Push_after_pop_truncates_forward_history()
    {
        var h = new InspectorPanelHistory();
        h.Push(new ReferencesPanelView(1, "Foo", "procedure"));
        h.Push(new ReferencesPanelView(2, "Bar", "procedure"));
        h.PopTo(0);

        // Pushing while back at the outline should drop Foo and Bar.
        h.Push(new ReferencesPanelView(3, "Baz", "procedure"));

        h.Stack.Should().HaveCount(2);
        h.Cursor.Should().Be(1);
        ((ReferencesPanelView)h.Current).SymbolName.Should().Be("Baz");
    }

    [Fact]
    public void PopTo_out_of_range_is_a_noop()
    {
        var h = new InspectorPanelHistory();
        h.Push(new ReferencesPanelView(1, "Foo", "procedure"));

        h.PopTo(-1);
        h.Cursor.Should().Be(1);

        h.PopTo(99);
        h.Cursor.Should().Be(1);
    }

    [Fact]
    public void Reset_collapses_back_to_outline()
    {
        var h = new InspectorPanelHistory();
        h.Push(new ReferencesPanelView(1, "Foo", "procedure"));
        h.Push(new ReferencesPanelView(2, "Bar", "procedure"));

        h.Reset();

        h.Stack.Should().ContainSingle();
        h.Stack[0].Should().BeOfType<OutlinePanelView>();
        h.Cursor.Should().Be(0);
    }
}
