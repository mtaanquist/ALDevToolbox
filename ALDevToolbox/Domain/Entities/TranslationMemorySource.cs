namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One translation file in one of the organisation's repositories, and the
/// version of it the translation memory has already learned from.
///
/// <para>This table is what makes the nightly ingest cheap. A repository's file
/// list costs one call; reading a file costs another, and parsing it costs
/// more still - so the ingest reads a file only when its
/// <see cref="BlobSha"/> differs from the one recorded here. A run over an
/// organisation whose translations have not moved therefore costs one call per
/// repository and nothing else.</para>
///
/// <para>A row whose file is no longer in the repository's tree is deleted on
/// the next sweep, so the table says what is there now rather than what was
/// once seen. The memory pairs themselves stay: a translation is not wrong
/// because the file it came from was renamed.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c> (#631).</para>
/// </summary>
public class TranslationMemorySource
{
    public long Id { get; set; }

    /// <summary>Owning organisation. The EF query filter scopes every read to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The repository, as <c>owner/name</c>.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Where the file lives in that repository, from the root.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The Git blob sha of the version already learned from. The ingest
    /// compares the sha in the tree listing against this one and skips the file
    /// when they match.
    /// </summary>
    public string BlobSha { get; set; } = string.Empty;

    /// <summary>When the file was last read and learned from.</summary>
    public DateTime LastIngestedAt { get; set; }

    /// <summary>How many translated pairs that read offered the memory.</summary>
    public int UnitCount { get; set; }
}
