using ALDevToolbox.Components.Layout;
using ALDevToolbox.Domain.Navigation;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Tools;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The sidebar renders in the order the person arranged it (issue #956), read
/// from the <c>aldt-nav-order</c> cookie at render so the first paint is already
/// theirs, and carries the controls the arrange mode needs.
///
/// <para>Rendered for an anonymous visitor on purpose: that is the one sidebar
/// that needs no database (a signed-in one asks <c>ProjectAccess</c> about
/// Upgrades, which is <see cref="NavMenuTests"/>' job), and it still has three
/// groups and their items to arrange.</para>
/// </summary>
public sealed class NavMenuArrangeTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly DefaultHttpContext _http = new();
    private readonly FakeToolAvailability _tools = new();

    public NavMenuArrangeTests()
    {
        _ctx.AddAuthorization().SetNotAuthorized();
        _ctx.Services.AddSingleton<IOrganizationContext>(new AmbientOrganizationContext());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<IToolAvailability>(_tools);
        _ctx.Services.AddSingleton<ISingleTenantMode>(new FakeSingleTenantMode());
        _ctx.Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = _http });
    }

    public void Dispose() => _ctx.Dispose();

    private sealed class FakeToolAvailability : IToolAvailability
    {
        public HashSet<ToolKey> Disabled { get; } = new();
        public bool IsSiteEnabled(ToolKey key) => !Disabled.Contains(key);
    }

    private sealed class FakeSingleTenantMode : ISingleTenantMode
    {
        public bool IsEnabled => false;
    }

    /// <summary>Sets the cookie the way the browser sends it: URL-encoded, as nav-arrange.js writes it.</summary>
    private void SavedOrder(string value) =>
        _http.Request.Headers.Cookie = $"{SidebarOrder.CookieName}={Uri.EscapeDataString(value)}";

    private static List<string?> Groups(IRenderedComponent<NavMenu> cut) =>
        cut.FindAll(".app__nav-scroll > .nav-group[data-group]").Select(g => g.GetAttribute("data-group")).ToList();

    private static List<string?> Items(IRenderedComponent<NavMenu> cut, string group) =>
        cut.FindAll($".nav-group[data-group='{group}'] > .nav-group__items > .nav-row")
            .Select(r => r.GetAttribute("data-item")).ToList();

    [Fact]
    public void Without_a_saved_order_the_sidebar_renders_in_shipped_order()
    {
        var cut = _ctx.Render<NavMenu>();

        Groups(cut).Should().Equal("build", "text", "deliver");
        Items(cut, "build").Should().Equal("templates", "cookbook", "object-explorer");
        Items(cut, "deliver").Should().Equal(["solutions", "pipelines"],
            "Teams needs a signed-in user, so an anonymous visitor does not see it");
    }

    [Fact]
    public void The_sidebar_renders_in_the_saved_order()
    {
        SavedOrder("deliver,text,build|deliver:pipelines,solutions|build:object-explorer,templates,cookbook");

        var cut = _ctx.Render<NavMenu>();

        Groups(cut).Should().Equal("deliver", "text", "build");
        Items(cut, "deliver").Should().Equal("pipelines", "solutions");
        Items(cut, "build").Should().Equal("object-explorer", "templates", "cookbook");
        Items(cut, "text").Should().Equal(["translator", "diff", "piper"], "no order was saved for it");

        cut.Find(".app__nav-scroll > .nav-group--plain").NextElementSibling!.GetAttribute("data-group")
            .Should().Be("deliver", "Home stays first; it is not one of the arrangeable rows");
        cut.Find(".nav-row[data-item='pipelines'] a.nav-item").GetAttribute("href").Should().Be("/pipelines",
            "each row renders its own entry, not whatever shipped in that slot");
    }

    [Fact]
    public void A_saved_order_never_shows_what_the_person_may_not_see()
    {
        // The cookie names the admin group, MCP and a tool the site has switched off.
        _tools.Disabled.Add(ToolKey.Cookbook);
        SavedOrder("admin,assistant,site-admin,build|admin:dashboard|assistant:mcp|build:cookbook,templates");

        var cut = _ctx.Render<NavMenu>();

        Groups(cut).Should().Equal("build", "text", "deliver");
        Items(cut, "build").Should().Equal("templates", "object-explorer");
        cut.FindAll("a[href='/cookbook'], a[href='/admin'], a[href='/tools/mcp']").Should().BeEmpty();
    }

    [Fact]
    public void A_group_the_saved_order_does_not_name_is_appended()
    {
        SavedOrder("deliver|deliver:pipelines");

        var cut = _ctx.Render<NavMenu>();

        Groups(cut).Should().Equal("deliver", "build", "text");
    }

    [Fact]
    public void A_garbled_cookie_falls_back_to_shipped_order()
    {
        _http.Request.Headers.Cookie = $"{SidebarOrder.CookieName}=%%%|<b>:x";

        var cut = _ctx.Render<NavMenu>();

        Groups(cut).Should().Equal("build", "text", "deliver");
    }

    [Fact]
    public void Collapsed_and_current_page_state_follow_a_group_to_its_new_place()
    {
        SavedOrder("deliver,text,build");
        _http.Request.Headers.Cookie += "; aldt-nav-collapsed=deliver";
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/pipelines/builds");

        var cut = _ctx.Render<NavMenu>();

        var deliver = cut.Find(".app__nav-scroll > .nav-group[data-group='deliver']");
        deliver.ClassList.Should().Contain(["is-collapsed", "has-active-page"]);
        deliver.PreviousElementSibling!.ClassList.Should().Contain("nav-group--plain");
    }

    [Fact]
    public void The_sidebar_carries_the_arrange_controls()
    {
        var cut = _ctx.Render<NavMenu>();

        cut.Find("[data-nav-arrange]").TextContent.Trim().Should().Be("Arrange sidebar");
        cut.Find("[data-nav-arrange-reset]").TextContent.Trim().Should().Be("Reset to default");
        cut.Find("[data-nav-arrange-done]").TextContent.Trim().Should().Be("Done");
        cut.FindAll(".btn--primary").Should().BeEmpty("the sidebar never carries a page's primary action");

        var controls = cut.Find(".nav-arrange-source[hidden] .nav-arrange");
        controls.QuerySelector(".nav-arrange__grip svg.lucide-grip-vertical").Should().NotBeNull();
        cut.FindAll(".nav-arrange").Should().ContainSingle(
            "the controls are rendered once and copied onto the rows only while arranging");
    }

    [Fact]
    public void Each_move_button_is_named_after_the_row_it_moves()
    {
        var cut = _ctx.Render<NavMenu>();

        // The script puts the row's label in for {0}, so the group head's arrows
        // ("Move Deliver up") and an item's ("Move Solutions up") are told apart.
        cut.FindAll(".nav-arrange-source button[data-nav-move]")
            .Select(b => (b.GetAttribute("data-nav-move"), b.GetAttribute("data-name")))
            .Should().Equal(("-1", "Move {0} up"), ("1", "Move {0} down"));
        cut.FindAll(".app__nav-scroll > .nav-group[data-group] .nav-group__label")
            .Select(l => l.TextContent.Trim()).Should().Equal("Build", "Work with text", "Deliver");
        cut.Find(".nav-row[data-item='solutions'] > .nav-parent > a.nav-item .nav-item__label")
            .TextContent.Trim().Should().Be("Solutions", "the first label in a row is its own, not a sub-page's");
    }

    [Fact]
    public void The_arrange_bar_says_moves_are_saved_as_they_happen()
    {
        var cut = _ctx.Render<NavMenu>();

        var bar = cut.Find(".nav-arrange-bar");
        bar.QuerySelector("[data-nav-arrange-saved]")!.TextContent.Trim()
            .Should().Be("Saved as you go, in this browser.");
        var wasReset = bar.QuerySelector("[data-nav-arrange-was-reset]")!;
        wasReset.HasAttribute("hidden").Should().BeTrue("the undo line only shows after a reset");
        wasReset.TextContent.Should().Contain("Order reset.");
        wasReset.QuerySelector("button[data-nav-arrange-undo]")!.TextContent.Trim().Should().Be("Undo");
        bar.QuerySelector(".nav-arrange-status")!.GetAttribute("aria-live").Should().Be("polite");
    }

    [Fact]
    public void Sub_pages_ride_inside_their_parents_row_so_they_move_with_it()
    {
        var cut = _ctx.Render<NavMenu>();

        var solutions = cut.Find(".nav-row[data-item='solutions']");
        solutions.QuerySelector(".nav-sub a[href='/environments']").Should().NotBeNull(
            "Environments is in the row that moves, so arranging shows it travelling with Solutions");
        cut.Find(".nav-row[data-item='pipelines'] .nav-sub a[href='/pipelines/deployments']").Should().NotBeNull();
        cut.FindAll(".nav-sub [data-item], .nav-sub .nav-row").Should().BeEmpty(
            "a sub-page is not a row of its own, so it gets no controls");
    }

    [Fact]
    public void The_shipped_order_rides_on_the_list_for_reset()
    {
        var cut = _ctx.Render<NavMenu>();

        var shipped = SidebarOrder.Parse(cut.Find(".app__nav-scroll").GetAttribute("data-default-order"));
        shipped.Groups.Should().Equal("build", "text", "deliver", "assistant", "admin", "site-admin");
        shipped.ItemsOf("deliver").Should().Equal("solutions", "upgrades", "teams", "pipelines");
    }
}
