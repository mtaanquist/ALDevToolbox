using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Translation;

/// <summary>
/// Reads and writes the per-organisation translation memory
/// (<see cref="TranslationMemoryEntry"/>). Two writers feed it — the Object
/// Explorer XLIFF import and the Translator tool's exports — and one reader
/// serves it: <see cref="SuggestAsync"/>, which backs the editor's suggestion
/// chips with an exact match first and a <c>pg_trgm</c> trigram fuzzy fallback.
///
/// All reads are <c>AsNoTracking</c> and respect the tenant query filter on
/// <see cref="AppDbContext"/>; writes run inside an authenticated request.
/// See <c>.design/translator/</c> for the feature design.
/// </summary>
public sealed class TranslationMemoryService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<TranslationMemoryService> _logger;

    public TranslationMemoryService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<TranslationMemoryService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; TranslationMemoryService called outside an authenticated request.");

    /// <summary>
    /// Inserts new <c>(source, target)</c> pairs and bumps
    /// <see cref="TranslationMemoryEntry.HitCount"/> / <c>LastSeenAt</c> on
    /// pairs already known. Empty source or target entries are skipped, as are
    /// pairs whose source equals target (no-op "translations"). Returns the
    /// number of brand-new rows inserted. Safe to call with thousands of
    /// entries — existing rows are loaded by source hash in chunks rather than
    /// one query per entry.
    /// </summary>
    public async Task<int> UpsertAsync(IEnumerable<TranslationMemoryUpsert> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        // Normalise + dedupe inside the batch so a single import that repeats a
        // pair doesn't fight the unique index or double-count a hit.
        var byKey = new Dictionary<string, PreparedEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.SourceText) || string.IsNullOrWhiteSpace(e.TargetText)) continue;
            if (string.Equals(e.SourceText, e.TargetText, StringComparison.Ordinal)) continue;

            var srcLang = NormaliseLanguage(e.SourceLanguage);
            var tgtLang = NormaliseLanguage(e.TargetLanguage);
            if (string.IsNullOrEmpty(srcLang) || string.IsNullOrEmpty(tgtLang)) continue;

            var srcHash = Hash(e.SourceText);
            var tgtHash = Hash(e.TargetText);
            var key = $"{srcLang}{tgtLang}{srcHash}{tgtHash}";
            // Last write wins on collision within the batch.
            byKey[key] = new PreparedEntry(srcLang, tgtLang, e.SourceText, e.TargetText, srcHash, tgtHash, e.Kind, e.Origin);
        }

        if (byKey.Count == 0) return 0;

        var inserted = 0;
        const int chunkSize = 500;
        foreach (var chunk in byKey.Values.Chunk(chunkSize))
        {
            ct.ThrowIfCancellationRequested();

            var srcHashes = chunk.Select(c => c.SourceHash).Distinct().ToList();
            // Load existing rows that could match this chunk (same org via the
            // query filter, same source hashes), keyed by full natural key.
            var existing = await _db.TranslationMemory
                .Where(e => srcHashes.Contains(e.SourceHash))
                .ToListAsync(ct).ConfigureAwait(false);
            var existingByKey = existing.ToDictionary(
                e => $"{e.SourceLanguage}{e.TargetLanguage}{e.SourceHash}{e.TargetHash}",
                StringComparer.Ordinal);

            foreach (var c in chunk)
            {
                var key = $"{c.SourceLanguage}{c.TargetLanguage}{c.SourceHash}{c.TargetHash}";
                if (existingByKey.TryGetValue(key, out var row))
                {
                    row.HitCount += 1;
                    row.LastSeenAt = now;
                    row.UpdatedAt = now;
                    if (row.DeletedAt is not null) row.DeletedAt = null; // resurrect
                    continue;
                }

                _db.TranslationMemory.Add(new TranslationMemoryEntry
                {
                    OrganizationId = orgId,
                    SourceLanguage = c.SourceLanguage,
                    TargetLanguage = c.TargetLanguage,
                    SourceText = c.SourceText,
                    TargetText = c.TargetText,
                    SourceHash = c.SourceHash,
                    TargetHash = c.TargetHash,
                    Kind = string.IsNullOrEmpty(c.Kind) ? "other" : c.Kind,
                    Origin = c.Origin,
                    HitCount = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastSeenAt = now,
                });
                inserted++;
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation(
            "Translation memory upsert: Org={OrgId} Pairs={Pairs} Inserted={Inserted}",
            orgId, byKey.Count, inserted);
        return inserted;
    }

    /// <summary>
    /// Suggests translations for <paramref name="sourceText"/> in
    /// <paramref name="targetLanguage"/>: exact matches first (similarity 1.0),
    /// then trigram-similar sources ranked by similarity. When
    /// <paramref name="sourceLanguage"/> is null the source locale isn't
    /// constrained (useful when a file hasn't declared one). Results are
    /// de-duplicated by target text and capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<List<TranslationSuggestion>> SuggestAsync(
        string sourceText,
        string? sourceLanguage,
        string targetLanguage,
        int limit = 9,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return new List<TranslationSuggestion>();
        RequireOrganizationId();

        var tgt = NormaliseLanguage(targetLanguage);
        if (string.IsNullOrEmpty(tgt)) return new List<TranslationSuggestion>();
        var src = string.IsNullOrWhiteSpace(sourceLanguage) ? null : NormaliseLanguage(sourceLanguage);
        var srcHash = Hash(sourceText);

        var exact = await _db.TranslationMemory.AsNoTracking()
            .Where(e => e.DeletedAt == null && e.TargetLanguage == tgt
                && (src == null || e.SourceLanguage == src)
                && e.SourceHash == srcHash && e.SourceText == sourceText)
            .OrderByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
            .Take(limit)
            .Select(e => new TranslationSuggestion(e.TargetText, e.Origin, e.Kind, 1.0))
            .ToListAsync(ct).ConfigureAwait(false);

        var results = new List<TranslationSuggestion>(exact);
        var seenTargets = new HashSet<string>(exact.Select(r => r.TargetText), StringComparer.Ordinal);

        if (results.Count < limit)
        {
            // Trigram fuzzy. TrigramsAreSimilar maps to the `%` operator, which
            // the GIN index accelerates (default threshold 0.3); we then rank
            // by the exact similarity score.
            var fuzzy = await _db.TranslationMemory.AsNoTracking()
                .Where(e => e.DeletedAt == null && e.TargetLanguage == tgt
                    && (src == null || e.SourceLanguage == src)
                    && e.SourceText != sourceText
                    && EF.Functions.TrigramsAreSimilar(e.SourceText, sourceText))
                .Select(e => new
                {
                    e.TargetText,
                    e.Origin,
                    e.Kind,
                    Similarity = EF.Functions.TrigramsSimilarity(e.SourceText, sourceText),
                })
                .OrderByDescending(x => x.Similarity)
                .ThenByDescending(x => x.TargetText)
                .Take(limit * 3)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var f in fuzzy)
            {
                if (results.Count >= limit) break;
                if (!seenTargets.Add(f.TargetText)) continue;
                results.Add(new TranslationSuggestion(f.TargetText, f.Origin, f.Kind, f.Similarity));
            }
        }

        return results;
    }

    /// <summary>
    /// Bulk exact lookup for the "Pre-translate from memory" action: given many
    /// source strings, returns the best-known target for each (highest
    /// <c>hit_count</c>), in one query rather than one per unit. Sources with no
    /// exact memory hit are simply absent from the result.
    /// </summary>
    public async Task<Dictionary<string, TranslationSuggestion>> GetExactMatchesAsync(
        IReadOnlyCollection<string> sources,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, TranslationSuggestion>(StringComparer.Ordinal);
        if (sources.Count == 0) return result;
        RequireOrganizationId();

        var tgt = NormaliseLanguage(targetLanguage);
        if (string.IsNullOrEmpty(tgt)) return result;
        var src = string.IsNullOrWhiteSpace(sourceLanguage) ? null : NormaliseLanguage(sourceLanguage);

        // Map each distinct non-empty source to its hash; query by the bounded
        // hash set, then confirm the exact source text in memory.
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal); // hash -> source text
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            byHash[Hash(s)] = s;
        }
        if (byHash.Count == 0) return result;

        var hashes = byHash.Keys.ToList();
        foreach (var hashChunk in hashes.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var set = hashChunk.ToList();
            var rows = await _db.TranslationMemory.AsNoTracking()
                .Where(e => e.DeletedAt == null && e.TargetLanguage == tgt
                    && (src == null || e.SourceLanguage == src)
                    && set.Contains(e.SourceHash))
                .OrderByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
                .Select(e => new { e.SourceText, e.TargetText, e.Origin, e.Kind })
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var r in rows)
            {
                // First row per source wins (ordered by hit_count desc above).
                if (result.ContainsKey(r.SourceText)) continue;
                result[r.SourceText] = new TranslationSuggestion(r.TargetText, r.Origin, r.Kind, 1.0);
            }
        }

        return result;
    }

    private sealed class PreparedEntry
    {
        public PreparedEntry(string sourceLanguage, string targetLanguage, string sourceText,
            string targetText, string sourceHash, string targetHash, string kind, string? origin)
        {
            SourceLanguage = sourceLanguage;
            TargetLanguage = targetLanguage;
            SourceText = sourceText;
            TargetText = targetText;
            SourceHash = sourceHash;
            TargetHash = targetHash;
            Kind = kind;
            Origin = origin;
        }

        public string SourceLanguage { get; }
        public string TargetLanguage { get; }
        public string SourceText { get; }
        public string TargetText { get; }
        public string SourceHash { get; }
        public string TargetHash { get; }
        public string Kind { get; }
        public string? Origin { get; }
    }

    /// <summary>MD5 hex of the text — a bounded key for the unique index, not a security primitive.</summary>
    private static string Hash(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Normalises a language tag to BCP-47 <c>xx-XX</c> shape, matching the
    /// form <c>AlXliffParser</c> stores so memory keys line up with imported
    /// rows. Mirrors that parser's private normaliser (kept local to avoid
    /// widening its API for one caller).
    /// </summary>
    private static string NormaliseLanguage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().Replace('_', '-');
        var dash = s.IndexOf('-');
        if (dash <= 0 || dash >= s.Length - 1) return s.ToLowerInvariant();
        var lang = s.Substring(0, dash).ToLowerInvariant();
        var region = s.Substring(dash + 1).ToUpperInvariant();
        return $"{lang}-{region}";
    }
}

/// <summary>A pair offered for insertion into the translation memory.</summary>
public sealed record TranslationMemoryUpsert(
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Kind,
    string? Origin);

/// <summary>One suggested target for a source string, with its provenance and match strength (0–1).</summary>
public sealed record TranslationSuggestion(
    string TargetText,
    string? Origin,
    string Kind,
    double Similarity);
