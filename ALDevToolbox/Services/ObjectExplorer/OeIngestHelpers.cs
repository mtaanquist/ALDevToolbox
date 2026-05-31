using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Persistence helpers shared by the Object Explorer ingest paths
/// (<see cref="ReleaseImportService"/> for AL <c>.app</c> packages and
/// <see cref="CalImportService"/> for legacy C/AL TXT exports). Factored out
/// once the second ingest path needed the same content-addressed blob store
/// and the same per-flush chunking discipline.
/// </summary>
internal static class OeIngestHelpers
{
    /// <summary>
    /// Rows-per-<c>SaveChanges</c> for source files and for objects. Base App
    /// carries several thousand files with multi-KB content each, and EF's
    /// batch builder allocates the whole batch text + parameter array in
    /// memory; bounded chunks keep the per-flush footprint flat. The C/AL path
    /// shares the same envelope (a W1+DK export is ~5k–8k objects).
    /// </summary>
    public const int FileChunkSize = 50;
    public const int ObjectChunkSize = 50;

    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="content"/>, as uppercase hex.</summary>
    public static string HashHex(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    /// <summary>1-based line count (a non-empty string has at least one line).</summary>
    public static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        int n = 1;
        foreach (var c in content) if (c == '\n') n++;
        return n;
    }

    /// <summary>
    /// Inserts a chunk's distinct source blobs into the shared, content-addressed
    /// <c>oe_file_contents</c> store, keyed by hash. <c>ON CONFLICT DO NOTHING</c>
    /// makes it idempotent and race-safe: two orgs importing the same source
    /// concurrently both succeed and the blob is stored exactly once. Must run
    /// before the <c>ModuleFile</c> rows referencing these hashes are saved, so
    /// their <c>content_hash</c> FK resolves. Raw SQL because EF can't express a
    /// batch upsert and a duplicate-PK <c>Add</c> would throw.
    /// </summary>
    public static async Task UpsertFileContentsAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, (string Content, int Length, int LineCount)> contents,
        CancellationToken ct)
    {
        if (contents.Count == 0) return;
        var hashes = new string[contents.Count];
        var bodies = new string[contents.Count];
        var lengths = new int[contents.Count];
        var lines = new int[contents.Count];
        int i = 0;
        foreach (var (hash, v) in contents)
        {
            hashes[i] = hash;
            bodies[i] = v.Content;
            lengths[i] = v.Length;
            lines[i] = v.LineCount;
            i++;
        }
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO oe_file_contents (content_hash, content, content_length, line_count) " +
            "SELECT * FROM unnest({0}::text[], {1}::text[], {2}::int[], {3}::int[]) " +
            "ON CONFLICT (content_hash) DO NOTHING",
            new object[] { hashes, bodies, lengths, lines }, ct).ConfigureAwait(false);
    }
}
