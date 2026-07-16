using ALDevToolbox.Data;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translator;

/// <summary>
/// The coordinator's job is to serialise every <see cref="TranslationMemoryService"/>
/// call behind one gate so a background prefetch can never touch the shared
/// circuit <c>DbContext</c> at the same time as an on-demand lookup, vote, or
/// remove. Regression for the "second operation started on this context" failure
/// when a "remove suggestion" click raced an in-flight prefetch.
/// </summary>
public sealed class TranslationSuggestionCoordinatorTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private TranslationMemoryService NewMemory(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<TranslationMemoryService>.Instance);

    private static TranslationMemoryUpsert Pair(string source, string target) =>
        new("en-US", "da-DK", source, target, "caption", "Base Application");

    private async Task<long> EntryIdAsync(string sourceText)
    {
        await using var read = _db.NewContext();
        return await read.TranslationMemory
            .Where(e => e.SourceText == sourceText).Select(e => e.Id).SingleAsync();
    }

    [Fact]
    public async Task Gate_serialises_lookup_and_delete_on_one_shared_context()
    {
        // Seed a handful of entries to delete while lookups run.
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[]
            {
                Pair("Posting Date", "Bogføringsdato"),
                Pair("Quantity", "Antal"),
                Pair("Customer", "Kunde"),
                Pair("Vendor", "Leverandør"),
            });

        var toDelete = new[]
        {
            await EntryIdAsync("Quantity"),
            await EntryIdAsync("Customer"),
            await EntryIdAsync("Vendor"),
        };

        // One context shared across the coordinator, exactly like a Blazor Server
        // circuit. Without the gate, the concurrent lookups and deletes below
        // reliably throw "A second operation was started on this context".
        await using var shared = _db.NewContext();
        var coordinator = new TranslationSuggestionCoordinator(NewMemory(shared));

        var work = new List<Task>();
        for (var i = 0; i < 6; i++)
            work.Add(coordinator.LookupGatedAsync("Posting Date", "en-US", "da-DK"));
        foreach (var id in toDelete)
            work.Add(coordinator.DeleteGatedAsync(id));

        // The whole point: no InvalidOperationException from overlapping ops.
        var act = async () => await Task.WhenAll(work);
        await act.Should().NotThrowAsync();

        coordinator.Dispose();

        await using (var ctx = _db.NewContext())
            (await NewMemory(ctx).SuggestAsync("Quantity", "en-US", "da-DK")).Should().BeEmpty();
    }
}
