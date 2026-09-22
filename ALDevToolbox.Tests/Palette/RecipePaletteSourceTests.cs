using System.Reflection;
using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The shared fence harness over <see cref="RecipePaletteSource"/>.
///
/// <para>Recipes hang off no solution, so
/// <see cref="PaletteSourceVisibilityTestBase.RowsBelongToSolutions"/> is false
/// and the Private-solution case reports as skipped rather than passing for a
/// reason that isn't true. What does apply applies in full: another
/// organisation's recipes never appear, and a caller who fails the Cookbook's
/// gate - signed out, or in an organisation that has switched the Cookbook off -
/// gets nothing.</para>
/// </summary>
public sealed class RecipePaletteSourceHarnessTests : PaletteSourceVisibilityTestBase
{
    protected override bool RowsBelongToSolutions => false;

    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new RecipePaletteSource(context.Db, TestDb.EverythingEnabled());

    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        ctx.Recipes.Add(RecipeBuilder
            .Default(seed.Title, organizationId: seed.OrganizationId)
            .WithFile("Recipe.al", "// recipe"));
        return Task.CompletedTask;
    }

    protected override IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        yield return ("an anonymous caller", new ClaimsPrincipal(new ClaimsIdentity()));
        yield return (
            "a member whose organisation has switched the Cookbook off",
            RecipePaletteSourceTests.MemberPrincipal(ToolKey.Cookbook));
    }
}

