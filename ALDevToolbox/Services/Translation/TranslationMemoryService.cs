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

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; TranslationMemoryService vote called outside an authenticated request.");

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
    /// then trigram-similar sources. Within each tier the ranking key is the
    /// net vote <see cref="TranslationMemoryEntry.Score"/> (a thumbs-up floats a
    /// good pair above a more-frequent but worse one), then <c>hit_count</c>,
    /// then recency. When <paramref name="sourceLanguage"/> is null the source
    /// locale isn't constrained. Results are de-duplicated by target text and
    /// capped at <paramref name="limit"/>; each carries the acting user's own
    /// vote so the chip can show its state.
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
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
            .Take(limit)
            .Select(e => new TranslationSuggestion(e.Id, e.TargetText, e.Origin, e.Kind, 1.0, e.Score, 0))
            .ToListAsync(ct).ConfigureAwait(false);

        var results = new List<TranslationSuggestion>(exact);
        var seenTargets = new HashSet<string>(exact.Select(r => r.TargetText), StringComparer.Ordinal);

        if (results.Count < limit)
        {
            // Trigram fuzzy. TrigramsAreSimilar maps to the `%` operator, which
            // the GIN index accelerates (default threshold 0.3); we rank by
            // closeness first, then vote score.
            var fuzzy = await _db.TranslationMemory.AsNoTracking()
                .Where(e => e.DeletedAt == null && e.TargetLanguage == tgt
                    && (src == null || e.SourceLanguage == src)
                    && e.SourceText != sourceText
                    && EF.Functions.TrigramsAreSimilar(e.SourceText, sourceText))
                .Select(e => new
                {
                    e.Id,
                    e.TargetText,
                    e.Origin,
                    e.Kind,
                    e.Score,
                    Similarity = EF.Functions.TrigramsSimilarity(e.SourceText, sourceText),
                })
                .OrderByDescending(x => x.Similarity)
                .ThenByDescending(x => x.Score)
                .ThenByDescending(x => x.TargetText)
                .Take(limit * 3)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var f in fuzzy)
            {
                if (results.Count >= limit) break;
                if (!seenTargets.Add(f.TargetText)) continue;
                results.Add(new TranslationSuggestion(f.Id, f.TargetText, f.Origin, f.Kind, f.Similarity, f.Score, 0));
            }
        }

        // Stamp the acting user's own vote on each result so the chips render
        // their up/down state (one extra query for the handful we return).
        var userId = _orgContext.CurrentUserId;
        if (userId is not null && results.Count > 0)
        {
            var ids = results.Select(r => r.EntryId).ToList();
            var mine = await _db.TranslationMemoryVotes.AsNoTracking()
                .Where(v => v.UserId == userId && ids.Contains(v.EntryId))
                .Select(v => new { v.EntryId, v.Value })
                .ToListAsync(ct).ConfigureAwait(false);
            if (mine.Count > 0)
            {
                var byEntry = mine.ToDictionary(v => v.EntryId, v => (int)v.Value);
                for (var i = 0; i < results.Count; i++)
                {
                    if (byEntry.TryGetValue(results[i].EntryId, out var mv))
                        results[i] = results[i] with { MyVote = mv };
                }
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
                .OrderByDescending(e => e.Score).ThenByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
                .Select(e => new { e.Id, e.SourceText, e.TargetText, e.Origin, e.Kind, e.Score })
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var r in rows)
            {
                // First row per source wins (best score, then hit_count, above).
                if (result.ContainsKey(r.SourceText)) continue;
                result[r.SourceText] = new TranslationSuggestion(r.Id, r.TargetText, r.Origin, r.Kind, 1.0, r.Score, 0);
            }
        }

        return result;
    }

    // ── Curation: vote / delete / restore / search ─────────────────────────

    /// <summary>
    /// Records the acting user's vote on an entry. <paramref name="direction"/>
    /// &gt; 0 = up, &lt; 0 = down, 0 = clear. One vote per user (re-voting
    /// replaces it); the entry's denormalised <see cref="TranslationMemoryEntry.Score"/>
    /// is adjusted by the delta so suggestion ranking reflects it immediately.
    /// Returns the new score and the user's resulting vote.
    /// </summary>
    public async Task<VoteResult> VoteAsync(long entryId, int direction, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = RequireUserId();
        var newValue = (short)(direction > 0 ? 1 : direction < 0 ? -1 : 0);
        var now = DateTime.UtcNow;

        // The denormalised Score drives suggestion ranking, so it must equal the
        // sum of the vote rows. The old read-modify-write (entry.Score += delta on
        // a tracked entity) lost increments when two *different* users voted on
        // one entry at once, and a concurrent double-insert from the *same* user
        // tripped the (EntryId, UserId) unique index as a raw 500. Fix both:
        //  * apply the Score change as an atomic UPDATE ... SET score = score + delta,
        //  * keep the vote-row write and the Score change in one transaction, and
        //  * retry when a concurrent same-user insert wins the unique index — the
        //    reload then sees their row and we re-derive the delta.
        // See #478.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            // Existence + tenant scope (the query filter). AsNoTracking: Score is
            // never mutated through the tracked entity — the atomic UPDATE owns it.
            var entryExists = await _db.TranslationMemory.AsNoTracking()
                .AnyAsync(e => e.Id == entryId, ct).ConfigureAwait(false);
            if (!entryExists)
                throw new InvalidOperationException($"Translation memory entry {entryId} not found in this organisation.");

            var vote = await _db.TranslationMemoryVotes
                .FirstOrDefaultAsync(v => v.EntryId == entryId && v.UserId == userId, ct).ConfigureAwait(false);
            var oldValue = vote?.Value ?? 0;
            var delta = newValue - oldValue;

            if (newValue == 0)
            {
                if (vote is not null) _db.TranslationMemoryVotes.Remove(vote);
            }
            else if (vote is null)
            {
                _db.TranslationMemoryVotes.Add(new TranslationMemoryVote
                {
                    OrganizationId = orgId,
                    EntryId = entryId,
                    UserId = userId,
                    Value = newValue,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                vote.Value = newValue;
                vote.UpdatedAt = now;
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // The vote-row insert can violate the (EntryId, UserId) unique
                // index if a concurrent same-user request inserted first.
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (delta != 0)
                {
                    await _db.TranslationMemory
                        .Where(e => e.Id == entryId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(e => e.Score, e => e.Score + delta)
                            .SetProperty(e => e.UpdatedAt, now), ct)
                        .ConfigureAwait(false);
                }

                // Read our own write inside the transaction so the returned score
                // reflects concurrent increments that landed before us.
                var newScore = await _db.TranslationMemory.AsNoTracking()
                    .Where(e => e.Id == entryId)
                    .Select(e => e.Score)
                    .FirstAsync(ct).ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return new VoteResult(entryId, newScore, newValue);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < maxAttempts)
            {
                // A concurrent vote from the same user inserted the row first.
                // Roll back, drop the failed insert from the tracker, and retry:
                // the reload sees their row and we re-derive the delta from it.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> wraps a Postgres unique-violation
    /// (SQLSTATE 23505) — e.g. two votes from the same user racing on the
    /// <c>(entry_id, user_id)</c> unique index.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Soft-deletes an entry (sets <c>deleted_at</c>) so it stops appearing in
    /// suggestions. Recoverable via <see cref="RestoreAsync"/>. Authorisation
    /// (Editor/Admin) is enforced by the caller — the management page and the
    /// MCP tool — matching how the rest of the content-authoring surface gates.
    /// </summary>
    public async Task DeleteAsync(long entryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var entry = await _db.TranslationMemory.FirstOrDefaultAsync(e => e.Id == entryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Translation memory entry {entryId} not found in this organisation.");
        if (entry.DeletedAt is null)
        {
            entry.DeletedAt = DateTime.UtcNow;
            entry.UpdatedAt = entry.DeletedAt.Value;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Translation memory entry {EntryId} soft-deleted.", entryId);
        }
    }

    /// <summary>Un-deletes a previously soft-deleted entry.</summary>
    public async Task RestoreAsync(long entryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var entry = await _db.TranslationMemory.FirstOrDefaultAsync(e => e.Id == entryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Translation memory entry {entryId} not found in this organisation.");
        if (entry.DeletedAt is not null)
        {
            entry.DeletedAt = null;
            entry.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Translation memory entry {EntryId} restored.", entryId);
        }
    }

    /// <summary>
    /// Browse / search the memory for the management page and the MCP tool:
    /// optional text (source or target substring), language / kind / origin
    /// filters, and an include-deleted toggle. Ranked the same way suggestions
    /// are (score, then hit_count, then recency) and paged. Returns the page
    /// plus the total match count.
    /// </summary>
    public async Task<MemorySearchResult> SearchAsync(MemorySearchQuery q, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var userId = _orgContext.CurrentUserId;

        var query = _db.TranslationMemory.AsNoTracking().AsQueryable();
        if (!q.IncludeDeleted) query = query.Where(e => e.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(q.TargetLanguage))
        {
            var t = NormaliseLanguage(q.TargetLanguage);
            query = query.Where(e => e.TargetLanguage == t);
        }
        if (!string.IsNullOrWhiteSpace(q.SourceLanguage))
        {
            var s = NormaliseLanguage(q.SourceLanguage);
            query = query.Where(e => e.SourceLanguage == s);
        }
        if (!string.IsNullOrWhiteSpace(q.Kind)) query = query.Where(e => e.Kind == q.Kind);
        if (!string.IsNullOrWhiteSpace(q.Origin))
        {
            var o = q.Origin.Trim().ToLower();
            query = query.Where(e => e.Origin != null && e.Origin.ToLower().Contains(o));
        }
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var needle = q.Text.Trim().ToLower();
            query = query.Where(e => e.SourceText.ToLower().Contains(needle) || e.TargetText.ToLower().Contains(needle));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var take = Math.Clamp(q.Take, 1, 200);
        var rows = await query
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.HitCount).ThenByDescending(e => e.LastSeenAt)
            .Skip(Math.Max(0, q.Skip)).Take(take)
            .Select(e => new
            {
                e.Id, e.SourceLanguage, e.TargetLanguage, e.SourceText, e.TargetText,
                e.Kind, e.Origin, e.HitCount, e.Score, e.CreatedAt, e.LastSeenAt, e.DeletedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var myVotes = new Dictionary<long, int>();
        if (userId is not null && rows.Count > 0)
        {
            var ids = rows.Select(r => r.Id).ToList();
            myVotes = (await _db.TranslationMemoryVotes.AsNoTracking()
                .Where(v => v.UserId == userId && ids.Contains(v.EntryId))
                .Select(v => new { v.EntryId, v.Value })
                .ToListAsync(ct).ConfigureAwait(false))
                .ToDictionary(v => v.EntryId, v => (int)v.Value);
        }

        var items = rows.Select(r => new MemoryEntryView(
            r.Id, r.SourceLanguage, r.TargetLanguage, r.SourceText, r.TargetText, r.Kind, r.Origin,
            r.HitCount, r.Score, myVotes.GetValueOrDefault(r.Id), r.CreatedAt, r.LastSeenAt, r.DeletedAt != null)).ToList();
        return new MemorySearchResult(items, total);
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

/// <summary>
/// One suggested target for a source string: its memory-entry id (for voting /
/// removal), provenance, match strength (0–1), net vote score, and the acting
/// user's own vote (-1/0/1). <see cref="IsMachineTranslation"/> marks an
/// ephemeral provider-generated suggestion (<c>EntryId</c> 0, no vote/remove
/// controls, rendered with an "MT" badge) so the editor can tell it apart from
/// memory and in-file matches.
/// </summary>
public sealed record TranslationSuggestion(
    long EntryId,
    string TargetText,
    string? Origin,
    string Kind,
    double Similarity,
    int Score,
    int MyVote,
    bool IsMachineTranslation = false);

/// <summary>Result of a vote: the entry's new net score and the user's resulting vote (-1/0/1).</summary>
public sealed record VoteResult(long EntryId, int Score, int MyVote);

/// <summary>Filters for browsing the memory (management page + MCP search).</summary>
public sealed record MemorySearchQuery(
    string? Text = null,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    string? Kind = null,
    string? Origin = null,
    bool IncludeDeleted = false,
    int Skip = 0,
    int Take = 50);

/// <summary>One memory entry as shown in the management list.</summary>
public sealed record MemoryEntryView(
    long Id,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Kind,
    string? Origin,
    int HitCount,
    int Score,
    int MyVote,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    bool IsDeleted);

/// <summary>A page of <see cref="MemoryEntryView"/> plus the total match count.</summary>
public sealed record MemorySearchResult(IReadOnlyList<MemoryEntryView> Items, int Total);
