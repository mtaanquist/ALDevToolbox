using ALDevToolbox.Components.Pages;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The setup page at <c>/tools/mcp</c>, after the fresh-eyes review of PR 12.
///
/// Every rule here exists because the review found the page breaking it. Step 1
/// offered a genuine either/or where both buttons went to the same URL and only
/// one of the two paths continued into steps 2 and 3; the snippet carried a
/// placeholder the reader was told to swap by hand, having just lost their token
/// to the clipboard; and the tool table rendered flat, wire-name first, on a
/// page whose own caption says you never need to name them.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class McpSetupPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public McpSetupPageTests()
    {
        _db.McpAvailability.Set(true);
        _db.OrgContext.CurrentUserId = 1;

        _ctx.Services.AddSingleton<IMcpAvailability>(_db.McpAvailability);
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts => opts
            .UseNpgsql(_db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _ctx.Services.AddScoped<PersonalAccessTokenService>();
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = Request("https", "toolbox.cronus.example") });
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    private static DefaultHttpContext Request(string scheme, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        return ctx;
    }

    private async Task GiveTokenAsync()
    {
        await using var db = _db.NewContext();
        db.Users.Add(new User
        {
            Id = 1,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "kirsten.jensen@cronus.example",
            PasswordHash = "x",
            DisplayName = "Kirsten Jensen",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        db.PersonalAccessTokens.Add(new PersonalAccessToken
        {
            UserId = 1,
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS laptop",
            TokenHash = "not-a-real-hash",
            TokenPrefix = "aldt_pat_test",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The connector path has to end in step 1. It used to hand the reader on to
    /// "pick your client" - four desktop clients, none of them theirs - and then
    /// tell them to paste a token step 1 never gave them.
    /// </summary>
    [Fact]
    public void The_permission_screen_path_carries_the_address_and_says_it_is_finished()
    {
        var page = _ctx.RenderComponent<ALDevToolbox.Components.Pages.Mcp>();

        var connector = page.FindAll(".mcp-choice .card")[1];
        connector.TextContent.Should().Contain("https://toolbox.cronus.example/mcp",
            "this path never reaches a snippet, so step 1 is the only place the address can appear");
        connector.TextContent.Should().Contain("steps 2 and 3 are not yours");
        connector.QuerySelector("[data-copy-target]").Should().NotBeNull();
    }

    /// <summary>One primary button, on the path most arrivals need.</summary>
    [Fact]
    public void There_is_exactly_one_primary_action_and_it_is_the_token()
    {
        var page = _ctx.RenderComponent<ALDevToolbox.Components.Pages.Mcp>();

        page.WaitForAssertion(() =>
        {
            var primaries = page.FindAll(".btn--primary");
            primaries.Should().HaveCount(1);
            primaries[0].TextContent.Trim().Should().Be("Create a token");
        });
    }

    /// <summary>
    /// Pasting the token fills the snippet. Without it the reader copies the
    /// snippet, which overwrites the token sitting in their clipboard - and the
    /// token is shown only once, so it is now gone from the clipboard and the
    /// screen both.
    /// </summary>
    [Fact]
    public void Pasting_a_token_fills_it_into_the_snippet()
    {
        var page = _ctx.RenderComponent<ALDevToolbox.Components.Pages.Mcp>();

        page.Find(".step:nth-of-type(2) .codeblock__pre").TextContent
            .Should().Contain("Bearer PASTE-YOUR-TOKEN-HERE");

        page.Find("#mcp-token").Input("aldt_pat_7f3c_9K2mQx4LbT8vRn1sWd6Ey0Ap");

        var snippet = page.Find(".step:nth-of-type(2) .codeblock__pre").TextContent;
        snippet.Should().Contain("Bearer aldt_pat_7f3c_9K2mQx4LbT8vRn1sWd6Ey0Ap");
        snippet.Should().NotContain("PASTE-YOUR-TOKEN-HERE");
    }

    [Fact]
    public void Every_client_tab_produces_a_snippet_carrying_the_pasted_token()
    {
        var page = _ctx.RenderComponent<ALDevToolbox.Components.Pages.Mcp>();
        page.Find("#mcp-token").Input("aldt_pat_abc");

        var tabCount = page.FindAll(".pill-tab").Count;
        tabCount.Should().BeGreaterThan(1);

        for (var i = 0; i < tabCount; i++)
        {
            // Re-found each pass: clicking re-renders, and bUnit invalidates the
            // event-handler ids on the previously captured elements.
            page.FindAll(".pill-tab")[i].Click();
            page.Find(".step:nth-of-type(2) .codeblock__pre").TextContent
                .Should().Contain("aldt_pat_abc")
                .And.NotContain("${", "that is live variable syntax in a real mcp.json");
        }
    }

    /// <summary>
    /// Step 1 shows as done once the reader has actually done it, either way.
    /// The archetype ships `.step.is-done`; without using it a returning user
    /// re-reads the fork on every visit.
    /// </summary>
    /// <summary>
    /// Weakly ordered on purpose, and worth saying so: the page reads its state
    /// in OnInitializedAsync, so "not done" is also what the first render shows
    /// before that read lands. This pins the copy of the un-started state rather
    /// than the mechanism - the test below is the one that proves the mechanism.
    /// </summary>
    [Fact]
    public void Step_one_is_not_done_before_anything_is_set_up()
    {
        var page = _ctx.RenderComponent<ALDevToolbox.Components.Pages.Mcp>();

        page.Find(".step").ClassName.Should().NotContain("is-done");
        page.FindAll(".step .badge").Should().BeEmpty();
    }

    [Fact]
    public async Task Step_one_is_done_once_the_reader_has_a_token()
    {
        await GiveTokenAsync();

        var page = _ctx.RenderComponent<ALDevToolbox.Components.Pages.Mcp>();

        // WaitForAssertion, not a bare assert: the token and consent reads run in
        // OnInitializedAsync against a real database, so the first render lands
        // before they answer. Asserting straight away passed alone and failed in
        // the full suite, which is the worst way to find this out.
        page.WaitForAssertion(() =>
        {
            page.Find(".step").ClassName.Should().Contain("is-done");
            page.Find(".step .badge").TextContent.Trim().Should().Be("You have a token");
            page.Find(".btn--primary").TextContent.Trim().Should().Be("Manage your tokens");
        });
    }

    /// <summary>
    /// The reference is grouped and folded. Flat, wire-name-first and open by
    /// default, thirty-eight rows sat between the setup steps and the
    /// troubleshooting a stuck reader is scrolling for.
    /// </summary>
    [Fact]
    public void The_tool_reference_is_grouped_and_folded_away()
    {
        var page = _ctx.RenderComponent<ALDevToolbox.Components.Pages.Mcp>();

        var details = page.Find("details.mcp-tools");
        details.HasAttribute("open").Should().BeFalse("it is lookup material, not the task");

        var groups = page.FindAll(".mcp-tools__group").Select(e => e.TextContent.Trim());
        groups.Should().BeEquivalentTo(McpToolCatalog.Groups);

        var listed = page.FindAll(".mcp-tools table code").Select(e => e.TextContent.Trim());
        listed.Should().BeEquivalentTo(McpToolCatalog.All.Select(t => t.Name));
    }
}
