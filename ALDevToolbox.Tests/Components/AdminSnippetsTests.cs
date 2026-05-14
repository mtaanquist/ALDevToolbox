using ALDevToolbox.Components.Pages.Admin;
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
/// Smoke test for <c>/admin/snippets</c>. The page hosts two independent
/// lists — the snippet table and the suggestions queue — so the three-state
/// rule applies twice. Both empty states must render their own useful copy.
/// </summary>
public sealed class AdminSnippetsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminSnippetsTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<SnippetService>();
        _ctx.Services.AddScoped<SnippetSuggestionService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
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
    public void Both_lists_render_useful_empty_states_when_the_org_has_nothing_yet()
    {
        var cut = _ctx.RenderComponent<AdminSnippets>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No snippets yet");
            cut.Markup.Should().Contain("No pending suggestions",
                "the suggestions queue gets its own empty-state copy — admins "
                + "should not see an empty table");
            cut.Find("a[href='/admin/snippets/new']").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Populated_snippet_list_renders_one_row_per_snippet_with_an_edit_link()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Snippets.Add(SnippetBuilder.Default("Generic table proxy"));
            seed.Snippets.Add(SnippetBuilder.Default("Posting routine"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminSnippets>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("table.data-table tbody tr");
            rows.Should().HaveCount(2);
            cut.FindAll("a.btn[href^='/admin/snippets/']")
                .Where(a => (a.GetAttribute("href") ?? string.Empty) != "/admin/snippets/new")
                .Should().HaveCount(2, "every row gets an edit link to its detail page");
        });
    }

    [Fact]
    public async Task Soft_deleted_snippets_are_hidden_until_the_show_deleted_checkbox_is_ticked()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Snippets.Add(SnippetBuilder.Default("Active"));
            var trashed = SnippetBuilder.Default("Trashed");
            trashed.DeletedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            seed.Snippets.Add(trashed);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminSnippets>();

        cut.WaitForAssertion(() =>
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(1));

        cut.Find("div.admin-page__toolbar input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() =>
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(2));
    }
}
