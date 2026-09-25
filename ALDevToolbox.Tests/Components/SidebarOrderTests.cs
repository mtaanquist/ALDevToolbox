using ALDevToolbox.Domain.Navigation;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The ordering rule behind the arrangeable sidebar (issue #956): a saved order
/// is a preference applied to what the person may see, never a list of what to
/// show. <see cref="NavMenuArrangeTests"/> covers the same rule through the
/// rendered sidebar.
/// </summary>
public sealed class SidebarOrderTests
{
    private static readonly string[] Shipped = ["templates", "cookbook", "object-explorer"];

    [Fact]
    public void Saved_order_is_applied()
    {
        SidebarOrder.Apply(Shipped, ["object-explorer", "templates", "cookbook"])
            .Should().Equal("object-explorer", "templates", "cookbook");
    }

    [Fact]
    public void Keys_the_saved_order_does_not_name_are_appended_in_shipped_order()
    {
        // A tool that appeared after the person arranged the sidebar lands at the
        // end of its group rather than being lost.
        SidebarOrder.Apply(["templates", "cookbook", "object-explorer", "recipes"], ["object-explorer"])
            .Should().Equal("object-explorer", "templates", "cookbook", "recipes");
    }

    [Fact]
    public void Saved_keys_that_are_not_available_are_dropped()
    {
        // Cookbook is switched off for this org: its saved place does not bring it back.
        SidebarOrder.Apply(["templates", "object-explorer"], ["cookbook", "object-explorer", "templates"])
            .Should().Equal("object-explorer", "templates");
    }

    [Fact]
    public void Repeated_saved_keys_count_once()
    {
        SidebarOrder.Apply(Shipped, ["cookbook", "cookbook", "templates", "cookbook"])
            .Should().Equal("cookbook", "templates", "object-explorer");
    }

    [Fact]
    public void No_saved_order_keeps_the_shipped_order()
    {
        SidebarOrder.Apply(Shipped, []).Should().Equal(Shipped);
    }

    [Fact]
    public void Parse_reads_the_group_order_and_each_groups_items()
    {
        var order = SidebarOrder.Parse("deliver,text,build|deliver:pipelines,solutions|build:cookbook");

        order.Groups.Should().Equal("deliver", "text", "build");
        order.ItemsOf("deliver").Should().Equal("pipelines", "solutions");
        order.ItemsOf("build").Should().Equal("cookbook");
        order.ItemsOf("text").Should().BeEmpty("no item order was saved for it");
    }

    [Fact]
    public void Parse_accepts_the_value_url_encoded_as_the_script_writes_it()
    {
        var order = SidebarOrder.Parse(Uri.EscapeDataString("text,build|text:piper,diff"));

        order.Groups.Should().Equal("text", "build");
        order.ItemsOf("text").Should().Equal("piper", "diff");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%E0%A4%A")]
    [InlineData("|||:::,,,")]
    [InlineData("<script>|build:<b>")]
    [InlineData("BUILD,Text|:cookbook")]
    public void Garbage_falls_back_to_the_shipped_order(string? raw)
    {
        var order = SidebarOrder.Parse(raw);

        order.Groups.Should().BeEmpty();
        order.ItemsOf("build").Should().BeEmpty();
        SidebarOrder.Apply(Shipped, order.ItemsOf("build")).Should().Equal(Shipped);
    }

    [Fact]
    public void Parse_keeps_the_well_formed_parts_of_a_partly_garbled_value()
    {
        var order = SidebarOrder.Parse("deliver,<x>,build|nonsense|text:piper,DIFF,diff");

        order.Groups.Should().Equal("deliver", "build");
        order.ItemsOf("text").Should().Equal("piper", "diff");
    }

    [Fact]
    public void An_oversized_value_is_ignored()
    {
        SidebarOrder.Parse(new string('a', 5000)).Groups.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_round_trips_through_Parse()
    {
        var text = SidebarOrder.Serialize(
        [
            ("deliver", ["teams", "solutions"]),
            ("build", ["cookbook"]),
        ]);

        text.Should().Be("deliver,build|deliver:teams,solutions|build:cookbook");
        var order = SidebarOrder.Parse(text);
        order.Groups.Should().Equal("deliver", "build");
        order.ItemsOf("deliver").Should().Equal("teams", "solutions");
    }
}
