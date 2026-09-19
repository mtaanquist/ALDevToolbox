using ALDevToolbox.Components.Shared.Archetypes;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the structure the shared page primitives render: the class names and the
/// slot order the byte-locked handoff sheets expect. The CSS cannot be edited to
/// suit a drifted component, so the markup is the contract - and since every page
/// will compose these, a slip here is a slip on every page at once.
/// </summary>
public sealed class ArchetypePrimitivesTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ArchetypePrimitivesTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static RenderFragment Html(string markup) => b => b.AddMarkupContent(0, markup);

    // ---- PageHead ----------------------------------------------------------

    [Fact]
    public void PageHead_renders_crumbs_title_and_subtitle_in_one_column_then_actions()
    {
        var cut = _ctx.Render<PageHead>(p => p
            .Add(c => c.Title, "Environments")
            .Add(c => c.Subtitle, "Every environment you can reach.")
            .Add(c => c.Crumbs, Html("<a href=\"/\">Home</a>"))
            .Add(c => c.Actions, Html("<button class=\"btn\">Export</button>")));

        var head = cut.Find("header.page-head");
        head.ClassList.Should().NotContain("page-head--sticky");
        head.Children.Select(e => e.ClassName ?? "").Should().Equal("", "page-head__actions");

        // The unclassed left column is what keeps the three from becoming flex columns.
        head.Children[0].Children.Select(e => e.ClassName).Should()
            .Equal("page-head__crumbs", "page-head__title", "page-head__sub");
        cut.Find("nav.page-head__crumbs").GetAttribute("aria-label").Should().Be("Breadcrumb");
        cut.Find("h1.page-head__title").TextContent.Should().Be("Environments");
        cut.Find("p.page-head__sub").TextContent.Should().Be("Every environment you can reach.");
        cut.Find(".page-head__actions button").TextContent.Should().Be("Export");
    }

    [Fact]
    public void PageHead_draws_a_trail_as_links_with_a_chevron_between_and_the_page_last()
    {
        var cut = _ctx.Render<PageHead>(p => p
            .Add(c => c.Title, "Modules")
            .Add(c => c.Trail, [new Crumb("Admin", "/admin"), new Crumb("Content & more", "/admin/content"), new Crumb("Modules")]));

        var nav = cut.Find("nav.page-head__crumbs[aria-label=Breadcrumb]");
        nav.Children.Select(c => c.TagName.ToLowerInvariant()).Should().Equal("a", "svg", "a", "svg", "span");
        nav.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().Equal("/admin", "/admin/content");
        // A label is text, never markup: the ampersand arrives encoded once.
        nav.QuerySelectorAll("a")[1].TextContent.Should().Be("Content & more");
        nav.QuerySelector("span")!.TextContent.Should().Be("Modules");
    }

    [Fact]
    public void PageHead_with_an_empty_trail_draws_no_nav_and_a_fragment_wins_over_a_trail()
    {
        _ctx.Render<PageHead>(p => p.Add(c => c.Title, "Modules").Add(c => c.Trail, []))
            .FindAll("nav").Should().BeEmpty();

        var both = _ctx.Render<PageHead>(p => p
            .Add(c => c.Title, "Modules")
            .Add(c => c.Trail, [new Crumb("Admin", "/admin")])
            .Add(c => c.Crumbs, "<a href=\"/x\">Own</a>"));
        both.FindAll("nav").Should().ContainSingle();
        both.Find("nav a").TextContent.Should().Be("Own");
    }

    [Fact]
    public void PageHead_omits_the_optional_parts_rather_than_rendering_them_empty()
    {
        var cut = _ctx.Render<PageHead>(p => p.Add(c => c.Title, "Releases"));

        cut.FindAll(".page-head__crumbs").Should().BeEmpty();
        cut.FindAll(".page-head__sub").Should().BeEmpty();
        cut.FindAll(".page-head__actions").Should().BeEmpty();
    }

    [Fact]
    public void PageHead_subtitle_content_carries_markup_and_wins_over_the_string()
    {
        var cut = _ctx.Render<PageHead>(p => p
            .Add(c => c.Title, "Releases")
            .Add(c => c.Subtitle, "plain")
            .Add(c => c.SubtitleContent, Html("Publishes <code>.app</code> files")));

        cut.FindAll("p.page-head__sub").Should().ContainSingle();
        cut.Find("p.page-head__sub code").TextContent.Should().Be(".app");
    }

    [Fact]
    public void PageHead_hides_actions_when_the_page_says_so_and_pins_when_sticky()
    {
        var cut = _ctx.Render<PageHead>(p => p
            .Add(c => c.Title, "Releases")
            .Add(c => c.Sticky, true)
            .Add(c => c.ShowActions, false)
            .Add(c => c.Actions, Html("<button class=\"btn\">New</button>")));

        cut.Find("header.page-head").ClassList.Should().Contain("page-head--sticky");
        cut.FindAll(".page-head__actions").Should().BeEmpty();
    }

    // ---- EmptyState --------------------------------------------------------

    [Fact]
    public void EmptyState_renders_all_four_slots_in_handoff_order()
    {
        var cut = _ctx.Render<EmptyState>(p => p
            .Add(c => c.Icon, "server")
            .Add(c => c.Title, "No environments to show yet")
            .Add(c => c.Text, Html("Connect a solution first."))
            .Add(c => c.Action, Html("<a class=\"btn btn--primary\" href=\"/solutions\">Go to solutions</a>")));

        var root = cut.Find("div.empty-state");
        root.ClassList.Should().NotContain("empty-state--quiet");
        root.Children.Select(e => (e.TagName, e.ClassName)).Should().Equal(
            ("SPAN", "empty-state__icon"),
            ("SPAN", "empty-state__title"),
            ("SPAN", "empty-state__text"),
            ("SPAN", "empty-state__action"));
        cut.Find(".empty-state__title").TextContent.Should().Be("No environments to show yet");
        // The class sits on the wrapper, not the button: .empty-state__action only adds margin.
        cut.Find(".empty-state__action > a.btn").ClassList.Should().NotContain("empty-state__action");
    }

    [Fact]
    public void EmptyState_without_icon_or_action_renders_neither_wrapper()
    {
        var cut = _ctx.Render<EmptyState>(p => p
            .Add(c => c.Title, "No updates scheduled")
            .Add(c => c.Quiet, true));

        cut.Find("div.empty-state").ClassList.Should().Contain("empty-state--quiet");
        cut.Find("div.empty-state").Children.Select(e => e.ClassName).Should().Equal("empty-state__title");
    }

    // ---- LoadingBlock ------------------------------------------------------

    [Fact]
    public void LoadingBlock_renders_the_spinner_then_a_default_caption()
    {
        var cut = _ctx.Render<LoadingBlock>();

        var root = cut.Find("div.loading-block");
        root.ClassList.Should().NotContain("loading-block--under-table");
        root.Children.Should().HaveCount(2);
        root.Children[0].ClassName.Should().Be("spinner");
        root.Children[1].TextContent.Should().Be("Loading...");
    }

    [Fact]
    public void LoadingBlock_under_a_skeleton_table_takes_the_modifier_and_the_given_text()
    {
        var cut = _ctx.Render<LoadingBlock>(p => p
            .Add(c => c.Text, "Loading release pipelines...")
            .Add(c => c.UnderTable, true));

        cut.Find("div.loading-block").ClassList.Should().Contain("loading-block--under-table");
        cut.Find("div.loading-block").TextContent.Should().Be("Loading release pipelines...");
    }

    // ---- FilterBar ---------------------------------------------------------

    [Fact]
    public void FilterBar_orders_search_filters_spacer_trailing()
    {
        var cut = _ctx.Render<FilterBar>(p => p
            .Add(c => c.Search, Html("<input class=\"input\" type=\"search\" />"))
            .Add(c => c.ChildContent, Html("<span class=\"select-wrap\"></span>"))
            .Add(c => c.Trailing, Html("<div class=\"pill-tabs\"></div>")));

        cut.Find("div.filter-bar").Children.Select(e => e.ClassName).Should().Equal(
            "search filter-bar__search", "select-wrap", "filter-bar__spacer", "pill-tabs");
        var box = cut.Find(".filter-bar__search");
        box.TagName.Should().Be("SPAN");
        box.Children.Select(e => e.TagName).Should().Equal("svg", "INPUT");
    }

    [Fact]
    public void FilterBar_without_trailing_content_renders_no_spacer()
    {
        var cut = _ctx.Render<FilterBar>(p => p
            .Add(c => c.Search, Html("<input class=\"input\" type=\"search\" />")));

        cut.FindAll(".filter-bar__spacer").Should().BeEmpty();
    }

    /// <summary>
    /// #805: with the sizing class on the form, the box and its Search button stacked.
    /// </summary>
    [Fact]
    public void FilterBar_search_form_keeps_the_sizing_class_on_the_box_not_the_form()
    {
        var cut = _ctx.Render<FilterBar>(p => p
            .Add(c => c.SearchForm, true)
            .Add(c => c.SearchFormAction, "/site-admin/users")
            .Add(c => c.Search, Html("<input class=\"input\" type=\"search\" name=\"q\" />"))
            .Add(c => c.SearchFormExtras, Html("<button type=\"submit\" class=\"btn btn--sm\">Search</button>")));

        var form = cut.Find("div.filter-bar > form");
        form.ClassList.Should().NotContain("filter-bar__search");
        form.GetAttribute("method").Should().Be("get");
        form.GetAttribute("role").Should().Be("search");
        form.GetAttribute("action").Should().Be("/site-admin/users");
        form.Children.Select(e => e.TagName).Should().Equal("SPAN", "BUTTON");
        form.Children[0].ClassName.Should().Be("search filter-bar__search");
    }
}
