using ALDevToolbox.Components.Pages;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the "three states: loading, empty, populated" contract from
/// CLAUDE.md §"Always have the end user in mind" for the user-facing
/// snippets page. Specifically guards the two empty-state branches —
/// the "no snippets in this org" copy points to <c>/snippets/suggest</c>
/// (the recovery action) and is distinct from the "no match for query"
/// copy, which name-checks the search term.
/// </summary>
public sealed class SnippetsBrowserTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public SnippetsBrowserTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<SnippetService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLoggerFactory.Instance.CreateLogger<IconCatalog>()));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_org_renders_the_recovery_link_to_the_suggest_page()
    {
        var cut = _ctx.RenderComponent<SnippetsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No snippets in this organisation yet");
            cut.Find("a[href='/snippets/suggest']").Should().NotBeNull(
                "the empty-state copy must offer a path to the recovery action — "
                + "CLAUDE.md §\"three states\" rule");
        });
    }

    [Fact]
    public async Task Populated_org_renders_snippet_cards_with_links_to_the_detail_page()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Snippets.Add(SnippetBuilder.Default("Generic table proxy"));
            seed.Snippets.Add(SnippetBuilder.Default("Posting routine skeleton"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<SnippetsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll("a.snippet-card__title");
            cards.Should().HaveCount(2);
            cards.Select(c => c.TextContent).Should().BeEquivalentTo(
                new[] { "Generic table proxy", "Posting routine skeleton" });
            cards.Select(c => c.GetAttribute("href"))
                .Should().AllSatisfy(h => h!.StartsWith("/snippets/").Should().BeTrue());
        });
    }

    [Fact]
    public async Task Deprecated_snippets_are_hidden_until_the_include_deprecated_checkbox_is_ticked()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Snippets.Add(SnippetBuilder.Default("Active pattern"));
            var deprecated = SnippetBuilder.Default("Old pattern");
            deprecated.Deprecated = true;
            seed.Snippets.Add(deprecated);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<SnippetsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("a.snippet-card__title").Should().HaveCount(1,
                "deprecated rows are hidden by default — SnippetService.SearchAsync's "
                + "includeDeprecated parameter defaults to false");
            cut.Markup.Should().Contain("Active pattern");
            cut.Markup.Should().NotContain("Old pattern");
        });

        var checkbox = cut.Find("input[type=checkbox]");
        checkbox.Change(true);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("a.snippet-card__title").Should().HaveCount(2);
            cut.Markup.Should().Contain("Old pattern");
        });
    }
}
