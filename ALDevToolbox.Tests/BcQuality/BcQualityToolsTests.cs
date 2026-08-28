using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.BcQuality;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.BcQuality;

/// <summary>
/// The MCP boundary for the BCQuality tools: input mapping, and every refusal
/// surfacing as <see cref="McpException"/> with a message that tells the agent
/// what to do next rather than a raw validation exception.
/// </summary>
public sealed class BcQualityToolsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BcQualityRepoFixture _repo = new();

    private const string Key = "microsoft/knowledge/ui/no-nested-grids.md";

    public void Dispose()
    {
        _repo.Dispose();
        _db.Dispose();
    }

    private async Task SeedAsync()
    {
        _repo.WriteArticle(Key, keywords: "[grid, telemetry]");
        _repo.WriteSample(Key, "bad", "al", "// nested");
        await using var ctx = _db.NewContext();
        await BcQualityTestServices.NewIngestService(ctx)
            .IngestFromDirectoryAsync(_repo.Root, "cafe123", null);
    }

    private static BcQualityTools NewTools(ALDevToolbox.Data.AppDbContext ctx) =>
        new(BcQualityTestServices.NewSearchService(ctx));

    [Fact]
    public async Task Search_returns_hits_with_the_citation_path_as_the_id()
    {
        await SeedAsync();

        await using var ctx = _db.NewContext();
        var hits = await NewTools(ctx).SearchAsync("telemetry");

        hits.Should().ContainSingle();
        hits[0].Id.Should().Be(Key);
        hits[0].SampleCount.Should().Be(1);
    }

    [Fact]
    public async Task Search_passes_the_bc_version_filter_through()
    {
        _repo.WriteArticle(Key, bcVersion: "[27..]", keywords: "[telemetry]");
        await using (var seed = _db.NewContext())
        {
            await BcQualityTestServices.NewIngestService(seed)
                .IngestFromDirectoryAsync(_repo.Root, "sha", null);
        }

        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await tools.SearchAsync("telemetry", bcVersion: 28)).Should().ContainSingle();
        (await tools.SearchAsync("telemetry", bcVersion: 26)).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_explains_that_the_mirror_is_empty_rather_than_returning_nothing()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewTools(ctx).SearchAsync("telemetry");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("has not been mirrored");
    }

    [Fact]
    public async Task Search_returns_an_empty_list_when_the_mirror_is_populated_but_nothing_matches()
    {
        await SeedAsync();

        await using var ctx = _db.NewContext();
        var hits = await NewTools(ctx).SearchAsync("quantum entanglement");

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_surfaces_a_validation_failure_as_an_mcp_error()
    {
        await SeedAsync();

        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).SearchAsync("  ");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("query");
    }

    [Fact]
    public async Task Get_article_returns_the_body_the_samples_and_the_commit()
    {
        await SeedAsync();

        await using var ctx = _db.NewContext();
        var article = await NewTools(ctx).GetArticleAsync(Key);

        article.Id.Should().Be(Key);
        article.CommitSha.Should().Be("cafe123");
        article.Samples.Should().ContainSingle()
            .Which.Path.Should().Be("microsoft/knowledge/ui/no-nested-grids.bad.al");
    }

    [Fact]
    public async Task Get_article_refuses_an_unknown_id_with_a_recoverable_message()
    {
        await SeedAsync();

        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).GetArticleAsync("microsoft/knowledge/ui/invented.md");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("search_bcquality");
    }
}

/// <summary>
/// The refresh cadence: first run when nothing has been ingested, daily after
/// that, and a backoff so an upstream outage does not turn the poll interval
/// into a clone interval. See <c>.design/bcquality.md</c>.
/// </summary>
public sealed class BcQualityRefreshSchedulerTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_mirror_that_has_never_been_ingested_is_due_immediately() =>
        BcQualityRefreshScheduler.IsDue(null, Now).Should().BeTrue();

    [Fact]
    public void A_mirror_refreshed_an_hour_ago_is_not_due() =>
        BcQualityRefreshScheduler.IsDue(
            new BcQualityIngestState { LastSuccessAt = Now.AddHours(-1) }, Now).Should().BeFalse();

    [Fact]
    public void A_mirror_refreshed_more_than_a_day_ago_is_due() =>
        BcQualityRefreshScheduler.IsDue(
            new BcQualityIngestState { LastSuccessAt = Now.AddHours(-25) }, Now).Should().BeTrue();

    [Fact]
    public void A_failed_attempt_backs_off_before_retrying()
    {
        var justFailed = new BcQualityIngestState { LastAttemptAt = Now.AddMinutes(-5), LastError = "boom" };
        BcQualityRefreshScheduler.IsDue(justFailed, Now).Should().BeFalse();

        var failedLongAgo = new BcQualityIngestState { LastAttemptAt = Now.AddHours(-2), LastError = "boom" };
        BcQualityRefreshScheduler.IsDue(failedLongAgo, Now).Should().BeTrue();
    }

    [Fact]
    public void A_failure_after_a_stale_success_also_backs_off()
    {
        var state = new BcQualityIngestState
        {
            LastSuccessAt = Now.AddDays(-3),
            LastAttemptAt = Now.AddMinutes(-10),
            LastError = "network unreachable",
        };

        BcQualityRefreshScheduler.IsDue(state, Now).Should().BeFalse();
        BcQualityRefreshScheduler.IsDue(state, Now.AddHours(2)).Should().BeTrue();
    }
}
