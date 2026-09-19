using ALDevToolbox.Components.Shared.Archetypes;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <see cref="EditPage"/> is the admin-edit frame from PageAdminEdit.dc.html. What is
/// pinned here is what separates it from the detail frame: the head is always drawn,
/// and the record's history only exists while there is a record to have one.
/// </summary>
public sealed class EditPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public EditPageTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static RenderFragment Html(string markup) => b => b.AddMarkupContent(0, markup);

    private static string Name(AngleSharp.Dom.IElement e) => string.IsNullOrEmpty(e.Id) ? e.ClassName ?? "" : e.Id;

    private IRenderedComponent<EditPage> Render(bool loading = false, bool notFound = false) =>
        _ctx.Render<EditPage>(p => p
            .Add(c => c.Title, "Edit module")
            .Add(c => c.Actions, Html("<button class=\"btn btn--primary\">Save changes</button>"))
            .Add(c => c.Notices, Html("<p id=\"notice\"></p>"))
            .Add(c => c.IsLoading, loading)
            .Add(c => c.IsNotFound, notFound)
            .Add(c => c.NotFound, Html("<p id=\"missing\"></p>"))
            .Add(c => c.ChildContent, Html("<form id=\"form\" class=\"edit-col\"></form>"))
            .Add(c => c.History, Html("<section id=\"history\"></section>")));

    [Fact]
    public void Editing_renders_head_notices_form_then_history()
    {
        Render().Find("div.page").Children.Select(Name).Should()
            .Equal("page-head", "notice", "form", "history");
    }

    [Theory]
    [InlineData(true, false, "loading-block")]
    [InlineData(false, true, "missing")]
    // A page whose not-found flag is also set while it loads must still show loading.
    [InlineData(true, true, "loading-block")]
    public void The_head_and_notices_stay_while_the_body_is_replaced_and_history_goes(
        bool loading, bool notFound, string body)
    {
        var cut = Render(loading, notFound);

        cut.Find("div.page").Children.Select(Name).Should().Equal("page-head", "notice", body);
        cut.Find("h1.page-head__title").TextContent.Should().Be("Edit module");
        cut.Find(".page-head__actions button").TextContent.Should().Be("Save changes");
    }

    [Fact]
    public void Sticky_keeps_save_in_view_on_a_long_form()
    {
        var cut = _ctx.Render<EditPage>(p => p.Add(c => c.Title, "Edit template").Add(c => c.Sticky, true));

        cut.Find("header.page-head").ClassList.Should().Contain("page-head--sticky");
    }
}
