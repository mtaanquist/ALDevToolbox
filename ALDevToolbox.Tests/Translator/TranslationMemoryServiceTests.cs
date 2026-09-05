using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
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

    // ── Curation: vote / delete / restore / search ──────────────────────────

    /// <summary>Creates a user in the default org and points the org context's CurrentUserId at it (votes need an acting user).</summary>
    private async Task<int> SeedActingUserAsync(UserRole role = UserRole.Editor)
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = $"u{Guid.NewGuid():N}@example.test",
                PasswordHash = "x",
                DisplayName = "Tester",
                Role = role,
                CreatedAt = DateTime.UtcNow,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            id = user.Id;
        }
        _db.OrgContext.CurrentUserId = id;
        return id;
    }

    private async Task<long> EntryIdAsync(string sourceText, string targetText)
    {
        await using var read = _db.NewContext();
        return await read.TranslationMemory
            .Where(e => e.SourceText == sourceText && e.TargetText == targetText)
            .Select(e => e.Id).SingleAsync();
    }

    [Fact]
    public async Task Vote_adjusts_score_clears_and_switches()
    {
        await SeedActingUserAsync();
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Vote me", "Stem på mig") });
        var entryId = await EntryIdAsync("Vote me", "Stem på mig");

        await using (var ctx = _db.NewContext())
        {
            var m = NewMemory(ctx);
            (await m.VoteAsync(entryId, 1)).Should().BeEquivalentTo(new { Score = 1, MyVote = 1 });
            (await m.VoteAsync(entryId, 0)).Should().BeEquivalentTo(new { Score = 0, MyVote = 0 });   // clear
            (await m.VoteAsync(entryId, -1)).Should().BeEquivalentTo(new { Score = -1, MyVote = -1 });
            (await m.VoteAsync(entryId, 1)).Should().BeEquivalentTo(new { Score = 1, MyVote = 1 });    // switch -1 -> +1
        }

        await using (var read = _db.NewContext())
        {
            (await read.TranslationMemoryVotes.CountAsync(v => v.EntryId == entryId))
                .Should().Be(1, because: "one vote row per user, replaced not duplicated");
            (await read.TranslationMemory.Where(e => e.Id == entryId).Select(e => e.Score).SingleAsync())
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task Concurrent_upvotes_from_different_users_do_not_lose_score()
    {
        // Regression for #478: the old read-modify-write on entry.Score lost
        // increments when several users voted on one entry at once. The atomic
        // UPDATE ... SET score = score + delta must land every vote.
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Concurrent", "Samtidig") });
        var entryId = await EntryIdAsync("Concurrent", "Samtidig");

        const int voters = 12;
        var userIds = new List<int>();
        await using (var ctx = _db.NewContext())
        {
            for (var i = 0; i < voters; i++)
            {
                var user = new User
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Email = $"voter{i}-{Guid.NewGuid():N}@example.test",
                    PasswordHash = "x",
                    DisplayName = $"Voter {i}",
                    Role = UserRole.Editor,
                    CreatedAt = DateTime.UtcNow,
                };
                ctx.Users.Add(user);
                await ctx.SaveChangesAsync();
                userIds.Add(user.Id);
            }
        }

        // Each voter gets its own context + org-context (with its own
        // CurrentUserId) + service — DbContext isn't thread-safe. Fire them
        // together so the increments genuinely contend on the row.
        var tasks = userIds.Select(uid => Task.Run(async () =>
        {
            var orgCtx = new AmbientOrganizationContext
            {
                CurrentOrganizationId = TestDb.DefaultOrgId,
                CurrentUserId = uid,
            };
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_db.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var ctx = new AppDbContext(options, orgCtx);
            var svc = new TranslationMemoryService(ctx, orgCtx, NullLogger<TranslationMemoryService>.Instance);
            await svc.VoteAsync(entryId, 1);
        })).ToArray();
        await Task.WhenAll(tasks);

        await using var read = _db.NewContext();
        (await read.TranslationMemory.Where(e => e.Id == entryId).Select(e => e.Score).SingleAsync())
            .Should().Be(voters, because: "every concurrent upvote is applied via an atomic increment");
        (await read.TranslationMemoryVotes.CountAsync(v => v.EntryId == entryId))
            .Should().Be(voters);
    }

    [Fact]
    public async Task Suggest_ranks_upvoted_above_more_frequent()
    {
        await SeedActingUserAsync();
        await using (var ctx = _db.NewContext())
        {
            var m = NewMemory(ctx);
            await m.UpsertAsync(new[] { Pair("Status", "Tilstand") });       // A
            await m.UpsertAsync(new[] { Pair("Status", "Tilstand") });       // A again -> hit_count 2
            await m.UpsertAsync(new[] { Pair("Status", "Status-felt") });    // B -> hit_count 1
        }
        var bId = await EntryIdAsync("Status", "Status-felt");
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).VoteAsync(bId, 1);

        await using (var ctx = _db.NewContext())
        {
            var hits = await NewMemory(ctx).SuggestAsync("Status", "en-US", "da-DK");
            hits.Should().HaveCountGreaterThanOrEqualTo(2);
            hits[0].TargetText.Should().Be("Status-felt", because: "an upvoted pair outranks a more-frequent unvoted one");
            hits[0].MyVote.Should().Be(1);
        }
    }

    [Fact]
    public async Task Delete_hides_from_suggestions_and_restore_brings_it_back()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Remove me", "Slet mig") });
        var id = await EntryIdAsync("Remove me", "Slet mig");

        await using (var ctx = _db.NewContext()) await NewMemory(ctx).DeleteAsync(id);
        await using (var ctx = _db.NewContext())
            (await NewMemory(ctx).SuggestAsync("Remove me", "en-US", "da-DK")).Should().BeEmpty();

        await using (var ctx = _db.NewContext()) await NewMemory(ctx).RestoreAsync(id);
        await using (var ctx = _db.NewContext())
            (await NewMemory(ctx).SuggestAsync("Remove me", "en-US", "da-DK")).Should().ContainSingle();
    }

    [Fact]
    public async Task Search_filters_text_and_respects_include_deleted()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Apple", "Æble"), Pair("Banana", "Banan") });
        var appleId = await EntryIdAsync("Apple", "Æble");
        await using (var ctx = _db.NewContext()) await NewMemory(ctx).DeleteAsync(appleId);

        await using (var ctx = _db.NewContext())
        {
            var m = NewMemory(ctx);

            var active = await m.SearchAsync(new MemorySearchQuery(Text: "an"));
            active.Items.Should().OnlyContain(i => !i.IsDeleted);
            active.Items.Select(i => i.SourceText).Should().Contain("Banana").And.NotContain("Apple");

            var withDeleted = await m.SearchAsync(new MemorySearchQuery(Text: "Apple", IncludeDeleted: true));
            withDeleted.Items.Should().ContainSingle(i => i.SourceText == "Apple" && i.IsDeleted);
        }
    }

    [Fact]
    public async Task Search_matches_case_insensitively_on_source_and_target()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Posting Date", "Bogføringsdato") });

        await using (var ctx = _db.NewContext())
        {
            var m = NewMemory(ctx);
            (await m.SearchAsync(new MemorySearchQuery(Text: "POSTING"))).Items
                .Should().ContainSingle(i => i.SourceText == "Posting Date");
            (await m.SearchAsync(new MemorySearchQuery(Text: "bogføringsdato"))).Items
                .Should().ContainSingle(i => i.TargetText == "Bogføringsdato");
            (await m.SearchAsync(new MemorySearchQuery(Origin: "base APPLICATION"))).Items
                .Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Search_treats_like_wildcards_in_the_needle_literally()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).UpsertAsync(new[]
            {
                Pair("100% complete", "100% færdig"),
                Pair("snake_case name", "snake_case navn"),
                Pair("path\\to\\file", "sti\\til\\fil"),
                Pair("plain text", "almindelig tekst"),
            });
        }

        await using (var ctx = _db.NewContext())
        {
            var m = NewMemory(ctx);

            // Bare wildcards must not match every row.
            (await m.SearchAsync(new MemorySearchQuery(Text: "%"))).Items
                .Should().ContainSingle(i => i.SourceText == "100% complete");
            (await m.SearchAsync(new MemorySearchQuery(Text: "_"))).Items
                .Should().ContainSingle(i => i.SourceText == "snake_case name");
            (await m.SearchAsync(new MemorySearchQuery(Text: "\\"))).Items
                .Should().ContainSingle(i => i.SourceText == "path\\to\\file");

            // Matches several rows if `%` were a wildcard; nothing literally.
            (await m.SearchAsync(new MemorySearchQuery(Text: "n%e"))).Items.Should().BeEmpty();
        }
    }

    // ── Learning from a finished file ────────────────────────────────────
    // Both ways out of the Translator - the export download and the save back
    // to a repository (#625) - feed the memory through the same method, so a
    // translation is remembered whichever way the translator takes delivery.

    [Fact]
    public async Task Learning_from_a_finished_file_remembers_its_translated_pairs()
    {
        await using (var ctx = _db.NewContext())
        {
            var learned = await NewMemory(ctx).LearnFromXliffAsync(Xliff("Amount", "Beløb"));
            learned.Should().Be(1);
        }

        await using (var ctx = _db.NewContext())
        {
            var hits = await NewMemory(ctx).SuggestAsync("Amount", "en-US", "da-DK");
            hits.Should().ContainSingle();
            hits[0].TargetText.Should().Be("Beløb");
            hits[0].Origin.Should().Be("PaymentImport", "the file says which app it came from");
        }
    }

    [Fact]
    public async Task An_explicit_origin_wins_over_the_one_inside_the_file()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).LearnFromXliffAsync(Xliff("Amount", "Beløb"), "PaymentImport.da-DK.xlf");
        }

        await using (var ctx = _db.NewContext())
        {
            (await NewMemory(ctx).SuggestAsync("Amount", "en-US", "da-DK"))[0]
                .Origin.Should().Be("PaymentImport.da-DK.xlf");
        }
    }

    [Fact]
    public async Task A_file_that_does_not_parse_teaches_nothing_rather_than_failing()
    {
        // The caller has already given the user what they asked for - a
        // download, or a pull request - by the time this runs.
        await using var ctx = _db.NewContext();
        (await NewMemory(ctx).LearnFromXliffAsync("not xliff at all")).Should().Be(0);
    }

    [Fact]
    public async Task Untranslated_units_are_not_remembered_as_translations()
    {
        await using var ctx = _db.NewContext();
        (await NewMemory(ctx).LearnFromXliffAsync(Xliff("Amount", ""))).Should().Be(0);
    }

    // ── Where a pair came from (#631) ────────────────────────────────────

    [Fact]
    public async Task A_pair_learned_from_a_repository_carries_the_file_into_its_suggestions()
    {
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).UpsertAsync(new[]
            {
                new TranslationMemoryUpsert("en-US", "da-DK", "Posting Date", "Bogføringsdato", "caption",
                    "customer-app / PaymentImport", "cronus-dk/customer-app",
                    "PaymentImport/Translations/PaymentImport.da-DK.xlf"),
            });
        }

        await using (var ctx = _db.NewContext())
        {
            var hit = (await NewMemory(ctx).SuggestAsync("Posting Date", "en-US", "da-DK")).Single();
            hit.SourceRepository.Should().Be("cronus-dk/customer-app");
            hit.SourcePath.Should().Be("PaymentImport/Translations/PaymentImport.da-DK.xlf");

            var view = (await NewMemory(ctx).SearchAsync(new MemorySearchQuery(Text: "Posting"))).Items.Single();
            view.SourceRepository.Should().Be("cronus-dk/customer-app");
            view.SourcePath.Should().Be("PaymentImport/Translations/PaymentImport.da-DK.xlf");
        }
    }

    [Fact]
    public async Task Seeing_a_pair_in_a_second_file_moves_the_attribution_to_that_file()
    {
        // The unique pair index is unchanged, so a pair in two files keeps one
        // attribution - and it has to be the most recent one, or "where did this
        // come from" points at a file that may no longer say it.
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).UpsertAsync(new[]
            {
                new TranslationMemoryUpsert("en-US", "da-DK", "Quantity", "Antal", "caption",
                    "customer-app / PaymentImport", "cronus-dk/customer-app", "PaymentImport/Translations/a.da-DK.xlf"),
            });
        }
        await using (var ctx = _db.NewContext())
        {
            await NewMemory(ctx).UpsertAsync(new[]
            {
                new TranslationMemoryUpsert("en-US", "da-DK", "Quantity", "Antal", "caption",
                    "other-app / Sales", "cronus-dk/other-app", "Sales/Translations/b.da-DK.xlf"),
            });
        }

        await using (var read = _db.NewContext())
        {
            var row = await read.TranslationMemory.SingleAsync(e => e.SourceText == "Quantity");
            row.HitCount.Should().Be(2);
            row.Origin.Should().Be("other-app / Sales");
            row.SourceRepository.Should().Be("cronus-dk/other-app");
            row.SourcePath.Should().Be("Sales/Translations/b.da-DK.xlf");
        }
    }

    [Fact]
    public async Task A_pair_that_came_from_an_upload_names_no_repository()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Amount", "Beløb") });

        await using (var ctx = _db.NewContext())
        {
            var hit = (await NewMemory(ctx).SuggestAsync("Amount", "en-US", "da-DK")).Single();
            hit.SourceRepository.Should().BeNull();
            hit.SourcePath.Should().BeNull();
        }
    }

    private static string Xliff(string source, string target) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="da-DK" original="PaymentImport">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>{source}</source>
                  <target state="translated">{target}</target>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;
}
