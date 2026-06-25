using System.Threading;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Translation;

/// <summary>
/// Owns the suggestion/machine-translation caching state machine that used to
/// live inline in <c>Components/Pages/Translator.razor</c>: a small per-source
/// cache of memory hits (so the next unit's panel is warm on Alt+Enter / arrow
/// nav instead of flashing "Looking up memory…"), a parallel cache of provider
/// (machine-translation) results so re-selecting a unit never re-bills, and the
/// <see cref="SemaphoreSlim"/> that serialises every <see cref="TranslationMemoryService"/>
/// call so a background prefetch can never run concurrently with an on-demand
/// lookup — the EF <c>DbContext</c> behind that service is not safe for
/// overlapping operations.
///
/// Scoped, like the services it wraps: a Blazor Server circuit gets its own
/// instance, so the caches and the gate share the editor's lifetime and the
/// underlying <c>DbContext</c> is the circuit's own. The component keeps all
/// render-state decisions (spinners, <c>StateHasChanged</c>, which unit to
/// prefetch, when MT fires fire-and-forget); this type only owns the cache,
/// the gate, the gated lookup, and the two pure helpers (in-file merge and the
/// MT-trigger decision). See <c>.design/translator/</c> for the feature design.
/// </summary>
public sealed class TranslationSuggestionCoordinator : IDisposable
{
    private readonly TranslationMemoryService _memory;

    // Keyed by exact source text; holds DB hits only — in-file matches (#302)
    // are merged in fresh on every render since session edits change them.
    private readonly Dictionary<string, List<TranslationSuggestion>> _suggCache = new(StringComparer.Ordinal);
    private const int SuggCacheMax = 128;

    // Per-source cache of provider results, mirroring _suggCache.
    private readonly Dictionary<string, TranslationSuggestion> _mtCache = new(StringComparer.Ordinal);

    // Serialises every TranslationMemoryService call so background prefetch
    // (#300) can never run concurrently with an on-demand lookup.
    private readonly SemaphoreSlim _memoryGate = new(1, 1);

    public TranslationSuggestionCoordinator(TranslationMemoryService memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// A translation the user has already made *in the open file*, offered as a
    /// candidate suggestion. The component builds these from its in-memory unit
    /// list; the coordinator only de-duplicates and merges them.
    /// </summary>
    public readonly record struct InFileCandidate(string Target, string Kind);

    // ── Memory cache ──────────────────────────────────────────────────────

    /// <summary>True (with the cached hits) when <paramref name="source"/> was
    /// warmed by a previous visit or a prefetch — no spinner, no DB hit needed.</summary>
    public bool TryGetCached(string source, out List<TranslationSuggestion> memory)
        => _suggCache.TryGetValue(source, out memory!);

    /// <summary>True once <paramref name="source"/> has been looked up (so a
    /// prefetch can skip work already done).</summary>
    public bool IsCached(string source) => _suggCache.ContainsKey(source);

    /// <summary>
    /// Looks up the shared translation memory for <paramref name="source"/>
    /// behind the gate (so a background prefetch and an on-demand lookup never
    /// touch the EF context at the same time) and caches the result. Call this
    /// from the circuit thread only — it writes the cache.
    /// </summary>
    public async Task<List<TranslationSuggestion>> LookupAndCacheAsync(
        string source, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        var memory = await LookupGatedAsync(source, sourceLanguage, targetLanguage, ct);
        Cache(source, memory);
        return memory;
    }

    /// <summary>
    /// The gated memory lookup with no cache write — safe to call from a
    /// background prefetch thread. The caller writes the result back into the
    /// cache via <see cref="Cache"/> on the circuit thread, keeping the cache
    /// dictionary single-threaded (matching the original component behaviour).
    /// </summary>
    public async Task<List<TranslationSuggestion>> LookupGatedAsync(
        string source, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        await _memoryGate.WaitAsync(ct);
        try
        {
            return await _memory.SuggestAsync(source, sourceLanguage, targetLanguage, 9, ct);
        }
        finally
        {
            _memoryGate.Release();
        }
    }

    /// <summary>
    /// Writes a memory lookup into the per-source cache. Must be called on the
    /// circuit thread — the dictionary is not synchronised.
    /// </summary>
    public void Cache(string source, List<TranslationSuggestion> memory)
    {
        // Cheap bound: a single file rarely has more distinct sources than this,
        // and clearing wholesale is fine — it just costs a re-lookup.
        if (_suggCache.Count >= SuggCacheMax) _suggCache.Clear();
        _suggCache[source] = memory;
    }

    // ── Machine-translation cache ─────────────────────────────────────────

    /// <summary>True (with the cached chip) when a provider result for
    /// <paramref name="source"/> is already warm.</summary>
    public bool TryGetCachedMt(string source, out TranslationSuggestion mt)
        => _mtCache.TryGetValue(source, out mt!);

    /// <summary>Remembers a provider result so re-selecting the unit — or an auto
    /// mode firing on every selection — never re-bills the provider.</summary>
    public void CacheMt(string source, TranslationSuggestion mt) => _mtCache[source] = mt;

    // ── Pure helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// #302: surfaces translations the user has already made *in the open file*
    /// as suggestions — identical captions/tooltips are common in a feature, and
    /// fresh translations aren't in the shared memory until export. The caller
    /// passes the candidate (target, kind) pairs already filtered to exact source
    /// matches (excluding the selected unit and empty targets); this de-duplicates
    /// by target, tags them "this file", and prepends them ahead of the DB hits.
    /// <see cref="TranslationSuggestion.EntryId"/> is 0 to mark them un-persisted
    /// (no vote/remove controls).
    /// </summary>
    public List<TranslationSuggestion> Merge(IEnumerable<InFileCandidate> inFileCandidates, List<TranslationSuggestion> memory)
    {
        var inFile = new List<TranslationSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in inFileCandidates)
        {
            if (string.IsNullOrEmpty(c.Target)) continue;
            if (!seen.Add(c.Target)) continue;
            inFile.Add(new TranslationSuggestion(0, c.Target, "this file", c.Kind, 1.0, 0, 0));
        }

        if (inFile.Count == 0) return memory;
        var merged = new List<TranslationSuggestion>(inFile);
        foreach (var m in memory)
            if (seen.Add(m.TargetText)) merged.Add(m);
        return merged;
    }

    /// <summary>
    /// Decides whether to auto-fetch a machine translation for the current
    /// suggestions, honouring the per-tenant trigger: <c>AlwaysAuto</c> always
    /// fetches; <c>AutoWhenNoExactMatch</c> fetches only when no memory/in-file
    /// suggestion is an exact (≥0.99) match; everything else (including
    /// <c>OnDemand</c> and <c>Off</c>) waits. A warm cache hit is the caller's
    /// concern — it's appended without consulting this decision.
    /// </summary>
    public bool ShouldAutoTranslate(MtTrigger trigger, IReadOnlyList<TranslationSuggestion> suggestions)
        => trigger switch
        {
            MtTrigger.AlwaysAuto => true,
            MtTrigger.AutoWhenNoExactMatch => !suggestions.Any(s => s.Similarity >= 0.99),
            _ => false,
        };

    // ── Lifetime ──────────────────────────────────────────────────────────

    /// <summary>Drops both caches. Called when the corpus or the target language
    /// changes, so stale suggestions never leak across files/languages.</summary>
    public void ClearCaches()
    {
        _suggCache.Clear();
        _mtCache.Clear();
    }

    public void Dispose() => _memoryGate.Dispose();
}
