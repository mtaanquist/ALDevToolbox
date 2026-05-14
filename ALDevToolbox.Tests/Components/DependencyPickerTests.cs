using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the catalogue-vs-manual model in <see cref="DependencyPicker"/>:
/// the component owns no state of its own; every interaction must
/// surface through <c>ValueChanged</c> so the parent's persisted list
/// stays the source of truth. The empty-catalogue, toggle, and manual-
/// add validation branches are all visible here even though the parent
/// never sees them — bUnit pins them so a refactor that mutates internal
/// state stops working.
/// </summary>
public sealed class DependencyPickerTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public DependencyPickerTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static WellKnownDependency CatalogEntry(string id, string name, string publisher, string version, string? category = null) => new()
    {
        OrganizationId = 1,
        DepId = id,
        DepName = name,
        DepPublisher = publisher,
        DepVersionDefault = version,
        Category = category,
        Ordering = 0,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Empty_catalogue_renders_a_caption_telling_users_to_add_manual_or_ask_an_admin()
    {
        var cut = _ctx.RenderComponent<DependencyPicker>(p => p
            .Add(c => c.Catalog, Array.Empty<WellKnownDependency>())
            .Add(c => c.Value, Array.Empty<DependencyEntry>()));

        cut.Markup.Should().Contain("No catalogue entries available");
        cut.Markup.Should().Contain("Add manual dependency",
            "the manual-add section must always render so the empty catalogue "
            + "isn't a dead end");
    }

    [Fact]
    public void Catalogue_groups_render_one_section_per_category_sorted_alphabetically()
    {
        var catalog = new[]
        {
            CatalogEntry("11111111-1111-1111-1111-111111111111", "ForNAV Core", "ForNAV", "1.0.0.0", category: "ForNAV"),
            CatalogEntry("22222222-2222-2222-2222-222222222222", "Continia Doc", "Continia", "1.0.0.0", category: "Continia"),
            CatalogEntry("33333333-3333-3333-3333-333333333333", "Loose", "X", "1.0.0.0", category: null),
        };

        var cut = _ctx.RenderComponent<DependencyPicker>(p => p
            .Add(c => c.Catalog, catalog)
            .Add(c => c.Value, Array.Empty<DependencyEntry>()));

        var groupNames = cut.FindAll("div.dep-picker__category-name")
            .Select(e => e.TextContent.Trim())
            .ToList();
        groupNames.Should().Equal(new[] { "Continia", "ForNAV", "Other" },
            "groups sort by category name; null/whitespace folds into 'Other'");
    }

    [Fact]
    public void Toggling_a_catalogue_checkbox_calls_ValueChanged_with_the_new_entry()
    {
        var item = CatalogEntry("44444444-4444-4444-4444-444444444444", "X", "Pub", "1.0.0.0", category: "Other");
        List<DependencyEntry>? observed = null;

        var cut = _ctx.RenderComponent<DependencyPicker>(p => p
            .Add(c => c.Catalog, new[] { item })
            .Add(c => c.Value, Array.Empty<DependencyEntry>())
            .Add(c => c.ValueChanged, v => observed = v));

        cut.Find("div.dep-picker__rows input[type=checkbox]").Change(true);

        observed.Should().NotBeNull();
        observed!.Should().ContainSingle(d =>
            d.DepId == item.DepId
            && d.DepName == "X"
            && d.DepPublisher == "Pub"
            && d.DepVersion == "1.0.0.0",
            "ticking a catalogue checkbox must round-trip through ValueChanged — "
            + "the component owns no state");
    }

    [Fact]
    public void Catalogue_entry_already_selected_renders_the_version_input_alongside_the_checkbox()
    {
        var item = CatalogEntry("55555555-5555-5555-5555-555555555555", "Y", "Pub", "1.0.0.0", category: "Other");
        var selected = new[] { new DependencyEntry(item.DepId, "Y", "Pub", "2.5.0.0") };

        var cut = _ctx.RenderComponent<DependencyPicker>(p => p
            .Add(c => c.Catalog, new[] { item })
            .Add(c => c.Value, selected));

        cut.Find("label.dep-row").GetAttribute("class").Should().Contain("dep-row--selected");
        cut.Find("input.dep-row__version").GetAttribute("value").Should().Be("2.5.0.0",
            "the version input lets users override the catalogue's default; the "
            + "current Value must round-trip back into the DOM");
    }

    [Fact]
    public void Manual_add_rejects_blank_fields_with_an_inline_error_and_does_not_emit_ValueChanged()
    {
        bool emitted = false;
        var cut = _ctx.RenderComponent<DependencyPicker>(p => p
            .Add(c => c.Catalog, Array.Empty<WellKnownDependency>())
            .Add(c => c.Value, Array.Empty<DependencyEntry>())
            .Add(c => c.ValueChanged, _ => emitted = true));

        cut.Find("div.dep-picker__manual button.btn").Click();

        emitted.Should().BeFalse(
            "validation must fail closed — empty fields must not silently emit");
        cut.Find("p.dep-picker__error").TextContent.Should().Contain("required");
    }

    [Fact]
    public async Task Manual_add_with_valid_fields_emits_ValueChanged_and_clears_the_inputs()
    {
        List<DependencyEntry>? observed = null;
        var cut = _ctx.RenderComponent<DependencyPicker>(p => p
            .Add(c => c.Catalog, Array.Empty<WellKnownDependency>())
            .Add(c => c.Value, Array.Empty<DependencyEntry>())
            .Add(c => c.ValueChanged, v => observed = v));

        var inputs = cut.FindAll("div.dep-picker__manual input[type=text]");
        inputs[0].Change("66666666-6666-6666-6666-666666666666");
        inputs[1].Change("Custom");
        inputs[2].Change("PubCo");
        inputs[3].Change("1.0.0.0");

        await cut.InvokeAsync(() => cut.Find("div.dep-picker__manual button.btn").Click());

        observed.Should().NotBeNull();
        observed!.Should().ContainSingle(d =>
            d.DepId == "66666666-6666-6666-6666-666666666666"
            && d.DepName == "Custom"
            && d.DepPublisher == "PubCo");

        cut.FindAll("p.dep-picker__error").Should().BeEmpty(
            "the previous error message must clear once a valid entry is added");
    }

    [Fact]
    public void Manual_add_rejects_non_guid_dep_id()
    {
        bool emitted = false;
        var cut = _ctx.RenderComponent<DependencyPicker>(p => p
            .Add(c => c.Catalog, Array.Empty<WellKnownDependency>())
            .Add(c => c.Value, Array.Empty<DependencyEntry>())
            .Add(c => c.ValueChanged, _ => emitted = true));

        var inputs = cut.FindAll("div.dep-picker__manual input[type=text]");
        inputs[0].Change("not-a-guid");
        inputs[1].Change("Custom");
        inputs[2].Change("PubCo");
        inputs[3].Change("1.0.0.0");

        cut.Find("div.dep-picker__manual button.btn").Click();

        emitted.Should().BeFalse();
        cut.Find("p.dep-picker__error").TextContent.Should().Contain("GUID",
            "the server validates dep_id as a GUID — the form mirrors that "
            + "rule client-side so the user sees the failure inline");
    }
}
