using System.Text.RegularExpressions;
using ALDevToolbox.Components.Shared.Archetypes;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <see cref="GeneratorPage"/> is the generator frame from PageGenerator.dc.html. What
/// is pinned here is what the two generator pages used to each get right by hand: the
/// one primary button with the loading contract generate.js looks for, and a form that
/// reaches round the aside so that button posts it.
/// </summary>
public sealed class GeneratorPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public GeneratorPageTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static RenderFragment Html(string markup) => b => b.AddMarkupContent(0, markup);

    private static string Name(AngleSharp.Dom.IElement e) => string.IsNullOrEmpty(e.Id) ? e.ClassName ?? "" : e.Id;

    private IRenderedComponent<GeneratorPage> Render(bool loading = false, bool empty = false, Action? onSubmit = null) =>
        _ctx.Render<GeneratorPage>(p => p
            .Add(c => c.Title, "New workspace")
            .Add(c => c.IsLoading, loading)
            .Add(c => c.IsEmpty, empty)
            .Add(c => c.Empty, Html("<p id=\"nothing\"></p>"))
            .Add(c => c.FormId, "gen-form")
            .Add(c => c.FormAction, "/generate/workspace")
            .Add(c => c.OnSubmit, () => onSubmit?.Invoke())
            .Add(c => c.Notices, Html("<p id=\"notice\"></p>"))
            .Add(c => c.Form, Html("<section id=\"sec\" class=\"form-sec\"></section>"))
            .Add(c => c.Preview, Html("<div id=\"preview\"></div>"))
            .Add(c => c.Stats, Html("<div class=\"stat-card\"></div><div class=\"stat-card\"></div>"))
            .Add(c => c.PrimaryLabel, Html("Download ZIP"))
            .Add(c => c.PrimaryNote, "2 extensions")
            .Add(c => c.AsideCards, Html("<div id=\"after\"><button class=\"btn\">Create repository</button></div>")));

    [Fact]
    public void The_aside_runs_preview_counts_button_note_then_cards()
    {
        var cut = Render();

        cut.Find("aside.gen__aside").Children.Select(Name).Should().Equal(
            "preview", "gen__stats", "btn btn--primary btn--lg gen__go",
            "form-actions__note gen__note", "after");
        cut.Find(".gen__form").Children.Select(Name).Should().Equal("sec");
        cut.FindAll(".gen__stats > .stat-card").Should().HaveCount(2);
    }

    [Fact]
    public void The_one_primary_is_the_submit_and_carries_the_loading_contract()
    {
        var cut = Render();

        var primary = cut.FindAll(".btn--primary").Should().ContainSingle().Subject;
        primary.GetAttribute("type").Should().Be("submit");
        primary.HasAttribute("data-loading-button").Should().BeTrue(
            because: "generate.js finds the button by this attribute to start the spinner");
        primary.QuerySelector(".btn__spinner").Should().NotBeNull();
        primary.QuerySelector(".btn__label")!.TextContent.Should().Be("Download ZIP");
        primary.QuerySelector(".btn__label-busy")!.TextContent.Should().Be("Generating...");
    }

    [Fact]
    public void The_form_wraps_both_columns_and_posts_what_generate_js_needs()
    {
        var cut = Render();
        var form = cut.Find("form#gen-form");

        form.GetAttribute("method").Should().Be("post");
        form.GetAttribute("action").Should().Be("/generate/workspace");
        form.QuerySelector("input[type=hidden][name=GenToken]").Should().NotBeNull();
        form.QuerySelector("aside.gen__aside .gen__go").Should().NotBeNull(
            because: "the submit button sits in the aside, so the form has to reach round it");
        form.HasAttribute("data-loading-form").Should().BeFalse(
            because: "the page cancels every native submit to validate first (#546); that "
                   + "listener would start the spinner on a submit that never leaves");
        form.Children.Select(Name).Should().ContainInOrder("notice", "gen");
    }

    [Fact]
    public void Submitting_runs_the_pages_handler()
    {
        var submitted = 0;
        var cut = Render(onSubmit: () => submitted++);

        cut.Find("form").Submit();

        submitted.Should().Be(1);
    }

    [Theory]
    [InlineData(true, false, "loading-block")]
    [InlineData(false, true, "nothing")]
    [InlineData(true, true, "loading-block")]
    public void Loading_and_empty_keep_the_head_and_drop_the_form(bool loading, bool empty, string body)
    {
        var cut = Render(loading, empty);

        cut.Find("div.page").Children.Select(Name).Should().Equal("page-head", body);
        cut.FindAll("form").Should().BeEmpty();
    }

    // ── The pages ──────────────────────────────────────────────────────

    public static TheoryData<string> Generators => new()
    {
        "ALDevToolbox/Components/Pages/NewWorkspace.razor",
        "ALDevToolbox/Components/Pages/NewExtension.razor",
    };

    /// <summary>
    /// CLAUDE.md: Generate is the only primary on the page. The frame renders it, so a
    /// page that writes another has two. The empty state is the exception - the form,
    /// and its button, are not on the page then.
    /// </summary>
    [Theory]
    [MemberData(nameof(Generators))]
    public void A_generator_page_writes_no_primary_button_of_its_own(string page)
    {
        var markup = File.ReadAllText(Path.Combine(Root(), page));
        markup = markup[..markup.IndexOf("@code {", StringComparison.Ordinal)];
        markup = Regex.Replace(markup, @"@\*.*?\*@", "", RegexOptions.Singleline);
        markup = Regex.Replace(markup, @"<Empty>.*?</Empty>", "", RegexOptions.Singleline);

        markup.Should().Contain("<GeneratorPage");
        markup.Should().NotContain("btn--primary");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
