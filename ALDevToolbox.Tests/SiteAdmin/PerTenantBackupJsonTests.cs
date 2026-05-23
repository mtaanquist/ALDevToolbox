using System.Text;
using System.Text.Json;
using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// <see cref="PerTenantBackupJson.BatchJsonArrayAsync"/> exists because a
/// blob-heavy tenant tripped Postgres' 256 MB jsonb-value ceiling on
/// restore. Pin the chunking contract so a future refactor can't quietly
/// undo it and let the original bug come back.
/// </summary>
public sealed class PerTenantBackupJsonTests
{
    [Fact]
    public async Task Empty_array_yields_no_batches()
    {
        using var stream = StreamOf("[]");

        var batches = await CollectAsync(PerTenantBackupJson.BatchJsonArrayAsync(stream, maxBatchBytes: 1024));

        batches.Should().BeEmpty();
    }

    [Fact]
    public async Task Single_small_array_yields_one_batch_with_every_row()
    {
        var input = """[{"id":1,"name":"a"},{"id":2,"name":"b"},{"id":3,"name":"c"}]""";
        using var stream = StreamOf(input);

        var batches = await CollectAsync(PerTenantBackupJson.BatchJsonArrayAsync(stream, maxBatchBytes: 4096));

        batches.Should().HaveCount(1);
        var roundTrip = JsonSerializer.Deserialize<JsonElement[]>(batches[0])!;
        roundTrip.Should().HaveCount(3);
        roundTrip[0].GetProperty("id").GetInt32().Should().Be(1);
        roundTrip[2].GetProperty("name").GetString().Should().Be("c");
    }

    [Fact]
    public async Task Splits_when_rows_exceed_the_byte_budget()
    {
        // Each row's GetRawText() is roughly 60 bytes here ("payload" plus
        // a 50-char body). With maxBatchBytes=130 we expect roughly two
        // rows per batch (60 + 60 + brackets + comma fits, a third row
        // doesn't).
        var rows = Enumerable.Range(0, 6)
            .Select(i => new { id = i, payload = new string('x', 50) })
            .ToArray();
        var input = JsonSerializer.Serialize(rows);
        using var stream = StreamOf(input);

        var batches = await CollectAsync(PerTenantBackupJson.BatchJsonArrayAsync(stream, maxBatchBytes: 130));

        batches.Should().HaveCountGreaterThan(1, "the source is much larger than the byte budget");
        var allRows = batches
            .SelectMany(b => JsonSerializer.Deserialize<JsonElement[]>(b)!)
            .Select(e => e.GetProperty("id").GetInt32())
            .ToArray();
        allRows.Should().Equal(0, 1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task Throws_when_a_single_row_exceeds_the_batch_limit()
    {
        var bigRow = new { id = 1, payload = new string('x', 500) };
        var input = JsonSerializer.Serialize(new[] { bigRow });
        using var stream = StreamOf(input);

        var act = async () => await CollectAsync(
            PerTenantBackupJson.BatchJsonArrayAsync(stream, maxBatchBytes: 100));

        // Surface the offending size and the configured limit so an
        // operator can see why the restore aborted.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("100") && ex.Message.Contains("bytes"));
    }

    [Fact]
    public async Task Preserves_row_contents_byte_for_byte()
    {
        // Round-tripping through JsonElement.GetRawText() must keep
        // numbers, escapes, and unicode intact so jsonb-typed columns
        // restore exactly.
        var input = """[{"a":1,"b":"héé","c":null,"d":[1,2,3],"e":{"f":1.5}}]""";
        using var stream = StreamOf(input);

        var batches = await CollectAsync(PerTenantBackupJson.BatchJsonArrayAsync(stream, maxBatchBytes: 1024));

        batches.Should().HaveCount(1);
        using var parsed = JsonDocument.Parse(batches[0]);
        var row = parsed.RootElement[0];
        row.GetProperty("a").GetInt32().Should().Be(1);
        row.GetProperty("b").GetString().Should().Be("héé");
        row.GetProperty("c").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("d").GetArrayLength().Should().Be(3);
        row.GetProperty("e").GetProperty("f").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task Each_batch_stays_within_byte_budget_for_multibyte_rows()
    {
        // Regression guard: the budget is a *byte* ceiling. Each '日' is one
        // UTF-16 char but three UTF-8 bytes, so a char-based measurement would
        // pack ~3x too much into a batch and blow past maxBatchBytes. Assert
        // the actual UTF-8 size of every emitted batch respects the budget.
        var rows = Enumerable.Range(0, 40)
            .Select(i => new { id = i, text = new string('日', 80) })
            .ToArray();
        using var stream = StreamOf(JsonSerializer.Serialize(rows));
        const long maxBatchBytes = 4096;

        var batches = await CollectAsync(PerTenantBackupJson.BatchJsonArrayAsync(stream, maxBatchBytes));

        batches.Should().HaveCountGreaterThan(1);
        batches.Should().OnlyContain(b => Encoding.UTF8.GetByteCount(b) <= maxBatchBytes);
        var allIds = batches
            .SelectMany(b => JsonSerializer.Deserialize<JsonElement[]>(b)!)
            .Select(e => e.GetProperty("id").GetInt32())
            .ToArray();
        allIds.Should().Equal(Enumerable.Range(0, 40).ToArray());
    }

    private static MemoryStream StreamOf(string json) => new(Encoding.UTF8.GetBytes(json));

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}
