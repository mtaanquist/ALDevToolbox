using Microsoft.AspNetCore.DataProtection;
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
/// Smoke test for <c>/admin/cookbook</c>. The page hosts three independent
/// surfaces — the recipe table, the cookbook-guidance editor, and the
/// suggestions queue — so the three-state rule applies to each. Both empty
/// states (recipes, suggestions) must render their own useful copy.
/// </summary>
public sealed class AdminCookbookTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    public AdminCookbookTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<RecipeService>();
        _ctx.Services.AddScoped<RecipeSuggestionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddDataProtection();
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
    public void Empty_org_renders_useful_empty_states_for_both_lists()
    {
        var cut = _ctx.RenderComponent<AdminCookbook>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No recipes yet");
            cut.Markup.Should().Contain("No pending suggestions",
                "the suggestions queue gets its own empty-state copy — admins "
                + "should not see an empty table");
            cut.Find("a[href='/admin/cookbook/new']").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Populated_recipe_list_renders_one_row_per_recipe_with_an_edit_link()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Recipes.Add(RecipeBuilder.Default("Generic table proxy"));
            seed.Recipes.Add(RecipeBuilder.Default("Posting routine"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminCookbook>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("table.data-table tbody tr");
            rows.Should().HaveCount(2);
            cut.FindAll("a.btn[href^='/admin/cookbook/']")
                .Where(a => (a.GetAttribute("href") ?? string.Empty) != "/admin/cookbook/new")
                .Should().HaveCount(2, "every row gets an edit link to its detail page");
        });
    }

    [Fact]
    public async Task Soft_deleted_recipes_are_hidden_until_the_show_deleted_checkbox_is_ticked()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Recipes.Add(RecipeBuilder.Default("Active"));
            var trashed = RecipeBuilder.Default("Trashed");
            trashed.DeletedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            seed.Recipes.Add(trashed);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.RenderComponent<AdminCookbook>();

        cut.WaitForAssertion(() =>
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(1));

        cut.Find("div.admin-page__toolbar input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() =>
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(2));
    }

}
