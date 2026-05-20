using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ALDevToolbox.Services;

/// <summary>
/// Streaming helpers that keep <see cref="PerTenantBackupService"/> clear of
/// Postgres' 256 MB jsonb-value ceiling. A naive
/// <c>jsonb_agg(to_jsonb(t))</c> dump and an all-rows-at-once
/// <c>jsonb_populate_recordset</c> restore both build a single jsonb that
/// blows past the limit on tenants with blob-heavy tables
/// (<c>oe_module_files</c> in particular). These helpers stream rows on
/// the way out and re-batch them under a safe size on the way back in.
/// </summary>
internal static class PerTenantBackupJson
{
    /// <summary>
    /// Hard ceiling for any single jsonb value PostgreSQL accepts. Defined
    /// in the server as <c>JENTRY_OFFLENMASK</c> — 2^28 − 1 bytes ≈ 256 MB.
    /// We size batches well under this on the restore side and surface a
    /// clear error if a single row's serialised text exceeds it.
    /// </summary>
    public const long PostgresJsonbValueLimit = 268_435_455;

    /// <summary>
    /// Streams a JSON array from <paramref name="source"/> and yields it
    /// back as one or more JSON-array strings, each at most
    /// <paramref name="maxBatchBytes"/> bytes long. The caller hands each
    /// batch to <c>jsonb_populate_recordset</c> so the value-side cast to
    /// jsonb stays well under the 256 MB limit.
    ///
    /// <para>
    /// The source must be a JSON array at the root. Rows are kept verbatim
    /// — <see cref="JsonElement.GetRawText"/> preserves the original text
    /// exactly so jsonb-typed columns round-trip without re-quoting.
    /// </para>
    /// </summary>
    public static async IAsyncEnumerable<string> BatchJsonArrayAsync(
        Stream source,
        long maxBatchBytes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (maxBatchBytes < 2) throw new ArgumentOutOfRangeException(nameof(maxBatchBytes));

        var batch = new StringBuilder();
        batch.Append('[');
        var count = 0;

        await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
            source, cancellationToken: ct).ConfigureAwait(false))
        {
            var raw = element.GetRawText();
            // Adding 2 for the brackets and 1 for the leading comma when not first.
            var addedLength = raw.Length + (count == 0 ? 0 : 1);
            if (raw.Length + 2L > maxBatchBytes)
            {
                throw new InvalidOperationException(
                    $"A single row's serialised JSON is {raw.Length:N0} bytes, exceeding the batch limit of {maxBatchBytes:N0}. "
                    + "Reduce row size or raise the limit (but stay below PostgreSQL's 256 MB jsonb ceiling).");
            }
            if (count > 0 && batch.Length + addedLength + 1L > maxBatchBytes)
            {
                batch.Append(']');
                yield return batch.ToString();
                batch.Clear();
                batch.Append('[');
                count = 0;
                addedLength = raw.Length;
            }
            if (count > 0) batch.Append(',');
            batch.Append(raw);
            count++;
        }

        if (count > 0)
        {
            batch.Append(']');
            yield return batch.ToString();
        }
    }
}
