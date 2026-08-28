using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.BcQuality;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.BcQuality;

/// <summary>
/// Search and article retrieval over the mirrored knowledge base. These run
/// against real Postgres, which is the point: the ranking under test is the
/// weighted <c>tsvector</c> the database maintains, not anything computable in
/// memory. See <c>.design/bcquality.md</c>.
/// </summary>
public sealed class BcQualitySearchServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BcQualityRepoFixture _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
        _db.Dispose();
    }

    private async Task IngestAsync(string sha = "sha-under-test")
    {
        await using var ctx = _db.NewContext();
        await BcQualityTestServices.NewIngestService(ctx)
            .IngestFromDirectoryAsync(_repo.Root, sha, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Search_ranks_a_title_match_above_a_body_only_match()
    {
        _repo.WriteArticle("microsoft/knowledge/ui/telemetry-in-pages.md",
            title: "Emit telemetry from page triggers",
            description: "Pages should report what users do.",
            domain: "ui",
            keywords: "[pages, triggers]");
        _repo.WriteArticle("microsoft/knowledge/performance/partial-records.md",
            title: "Load only the fields you read",
            description: "Reading whole records wastes bandwidth.",
            domain: "performance",
            keywords: "[setloadfields, partial]",
            extraSections: "\n## Anti Pattern\n\nIgnoring telemetry when tuning a slow page.\n");

        await IngestAsync();

        await using var ctx = _db.NewContext();
        var hits = await BcQualityTestServices.NewSearchService(ctx).SearchAsync("telemetry");

        hits.Should().HaveCount(2);
        hits[0].Id.Should().Be("microsoft/knowledge/ui/telemetry-in-pages.md");
        hits[0].Title.Should().Be("Emit telemetry from page triggers");
        hits[0].Domain.Should().Be("ui");
        hits[0].BcVersion.Should().Be("[all]");
        hits[0].Keywords.Should().Contain("triggers");
    }

    [Fact]
    public async Task Search_ranks_a_keyword_match_above_a_body_only_match()
    {
        _repo.WriteArticle("microsoft/knowledge/performance/partial-records.md",
            title: "Load only the fields you read",
            description: "Reading whole records wastes bandwidth.",
            domain: "performance",
            keywords: "[setloadfields, partial]");
        _repo.WriteArticle("microsoft/knowledge/style/naming.md",
            title: "Name objects consistently",
            description: "Consistent names make code searchable.",
            domain: "style",
            keywords: "[naming]",
            extraSections: "\n## Best Practice\n\nMention setloadfields only in passing here.\n");

        await IngestAsync();

        await using var ctx = _db.NewContext();
        var hits = await BcQualityTestServices.NewSearchService(ctx).SearchAsync("setloadfields");

        hits.Should().HaveCount(2);
        hits[0].Id.Should().Be("microsoft/knowledge/performance/partial-records.md");
    }

    [Fact]
    public async Task Search_returns_a_snippet_showing_why_the_article_matched()
    {
        _repo.WriteArticle("microsoft/knowledge/performance/partial-records.md",
            title: "Load only the fields you read",
            description: "Reading whole records wastes bandwidth.",
            domain: "performance",
            keywords: "[partial]",
            extraSections: "\n## Anti Pattern\n\nCalling FindSet without narrowing the fields first.\n");

        await IngestAsync();

        await using var ctx = _db.NewContext();
        var hits = await BcQualityTestServices.NewSearchService(ctx).SearchAsync("FindSet");

        hits.Should().ContainSingle();
        hits[0].Snippet.Should().Contain("FindSet");
        hits[0].Summary.Should().Be("Reading whole records wastes bandwidth.");
    }

    [Fact]
    public async Task Search_filters_to_guidance_that_applies_to_the_target_bc_version()
    {
        _repo.WriteArticle("microsoft/knowledge/ui/everywhere.md",
            title: "Telemetry applies everywhere", bcVersion: "[all]", keywords: "[telemetry]");
        _repo.WriteArticle("microsoft/knowledge/ui/from-24.md",
            title: "Telemetry from 24 onwards", bcVersion: "[24..]", keywords: "[telemetry]");
        _repo.WriteArticle("microsoft/knowledge/ui/listed.md",
            title: "Telemetry on 20 and 21", bcVersion: "[20, 21]", keywords: "[telemetry]");
        _repo.WriteArticle("microsoft/knowledge/ui/ranged.md",
            title: "Telemetry across 18 to 20", bcVersion: "[18..20]", keywords: "[telemetry]");
        await IngestAsync();

        await using var ctx = _db.NewContext();
        var service = BcQualityTestServices.NewSearchService(ctx);

        var onTwentySix = await service.SearchAsync("telemetry", bcVersion: 26);
        onTwentySix.Select(h => h.Id).Should().BeEquivalentTo(
            "microsoft/knowledge/ui/everywhere.md",
            "microsoft/knowledge/ui/from-24.md");

        var onTwenty = await service.SearchAsync("telemetry", bcVersion: 20);
        onTwenty.Select(h => h.Id).Should().BeEquivalentTo(
            "microsoft/knowledge/ui/everywhere.md",
            "microsoft/knowledge/ui/listed.md",
            "microsoft/knowledge/ui/ranged.md");

        var unfiltered = await service.SearchAsync("telemetry");
        unfiltered.Should().HaveCount(4);
    }

    [Fact]
    public async Task Search_can_narrow_to_one_domain()
    {
        _repo.WriteArticle("microsoft/knowledge/ui/telemetry-pages.md",
            title: "Telemetry from pages", domain: "ui", keywords: "[telemetry]");
        _repo.WriteArticle("microsoft/knowledge/performance/telemetry-queries.md",
            title: "Telemetry from queries", domain: "performance", keywords: "[telemetry]");
        await IngestAsync();

        await using var ctx = _db.NewContext();
        var hits = await BcQualityTestServices.NewSearchService(ctx)
            .SearchAsync("telemetry", domain: "PERFORMANCE");

        hits.Should().ContainSingle().Which.Id.Should().Be("microsoft/knowledge/performance/telemetry-queries.md");
    }

    [Fact]
    public async Task Search_caps_the_result_count()
    {
        for (var i = 0; i < 12; i++)
        {
            _repo.WriteArticle($"microsoft/knowledge/ui/article-{i}.md",
                title: $"Telemetry note {i}", keywords: "[telemetry]");
        }
        await IngestAsync();

        await using var ctx = _db.NewContext();
        var service = BcQualityTestServices.NewSearchService(ctx);

        (await service.SearchAsync("telemetry")).Should().HaveCount(10);
        (await service.SearchAsync("telemetry", limit: 3)).Should().HaveCount(3);
        (await service.SearchAsync("telemetry", limit: 500)).Should().HaveCount(12);
    }

    [Fact]
    public async Task Search_refuses_an_empty_query()
    {
        await using var ctx = _db.NewContext();
        var service = BcQualityTestServices.NewSearchService(ctx);

        var act = () => service.SearchAsync("   ");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("query");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(26000)]
    public async Task Search_refuses_a_bc_version_that_is_not_a_major_version(int bcVersion)
    {
        await using var ctx = _db.NewContext();
        var service = BcQualityTestServices.NewSearchService(ctx);

        var act = () => service.SearchAsync("telemetry", bcVersion);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("bcVersion");
    }

    [Fact]
    public async Task Get_returns_the_article_its_samples_and_the_commit_it_came_from()
    {
        const string key = "microsoft/knowledge/ui/no-nested-grids.md";
        _repo.WriteArticle(key, extraSections: "\n## Anti Pattern\n\nWrapping a grid in a grid.\n");
        _repo.WriteSample(key, "bad", "al", "// nested");
        await IngestAsync("f00ba7");

        await using var ctx = _db.NewContext();
        var article = await BcQualityTestServices.NewSearchService(ctx).GetAsync(key);

        article.Should().NotBeNull();
        article!.Id.Should().Be(key);
        article.Title.Should().Be("Nested grids are not supported");
        article.Content.Should().Contain("## Anti Pattern");
        article.CommitSha.Should().Be("f00ba7");
        article.Samples.Should().ContainSingle();
        article.Samples[0].Path.Should().Be("microsoft/knowledge/ui/no-nested-grids.bad.al");
        article.Samples[0].Kind.Should().Be("bad");
        article.Samples[0].Content.Should().Be("// nested");
    }

    [Theory]
    // The forms a caller actually pastes an article path in.
    [InlineData("microsoft/knowledge/ui/no-nested-grids.md")]
    [InlineData("/microsoft/knowledge/ui/no-nested-grids.md")]
    [InlineData("  microsoft/knowledge/ui/no-nested-grids.md  ")]
    [InlineData("microsoft\\knowledge\\ui\\no-nested-grids.md")]
    [InlineData("microsoft/knowledge/ui/no-nested-grids")]
    public async Task Get_accepts_the_citation_path_in_the_forms_callers_use(string id)
    {
        _repo.WriteArticle("microsoft/knowledge/ui/no-nested-grids.md");
        await IngestAsync();

        await using var ctx = _db.NewContext();
        var article = await BcQualityTestServices.NewSearchService(ctx).GetAsync(id);

        article.Should().NotBeNull();
        article!.Id.Should().Be("microsoft/knowledge/ui/no-nested-grids.md");
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_article()
    {
        _repo.WriteArticle("microsoft/knowledge/ui/no-nested-grids.md");
        await IngestAsync();

        await using var ctx = _db.NewContext();
        var article = await BcQualityTestServices.NewSearchService(ctx)
            .GetAsync("microsoft/knowledge/ui/invented.md");

        article.Should().BeNull();
    }

    [Fact]
    public async Task Get_refuses_an_empty_id()
    {
        await using var ctx = _db.NewContext();
        var service = BcQualityTestServices.NewSearchService(ctx);

        var act = () => service.GetAsync(" ");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("id");
    }

    [Fact]
    public async Task HasContent_reports_whether_the_mirror_has_been_populated()
    {
        await using (var before = _db.NewContext())
        {
            (await BcQualityTestServices.NewSearchService(before).HasContentAsync()).Should().BeFalse();
        }

        _repo.WriteArticle("microsoft/knowledge/ui/no-nested-grids.md");
        await IngestAsync();

        await using var after = _db.NewContext();
        (await BcQualityTestServices.NewSearchService(after).HasContentAsync()).Should().BeTrue();
    }
}