/// <summary>
/// What the Recipes source finds and what it shows: title and tags only, the
/// Cookbook list's own subtitle, a link to the recipe page that exists, and the
/// two kinds of row the Cookbook list leaves out.
/// </summary>
public sealed class RecipePaletteSourceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_recipe_is_found_by_a_tag_that_is_not_the_first_one()
    {
        // The Cookbook card shows several tags; a palette row has space for
        // one, so a match on a later tag is only useful if the row then shows
        // that tag. Both halves are asserted here.
        await SeedAsync(recipe =>
        {
            recipe.Title = "Attachment Factbox";
            recipe.Keywords = "factbox,document attachments,posting";
        });

        var results = await SearchAsync("posting");

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("recipe");
        row.Title.Should().Be("Attachment Factbox");
        row.Subtitle.Should().Contain("posting", "the row has to show the tag it was found by");
    }

    [Fact]
    public async Task Terms_may_be_spread_across_the_title_and_a_tag()
    {
        await SeedAsync(recipe =>
        {
            recipe.Title = "Attachment Factbox";
            recipe.Keywords = "posting";
        });

        var results = await SearchAsync("factbox posting");

        results.Should().ContainSingle().Which.Title.Should().Be("Attachment Factbox");
    }

    [Fact]
    public async Task The_summary_is_not_searched()
    {
        // The Cookbook's own search reads the description; the palette does not,
        // because the row it draws cannot show it. See
        // .design/command-palette.md, "Sources".
        await SeedAsync(recipe =>
        {
            recipe.Title = "Attachment Factbox";
            recipe.Keywords = "factbox";
            recipe.Description = "Use this to wire up subscriber events.";
        });

        var results = await SearchAsync("subscriber");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task The_subtitle_is_the_minimum_version_and_the_first_tag()
    {
        var versionId = await SeedApplicationVersionAsync("Business Central 2026 Release Wave 1");
        await SeedAsync(recipe =>
        {
            recipe.Title = "Attachment Factbox";
            recipe.Keywords = "factbox,posting";
            recipe.MinimumApplicationVersionId = versionId;
        });

        var results = await SearchAsync("attachment");

        results.Should().ContainSingle().Which.Subtitle.Should()
            .Be("Business Central 2026 Release Wave 1 or later - factbox");
    }

    [Fact]
    public async Task A_recipe_with_no_version_and_no_tags_has_no_subtitle()
    {
        await SeedAsync(recipe =>
        {
            recipe.Title = "Attachment Factbox";
            recipe.Keywords = string.Empty;
        });

        var results = await SearchAsync("attachment");

        results.Should().ContainSingle().Which.Subtitle.Should().BeNull();
    }

    [Fact]
    public async Task A_deprecated_recipe_is_left_out()
    {
        await SeedAsync(recipe =>
        {
            recipe.Title = "Attachment Factbox";
            recipe.Deprecated = true;
        });

        var results = await SearchAsync("attachment");

        results.Should().BeEmpty("the Cookbook list hides deprecated recipes unless they are asked for");
    }

    [Fact]
    public async Task A_soft_deleted_recipe_is_left_out()
    {
        await SeedAsync(recipe =>
        {
            recipe.Title = "Attachment Factbox";
            recipe.DeletedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        });

        var results = await SearchAsync("attachment");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wildcard_typed_into_the_box_matches_itself()
    {
        await SeedAsync(recipe => recipe.Title = "Discount 50% Rule");
        await SeedAsync(recipe => recipe.Title = "Discount Ladder");

        var results = await SearchAsync("50%");

        results.Should().ContainSingle().Which.Title.Should().Be("Discount 50% Rule");
    }

    [Fact]
    public async Task The_link_points_at_the_recipe_page_that_exists()
    {
        await SeedAsync(recipe => recipe.Title = "Attachment Factbox");

        var results = await SearchAsync("attachment");

        var href = results.Should().ContainSingle().Subject.Href;
        href.Should().MatchRegex(@"^/cookbook/\d+$");
        RouteTemplates().Should().Contain("/cookbook/{Id:int}",
            "the palette's link is a dead end unless a page claims that route");
    }

    [Fact]
    public async Task The_gate_follows_the_site_wide_cookbook_toggle()
    {
        await using var ctx = _db.NewContext();
        var source = new RecipePaletteSource(ctx, new CookbookOff());

        var available = await source.IsAvailableAsync(MemberPrincipal(), CancellationToken.None);

        available.Should().BeFalse("a SiteAdmin who switched the Cookbook off hid it from the nav too");
    }

    [Fact]
    public async Task An_ordinary_member_passes_the_gate()
    {
        await using var ctx = _db.NewContext();
        var source = new RecipePaletteSource(ctx, TestDb.EverythingEnabled());

        var available = await source.IsAvailableAsync(MemberPrincipal(), CancellationToken.None);

        available.Should().BeTrue();
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    /// <summary>A signed-in member of the default organisation, with the named tools switched off for their org.</summary>
    internal static ClaimsPrincipal MemberPrincipal(params ToolKey[] disabledTools)
    {
        var claims = new List<Claim>
        {
            new(HttpOrganizationContext.UserIdClaim, "9400"),
            new(HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString()),
            new(ClaimTypes.Role, nameof(UserRole.User)),
        };
        if (disabledTools.Length > 0)
        {
            claims.Add(new Claim(
                EndpointHelpers.DisabledToolsClaim, string.Join(',', disabledTools.Select(t => t.ToString()))));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(string rawQuery)
    {
        await using var ctx = _db.NewContext();
        var source = new RecipePaletteSource(ctx, TestDb.EverythingEnabled());

        var query = PaletteQuery.Parse(rawQuery);
        query.IsUsable.Should().BeTrue();

        var candidates = await source.SearchAsync(query, 25, CancellationToken.None);
        // Ranked the way the service ranks, so the assertions are about what a
        // user would see rather than what the pre-filter let through.
        return PaletteRanking.Rank(query, candidates).Select(m => m.Candidate).ToList();
    }

    private async Task SeedAsync(Action<Recipe> configure)
    {
        await using var ctx = _db.NewContext();
        var recipe = RecipeBuilder.Default().WithFile("Recipe.al", "// recipe");
        configure(recipe);
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedApplicationVersionAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var version = new ApplicationVersion
        {
            OrganizationId = TestDb.DefaultOrgId,
            Key = "bc-2026-w1",
            Name = name,
            Application = "28.0.0.0",
            Runtime = "28.0",
            Ordering = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.ApplicationVersions.Add(version);
        await ctx.SaveChangesAsync();
        return version.Id;
    }

    internal static IReadOnlyList<string> RouteTemplates() =>
        typeof(HttpOrganizationContext).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: true))
            .Select(r => r.Template)
            .ToList();

    private sealed class CookbookOff : IToolAvailability
    {
        public bool IsSiteEnabled(ToolKey key) => key != ToolKey.Cookbook;
    }
}
