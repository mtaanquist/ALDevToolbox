using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Snippets;

/// <summary>
/// Search behaviour for <see cref="SnippetService.SearchAsync"/>: case-insensitive
/// substring match across title, description, and keywords; empty queries
/// return everything; deprecated and soft-deleted rows respect their flags.
/// </summary>
public sealed class SnippetServiceSearchTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Empty_query_returns_every_active_snippet_excluding_soft_deleted()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Snippets.Add(SnippetBuilder.Default("Alpha").WithFile("a.al", "// a"));
            ctx.Snippets.Add(SnippetBuilder.Default("Beta").WithFile("b.al", "// b"));
            var deleted = SnippetBuilder.Default("Gone").WithFile("g.al", "// g");
            deleted.DeletedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc);
            ctx.Snippets.Add(deleted);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var svc = NewService(ctx2);
        var rows = await svc.SearchAsync(query: null);
        rows.Select(r => r.Title).Should().BeEquivalentTo(new[] { "Alpha", "Beta" });
    }

    [Fact]
    public async Task Query_matches_title_case_insensitively()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Snippets.Add(SnippetBuilder.Default("Doc Attachment Factbox").WithFile("a.al", "// a"));
            ctx.Snippets.Add(SnippetBuilder.Default("Item Lookup Helper").WithFile("b.al", "// b"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync("FACTBOX");
        rows.Should().ContainSingle(r => r.Title == "Doc Attachment Factbox");
    }

    [Fact]
    public async Task Query_matches_description_substring()
    {
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Hidden Title").WithFile("a.al", "// a");
            s.Description = "Use this to wire up subscriber events.";
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync("subscriber");
        rows.Should().ContainSingle(r => r.Title == "Hidden Title");
    }

    [Fact]
    public async Task Query_matches_keywords_substring()
    {
        await using (var ctx = _db.NewContext())
        {
            var s = SnippetBuilder.Default("Tagged").WithFile("a.al", "// a");
            s.Keywords = "permissions tableextension";
            ctx.Snippets.Add(s);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var rows = await NewService(ctx2).SearchAsync("permission");
        rows.Should().ContainSingle(r => r.Title == "Tagged");
    }

    [Fact]
    public async Task Deprecated_rows_are_hidden_by_default_and_shown_when_requested()
    {
        await using (var ctx = _db.NewContext())
        {
            var depr = SnippetBuilder.Default("Old Way").WithFile("a.al", "// a");
            depr.Deprecated = true;
            ctx.Snippets.Add(depr);
            ctx.Snippets.Add(SnippetBuilder.Default("New Way").WithFile("b.al", "// b"));
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = _db.NewContext();
        var withoutDeprecated = await NewService(ctx2).SearchAsync(query: null, includeDeprecated: false);
        withoutDeprecated.Select(r => r.Title).Should().BeEquivalentTo(new[] { "New Way" });

        await using var ctx3 = _db.NewContext();
        var withDeprecated = await NewService(ctx3).SearchAsync(query: null, includeDeprecated: true);
        withDeprecated.Select(r => r.Title).Should().BeEquivalentTo(new[] { "Old Way", "New Way" });
    }

    private SnippetService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<SnippetService>.Instance, _db.OrgContext);
}
