namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One learned <c>source → target</c> translation pair, accumulated per
/// organisation so a caption translated once surfaces as a suggestion
/// everywhere it's needed again — even when it appears in no Microsoft base
/// app. Rows arrive from two places (see <c>.design/translator/</c> and the
/// Translator plan): the Object Explorer's XLIFF import path
/// (<c>TranslationImportService</c>) and the Translator tool's own completed
/// exports. Suggestions are served by <c>TranslationMemoryService</c> with an
/// exact match first and a <c>pg_trgm</c> trigram fuzzy fallback.
///
/// Distinct targets for the same source are kept on purpose — different
/// extensions legitimately translate the same string differently, and the
/// editor surfaces all candidates for the human to choose. Re-seeing a pair
/// bumps <see cref="HitCount"/> / <see cref="LastSeenAt"/> rather than
/// inserting a duplicate.
/// </summary>
public class TranslationMemoryEntry
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>BCP-47 source locale (normalised <c>xx-XX</c>), usually <c>en-US</c>.</summary>
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>BCP-47 target locale (normalised <c>xx-XX</c>), e.g. <c>da-DK</c>.</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    public string SourceText { get; set; } = string.Empty;
    public string TargetText { get; set; } = string.Empty;

    /// <summary>
    /// MD5 (hex) of <see cref="SourceText"/> / <see cref="TargetText"/>. The
    /// natural key includes the free-text source and target, which can be
    /// longer than a Postgres btree index key tolerates, so the unique index
    /// keys on these bounded hashes instead. Set by
    /// <c>TranslationMemoryService</c> on write; never edited by hand.
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;
    public string TargetHash { get; set; } = string.Empty;

    /// <summary>
    /// Bucketed category (caption / tooltip / label / option / …) from
    /// <c>AlXliffParser.BucketKind</c> — carried so the suggestion UI can show
    /// what kind of string a memory hit came from.
    /// </summary>
    public string Kind { get; set; } = "other";

    /// <summary>Free-text provenance shown next to a suggestion (e.g. module / extension name, or <c>"Translator"</c>).</summary>
    public string? Origin { get; set; }

    /// <summary>How many times this exact pair has been seen — a recency/popularity tie-breaker for ranking.</summary>
    public int HitCount { get; set; }

    /// <summary>
    /// Denormalised net vote score (sum of up/down votes from
    /// <see cref="TranslationMemoryVote"/>). The primary suggestion-ranking key
    /// — a translator's thumbs-up floats a good pair above a more-frequent but
    /// worse one; thumbs-down sinks it. Kept on the row so ranking is a plain
    /// ORDER BY rather than a per-query aggregate.
    /// </summary>
    public int Score { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>Null = active. Soft-delete, per the domain conventions.</summary>
    public DateTime? DeletedAt { get; set; }
}
