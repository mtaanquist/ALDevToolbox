namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One user's up/down vote on a <see cref="TranslationMemoryEntry"/>. Votes are
/// per-user (unique on <c>(entry, user)</c>) so a translator can change or clear
/// their vote without double-counting, and the UI can show "you voted up". The
/// aggregate is denormalised onto <see cref="TranslationMemoryEntry.Score"/> for
/// ranking. See the Translator plan / curation feature.
/// </summary>
public class TranslationMemoryVote
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public long EntryId { get; set; }
    public TranslationMemoryEntry? Entry { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>+1 (up) or -1 (down). A cleared vote deletes the row rather than storing 0.</summary>
    public short Value { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
