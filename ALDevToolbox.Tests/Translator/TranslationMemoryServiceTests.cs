using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translator;

/// <summary>
/// Coverage for the translation memory (<see cref="TranslationMemoryService"/>):
/// upsert + dedupe + hit-count, exact and <c>pg_trgm</c> fuzzy suggestions, the
/// bulk pre-translate lookup, and tenant isolation. Runs against a real Postgres
/// (the migration enables pg_trgm and builds the GIN trigram index).
/// </summary>
public sealed class TranslationMemoryServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private TranslationMemoryService NewMemory(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<TranslationMemoryService>.Instance);

    private static TranslationMemoryUpsert Pair(string source, string target, string kind = "caption", string? origin = "Base Application") =>
        new("en-US", "da-DK", source, target, kind, origin);

    [Fact]
    public async Task Upsert_then_suggest_returns_exact_match()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Posting Date", "Bogføringsdato") });
        }

        await using (var ctx = _db.NewContext())
        {
            var hits = await NewMemory(ctx).SuggestAsync("Posting Date", "en-US", "da-DK");
            hits.Should().ContainSingle();
            hits[0].TargetText.Should().Be("Bogføringsdato");
            hits[0].Similarity.Should().Be(1.0);
            hits[0].Origin.Should().Be("Base Application");
        }
    }

    [Fact]
    public async Task Upsert_existing_pair_bumps_hit_count_instead_of_duplicating()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Quantity", "Antal") });
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Quantity", "Antal") });

        await using (var read = _db.NewContext())
        {
            var rows = await read.TranslationMemory
                .Where(e => e.SourceText == "Quantity").ToListAsync();
            rows.Should().ContainSingle(because: "the same pair must not duplicate");
            rows[0].HitCount.Should().Be(2);
        }
    }

    [Fact]
    public async Task Suggest_returns_fuzzy_match_above_threshold()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Posting Date", "Bogføringsdato") });

        await using (var ctx = _db.NewContext())
        {
            // Close-but-not-exact source — trigram similarity should still find it.
            var hits = await NewMemory(ctx).SuggestAsync("Posting Dates", "en-US", "da-DK");
            hits.Should().Contain(h => h.TargetText == "Bogføringsdato");
            hits.First(h => h.TargetText == "Bogføringsdato").Similarity.Should().BeLessThan(1.0).And.BeGreaterThan(0.0);
        }
    }

    [Fact]
    public async Task Suggest_keeps_distinct_targets_for_the_same_source()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).UpsertAsync(new[]
            {
                Pair("Unit Price", "Enhedspris", origin: "Base Application"),
                Pair("Unit Price", "Kostpris", origin: "Other Ext"),
            });
        }

        await using (var ctx = _db.NewContext())
        {
            var hits = await NewMemory(ctx).SuggestAsync("Unit Price", "en-US", "da-DK");
            hits.Select(h => h.TargetText).Should().BeEquivalentTo(new[] { "Enhedspris", "Kostpris" });
        }
    }

    [Fact]
    public async Task Upsert_skips_empty_and_source_equals_target()
    {
        await using (var ctx = _db.NewContext())
        {
            var inserted = await NewMemory(ctx).UpsertAsync(new[]
            {
                Pair("Open", ""),          // empty target
                Pair("Antal", "Antal"),    // source == target (generator no-op)
            });
            inserted.Should().Be(0);
        }
        await using (var read = _db.NewContext())
            (await read.TranslationMemory.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetExactMatches_returns_best_target_per_source()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).UpsertAsync(new[]
            {
                Pair("Posting Date", "Bogføringsdato"),
                Pair("Document No.", "Bilagsnr."),
            });
        }

        await using (var ctx = _db.NewContext())
        {
            var map = await NewMemory(ctx).GetExactMatchesAsync(
                new[] { "Posting Date", "Document No.", "Nonexistent" }, "en-US", "da-DK");
            map.Should().HaveCount(2);
            map["Posting Date"].TargetText.Should().Be("Bogføringsdato");
            map["Document No."].TargetText.Should().Be("Bilagsnr.");
            map.Should().NotContainKey("Nonexistent");
        }
    }

    [Fact]
    public async Task Memory_is_isolated_per_organisation()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Posting Date", "Bogføringsdato") });

        // A user in another organisation must not see it.
        var otherOrg = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.OtherOrgId };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var otherCtx = new AppDbContext(options, otherOrg);
        var hits = await new TranslationMemoryService(
            otherCtx, otherOrg, NullLogger<TranslationMemoryService>.Instance)
            .SuggestAsync("Posting Date", "en-US", "da-DK");
        hits.Should().BeEmpty(because: "the tenant query filter scopes memory to the acting org");
    }
}
