using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pure-function coverage for the source-file outline grouper. The flat
/// list returned by <see cref="ObjectExplorerService.GetFileOutlineAsync"/>
/// is rearranged into categorised sections (OBJECT / FIELDS / PROCEDURES
/// / LOCAL PROCEDURES / EVENT PUBLISHERS / EVENT SUBSCRIBERS / TRIGGERS)
/// and field-bound triggers are nested under their preceding field by
/// name. These tests pin that behaviour without bUnit since the grouper
/// is a static helper.
/// </summary>
public sealed class SourceFileOutlineGrouperTests
{
    private static SourceFileOutlineItem Item(string kind, string name, int line, string? signature = null, long? objectId = null)
        => new(kind, name, signature, line, objectId);

    [Fact]
    public void Groups_object_fields_procedures_into_separate_sections()
    {
        var input = new[]
        {
            Item("table", "Sales Header", 1, objectId: 36),
            Item("field", "Document Type", 5, "Enum"),
            Item("field", "No.", 10, "Code[20]"),
            Item("procedure", "GetCustomer", 100, "()"),
            Item("local_procedure", "ComputeTotal", 130, "()"),
        };

        var groups = SourceFileOutlineGrouper.Build(input, filter: null);

        groups.Select(g => g.Title).Should().Equal("OBJECT", "FIELDS", "PROCEDURES", "LOCAL PROCEDURES");
        groups.Single(g => g.Key == "object").Items.Should().HaveCount(1);
        groups.Single(g => g.Key == "fields").Items.Select(e => e.Item.Name).Should().Equal("Document Type", "No.");
        groups.Single(g => g.Key == "procedures").Items.Single().Item.Name.Should().Be("GetCustomer");
        groups.Single(g => g.Key == "local-procedures").Items.Single().Item.Name.Should().Be("ComputeTotal");
    }

    [Fact]
    public void Field_bound_triggers_nest_under_the_preceding_field()
    {
        // OnValidate after a field is field-bound; the entry should
        // appear in the FIELDS section with IsChild=true so the panel
        // renders it indented.
        var input = new[]
        {
            Item("table", "Sales Header", 1, objectId: 36),
            Item("field", "No.", 5, "Code[20]"),
            Item("trigger", "OnValidate", 7),
            Item("field", "Description", 12, "Text[100]"),
            Item("trigger", "OnLookup", 14),
            Item("trigger", "OnValidate", 16),
        };

        var fields = SourceFileOutlineGrouper.Build(input, filter: null)
            .Single(g => g.Key == "fields").Items;

        fields.Select(e => (e.Item.Name, e.IsChild)).Should().Equal(
            ("No.", false),
            ("OnValidate", true),
            ("Description", false),
            ("OnLookup", true),
            ("OnValidate", true));
    }

    [Fact]
    public void Object_level_triggers_land_in_the_triggers_section()
    {
        // OnInsert / OnModify / OnDelete / OnRename are unambiguously
        // table-level by name — they belong in their own section, not
        // nested under whichever field happened to come last.
        var input = new[]
        {
            Item("table", "Sales Header", 1, objectId: 36),
            Item("field", "No.", 5, "Code[20]"),
            Item("trigger", "OnValidate", 7),       // field-bound
            Item("trigger", "OnInsert", 200),       // table-level
            Item("trigger", "OnModify", 205),       // table-level
            Item("trigger", "OnDelete", 210),       // table-level
        };

        var groups = SourceFileOutlineGrouper.Build(input, filter: null);
        var triggers = groups.Single(g => g.Key == "triggers").Items;

        triggers.Select(e => e.Item.Name).Should().Equal("OnInsert", "OnModify", "OnDelete");
        triggers.Should().OnlyContain(e => !e.IsChild);

        // The OnValidate stayed nested in FIELDS.
        groups.Single(g => g.Key == "fields").Items
            .Should().Contain(e => e.Item.Name == "OnValidate" && e.IsChild);
    }

    [Fact]
    public void Page_level_triggers_classified_as_object_level()
    {
        var input = new[]
        {
            Item("page", "Customer Card", 1, objectId: 21),
            Item("trigger", "OnOpenPage", 100),
            Item("trigger", "OnClosePage", 110),
            Item("trigger", "OnAfterGetRecord", 120),
        };

        var triggers = SourceFileOutlineGrouper.Build(input, filter: null)
            .Single(g => g.Key == "triggers").Items;

        triggers.Should().HaveCount(3);
        triggers.Should().OnlyContain(e => !e.IsChild);
    }

    [Fact]
    public void Empty_sections_are_omitted()
    {
        var input = new[]
        {
            Item("codeunit", "Sales-Post", 1, objectId: 80),
            Item("procedure", "Post", 10),
        };

        var groups = SourceFileOutlineGrouper.Build(input, filter: null);

        groups.Select(g => g.Key).Should().Equal("object", "procedures");
        groups.Should().NotContain(g => g.Key == "fields" || g.Key == "triggers");
    }

    [Fact]
    public void Filter_narrows_items_case_insensitively()
    {
        var input = new[]
        {
            Item("table", "Sales Header", 1, objectId: 36),
            Item("field", "Document Type", 5, "Enum"),
            Item("field", "Sell-to Customer No.", 10, "Code[20]"),
            Item("procedure", "GetCustomer", 100, "()"),
            Item("procedure", "PostSalesHeader", 130, "()"),
        };

        var groups = SourceFileOutlineGrouper.Build(input, filter: "customer");

        // "Document Type" doesn't match "customer" → dropped.
        groups.Single(g => g.Key == "fields").Items.Select(e => e.Item.Name)
            .Should().Equal("Sell-to Customer No.");
        groups.Single(g => g.Key == "procedures").Items.Select(e => e.Item.Name)
            .Should().Equal("GetCustomer");
    }

    [Fact]
    public void Filter_that_drops_parent_field_promotes_its_triggers_to_object_level()
    {
        // When the user filters by a trigger name, the parent field is
        // filtered out — the trigger should still surface, but it can't
        // nest under a hidden parent, so it lands in the object-level
        // TRIGGERS section so it's discoverable.
        var input = new[]
        {
            Item("table", "Sales Header", 1, objectId: 36),
            Item("field", "Document Type", 5, "Enum"),
            Item("trigger", "OnValidate", 7),
            Item("field", "No.", 10, "Code[20]"),
        };

        var groups = SourceFileOutlineGrouper.Build(input, filter: "onvalidate");

        groups.Single(g => g.Key == "triggers").Items.Select(e => e.Item.Name)
            .Should().Equal("OnValidate");
        groups.Should().NotContain(g => g.Key == "fields");
    }

    [Fact]
    public void Event_publishers_and_subscribers_are_separate_sections()
    {
        var input = new[]
        {
            Item("codeunit", "Sales-Post", 1, objectId: 80),
            Item("event_publisher", "OnBeforePost", 50),
            Item("event_subscriber", "HandleOnAfterInsertCustomer", 100),
            Item("procedure", "Post", 200),
        };

        var groups = SourceFileOutlineGrouper.Build(input, filter: null);

        groups.Select(g => g.Title).Should()
            .Equal("OBJECT", "PROCEDURES", "EVENT PUBLISHERS", "EVENT SUBSCRIBERS");
    }

    [Fact]
    public void Empty_input_returns_no_groups()
    {
        var groups = SourceFileOutlineGrouper.Build(Array.Empty<SourceFileOutlineItem>(), filter: null);
        groups.Should().BeEmpty();
    }
}
