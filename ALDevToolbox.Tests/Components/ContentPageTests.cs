using ALDevToolbox.Components.Pages;
using ALDevToolbox.Components.Pages.Docs;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The three content-archetype pages PR 12 rewrote (<c>/docs/mcp</c>,
/// <c>/docs/extensions-whats-next</c>, <c>/not-found</c>).
///
/// They are worth pinning for the same reason the auth pages were: nothing else
/// in the suite reaches them. Two are anonymous documentation and the third only
/// renders when something has already gone wrong, so a null-reference in a
/// branch here would ship and only show up as a blank page for someone who was
/// already lost.
/// </summary>
public sealed class ContentPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly HttpContextAccessor _http = new() { HttpContext = new DefaultHttpContext() };

    public ContentPageTests()
    {
        _ctx.Services.AddSingleton<IHttpContextAccessor>(_http);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    public void Dispose() => _ctx.Dispose();

    private void Navigate(string url) =>
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo(url);

    // ---------- /docs/mcp ----------

    /// <summary>
    /// Every assistant's setup block, rendered. The guide records differ in
    /// shape - three take a consent flow and carry no snippet at all, one has a
    /// snippet but no filename, one has neither - and the page reads
    /// <c>SettingsPath</c>, <c>Snippet</c> and <c>SnippetFile</c> off whichever
    /// is selected. A record added later with the wrong combination of nulls
    /// throws on render and nothing else would catch it.
    /// </summary>
    [Theory]
    [InlineData("claude-web")]
    [InlineData("claude-mobile")]
    [InlineData("chatgpt")]
    [InlineData("claude-desktop")]
    [InlineData("claude-code")]
    [InlineData("cursor")]
    [InlineData("vscode")]
    [InlineData("copilot")]
    [InlineData("openwebui")]
    public void Every_assistant_renders_its_own_setup_steps(string client)
    {
        Navigate($"/docs/mcp?client={client}");
        var page = _ctx.Render<McpDocs>();

        page.FindAll("ol li").Should().NotBeEmpty("every assistant has numbered steps");
        page.Find($"option[value=\"{client}\"]").HasAttribute("selected")
            .Should().BeTrue("the picker has to come back showing what was asked for");
    }

    /// <summary>
    /// Every assistant's steps have to contain the server address, in its own
    /// copyable control.
    ///
    /// Microsoft 365 Copilot shipped without one: its guide record carries no
    /// snippet, so the code block never rendered, and the steps told the reader
    /// twice to "set the server URL" while the page showed it exactly once - in
    /// the troubleshooting section, several screens further down. The rendered
    /// screenshot cropped one line above the defect. Any future client added
    /// with Snippet: null would do the same, which is why this is a theory over
    /// all of them rather than one regression test.
    /// </summary>
    [Theory]
    [InlineData("claude-web")]
    [InlineData("claude-mobile")]
    [InlineData("chatgpt")]
    [InlineData("claude-desktop")]
    [InlineData("claude-code")]
    [InlineData("cursor")]
    [InlineData("vscode")]
    [InlineData("copilot")]
    [InlineData("openwebui")]
    public void Every_assistant_is_shown_the_server_address_in_its_own_steps(string client)
    {
        _http.HttpContext!.Request.Scheme = "https";
        _http.HttpContext!.Request.Host = new HostString("toolbox.cronus.example");

        Navigate($"/docs/mcp?client={client}");
        var page = _ctx.Render<McpDocs>();

        // Scoped to the steps: the troubleshooting section at the foot of the
        // page also names the address, and finding it there is exactly the bug.
        var steps = page.Find("ol").TextContent;
        steps.Should().Contain("https://toolbox.cronus.example/mcp");

        page.FindAll("[data-copy-target]").Should().NotBeEmpty(
            "the address is useless if it cannot be copied");
    }

    /// <summary>
    /// The placeholder has to be replaced, and the page has to say so. It is
    /// deliberately not written as ${TOKEN}: inside a real .vscode/mcp.json that
    /// is live VS Code variable syntax, so a reader can reasonably paste it
    /// untouched and expect the editor to fill it in.
    /// </summary>
    [Theory]
    [InlineData("claude-desktop")]
    [InlineData("claude-code")]
    [InlineData("cursor")]
    [InlineData("vscode")]
    [InlineData("openwebui")]
    public void A_snippet_carrying_a_placeholder_token_says_to_replace_it(string client)
    {
        Navigate($"/docs/mcp?client={client}");
        var page = _ctx.Render<McpDocs>();

        var snippet = page.Find("#mcp-snippet").TextContent;
        snippet.Should().Contain("PASTE-YOUR-TOKEN-HERE");
        snippet.Should().NotContain("${", "that is live variable syntax in a real mcp.json");

        // Text, not markup: Blazor stamps a scoped-CSS attribute onto the <code>.
        var captions = page.FindAll(".prose__cap").Select(e => e.TextContent).ToList();
        captions.Should().Contain(c => c.Contains("Swap") && c.Contains("PASTE-YOUR-TOKEN-HERE"),
            "a placeholder nobody is told to replace is a 401 waiting to happen");
    }

    /// <summary>
    /// A hand-typed or stale ?client= must not throw. The page looks the guide
    /// up with First(), so an unmatched key would be an unhandled exception on
    /// an anonymous page - reachable by anyone editing the address bar.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("notepad")]
    [InlineData("../etc/passwd")]
    public void An_unknown_assistant_falls_back_to_the_first_one(string client)
    {
        Navigate($"/docs/mcp?client={Uri.EscapeDataString(client)}");
        var page = _ctx.Render<McpDocs>();

        page.Find("option[selected]").GetAttribute("value").Should().Be("claude-web");
    }

    [Fact]
    public void The_docs_list_every_registered_tool()
    {
        Navigate("/docs/mcp");
        var page = _ctx.Render<McpDocs>();

        var listed = page.FindAll("table code").Select(e => e.TextContent.Trim()).ToHashSet();
        listed.Should().BeEquivalentTo(McpToolCatalog.All.Select(t => t.Name));
    }

    /// <summary>
    /// The server URL is the deployment's own, not a YOUR-SERVER placeholder.
    /// It comes off the request, and the fallback path (no HttpContext) has to
    /// produce something valid too rather than an empty string in a config
    /// snippet someone is about to paste.
    /// </summary>
    [Fact]
    public void The_snippet_carries_this_deployment_url()
    {
        _http.HttpContext!.Request.Scheme = "https";
        _http.HttpContext!.Request.Host = new HostString("toolbox.cronus.example");

        Navigate("/docs/mcp?client=vscode");
        var page = _ctx.Render<McpDocs>();

        page.Find(".code-block pre").TextContent
            .Should().Contain("https://toolbox.cronus.example/mcp")
            .And.NotContain("YOUR-SERVER");
    }

    [Fact]
    public void The_docs_render_without_an_http_context()
    {
        _http.HttpContext = null;

        Navigate("/docs/mcp?client=cursor");
        var page = _ctx.Render<McpDocs>();

        page.Find(".code-block pre").TextContent.Should().Contain("/mcp");
    }

    // ---------- /docs/extensions-whats-next ----------

    /// <summary>
    /// Every heading the on-page contents links to has to exist. A TOC entry
    /// pointing at a missing id is a link that silently does nothing, and both
    /// lists are hand-written.
    /// </summary>
    [Theory]
    [InlineData(typeof(WhatsNextDocs))]
    [InlineData(typeof(McpDocs))]
    public void Every_contents_link_points_at_a_heading_on_the_page(Type page)
    {
        // McpDocs reads ?client= off the URL and renders a different block per
        // assistant; WhatsNextDocs ignores the query string entirely. Landing
        // both on /docs/mcp gives the first a defined selection and costs the
        // second nothing.
        Navigate("/docs/mcp");
        var rendered = _ctx.Render(builder =>
        {
            builder.OpenComponent(0, page);
            builder.CloseComponent();
        });

        var ids = rendered.FindAll("[id]").Select(e => e.Id).ToHashSet();
        var targets = rendered.FindAll(".toc-link")
            .Select(a => a.GetAttribute("href")!.TrimStart('#'))
            .ToList();

        targets.Should().NotBeEmpty();
        targets.Should().OnlyContain(t => ids.Contains(t));
    }

    // ---------- /not-found ----------

    /// <summary>
    /// The address that failed. It only survives on
    /// <c>IStatusCodeReExecuteFeature</c> - by the time this page renders,
    /// Request.Path is <c>/not-found</c> - so reading the wrong one shows every
    /// visitor their own error page's address instead of theirs.
    /// </summary>
    [Fact]
    public void The_404_shows_the_address_that_failed()
    {
        _http.HttpContext!.Request.Path = "/not-found";
        _http.HttpContext!.Features.Set<IStatusCodeReExecuteFeature>(new StatusCodeReExecuteFeature
        {
            OriginalPath = "/workspaces/CRONUS-Customer",
            OriginalQueryString = "?tab=objects",
        });

        var page = _ctx.Render<NotFound>();

        page.Find(".errpage__path").TextContent.Trim()
            .Should().Be("/workspaces/CRONUS-Customer?tab=objects");
    }

    /// <summary>
    /// The Router renders this page for an unmatched client-side route, where
    /// there is no re-execute feature. An empty grey box saying nothing is
    /// worse than no box.
    /// </summary>
    [Fact]
    public void The_404_shows_no_address_when_there_is_none_to_show()
    {
        var page = _ctx.Render<NotFound>();

        page.FindAll(".errpage__path").Should().BeEmpty();
        page.Find(".errpage__title").TextContent.Should().NotBeEmpty();
        page.Find("a.btn--primary").GetAttribute("href").Should().Be("/");
    }

    [Fact]
    public void The_500_shows_a_reference_a_person_can_quote()
    {
        HttpContext http = _http.HttpContext!;
        http.TraceIdentifier = "0HN7GQ1V2:00000003";
        var page = _ctx.Render<Error>(p => p.AddCascadingValue(http));

        var refs = page.FindAll(".errpage__ref").Select(e => e.TextContent.Trim()).ToList();
        refs.Should().HaveCount(2, "the id on its own is not enough to find the request in a log");
        refs.Should().Contain(r => r.Contains("UTC"), "a stamp with no zone is the thing that wastes an afternoon");
    }
}
