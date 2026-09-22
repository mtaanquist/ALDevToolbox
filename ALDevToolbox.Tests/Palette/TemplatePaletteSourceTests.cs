using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The palette's Templates source (#885): workspace templates by name and key,
/// landing on the template's own page. Templates are organisation content, not a
/// Solution's, so the Private-Solution case reports as skipped; another
/// organisation's templates and a caller without the Templates tool are covered.
/// </summary>
public sealed class TemplatePaletteSourceTests : PaletteSourceVisibilityTestBase
{
    protected override bool RowsBelongToSolutions => false;

    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new TemplatePaletteSource(context.Db, Db.NewToolEnablement(context.Db));

    /// <summary>One template per Solution the harness seeds, named for it and keyed by its short name.</summary>
    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        var template = TemplateBuilder.Default(seed.ShortName.ToLowerInvariant(), organizationId: seed.OrganizationId);
        template.Name = seed.Title;
        ctx.RuntimeTemplates.Add(template);
        return Task.CompletedTask;
    }

    protected override IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        foreach (var entry in base.PrincipalsThatGetNothing()) yield return entry;
        yield return ("a member whose organisation has switched Templates off",
            RecipePaletteSourceTests.MemberPrincipal(ToolKey.Templates));
    }

    [Fact]
    public async Task A_template_is_found_by_its_name()
    {
        await SeedWorldAsync();
        await AddTemplateAsync("runtime-15-cloud", "Cloud Runtime 15");

        var results = await SearchAsync("cloud runtime");

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("template");
        row.Title.Should().Be("Cloud Runtime 15");
        row.Subtitle.Should().Be("runtime-15-cloud - Runtime 15");
        row.Href.Should().Be("/templates/runtime-15-cloud");
    }

    [Fact]
    public async Task A_template_is_found_by_its_key_and_the_row_shows_it()
    {
        await SeedWorldAsync();
        await AddTemplateAsync("onprem-legacy", "Older installs");

        var results = await SearchAsync("onprem-legacy");

        results.Should().ContainSingle().Which.Subtitle.Should().StartWith("onprem-legacy",
            "a row found by its key has to show the key, or nothing on it explains the match");
    }

    [Fact]
    public async Task A_deprecated_template_is_offered_and_says_so()
    {
        // The Templates list shows deprecated templates, marked, and their page
        // still opens - so the palette offers them the same way.
        await SeedWorldAsync();
        await AddTemplateAsync("runtime-11", "Runtime Eleven", deprecated: true);

        var results = await SearchAsync("eleven");

        results.Should().ContainSingle().Which.Subtitle.Should().EndWith("Deprecated");
    }

    [Fact]
    public async Task A_soft_deleted_template_is_left_out()
    {
        await SeedWorldAsync();
        await AddTemplateAsync("runtime-9", "Runtime Nine", deletedAt: DateTime.UtcNow);

        var results = await SearchAsync("runtime nine");

        results.Should().BeEmpty("the Templates list does not show a deleted template");
    }

    [Fact]
    public void The_link_points_at_a_page_that_exists() =>
        RecipePaletteSourceTests.RouteTemplates().Should().Contain("/templates/{Key}",
            "the palette's link is a dead end unless a page claims that route");

    [Fact]
    public async Task An_ordinary_member_passes_the_gate()
    {
        // The browser's gate, not the content-author gate the admin template
        // pages carry: a plain User opens /templates.
        await using var ctx = Db.NewContext();
        var source = new TemplatePaletteSource(ctx, Db.NewToolEnablement(ctx));

        (await source.IsAvailableAsync(CallerPrincipal(), CancellationToken.None)).Should().BeTrue();
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private async Task AddTemplateAsync(
        string key, string name, bool deprecated = false, DateTime? deletedAt = null)
    {
        await using var ctx = Db.NewContext();
        var template = TemplateBuilder.Default(key, organizationId: TestDb.DefaultOrgId);
        template.Name = name;
        template.Deprecated = deprecated;
        template.DeletedAt = deletedAt;
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();
    }
}
