using NpgsqlTypes;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One knowledge article mirrored from Microsoft's BCQuality repository
/// (https://github.com/microsoft/BCQuality, MIT). See
/// <c>.design/bcquality.md</c> for the ingest and refresh contract.
///
/// <para>
/// Deliberately carries no <c>OrganizationId</c> and no EF query filter: this
/// is public Microsoft content, identical for every tenant, so there is
/// nothing to scope and therefore nothing for a read path to escape. Do not
/// add <c>IgnoreQueryFilters()</c> anywhere near it — there is no filter.
/// (Same reasoning as <c>OeFileContent</c>; see the note in
/// <c>AppDbContext.OnModelCreating</c>.)
/// </para>
/// </summary>
public class BcQualityArticle
{
    public int Id { get; set; }

    /// <summary>
    /// The repo-relative path of the markdown file, e.g.
    /// <c>microsoft/knowledge/ui/no-nested-grids.md</c>. This is BCQuality's
    /// own citation key (its READ contract requires consumers to cite an
    /// article by path, never by line number), so it doubles as our stable
    /// upsert key and as the id the MCP tools take and hand back.
    /// </summary>
    public string ArticleKey { get; set; } = string.Empty;

    /// <summary>The authority layer from the path: <c>microsoft</c>, <c>community</c>, or <c>custom</c>. Drives BCQuality's precedence rule.</summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>The frontmatter <c>domain</c> tag (<c>performance</c>, <c>ui</c>, ...). Open enumeration upstream — never validated against a closed list.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The file name without its <c>.md</c> extension. Also the prefix that ties sibling sample files to this article.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The article's first level-1 heading, falling back to the slug when the file has none.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>First paragraph of the required <c>## Description</c> section. The retrieval hint agents read before deciding to open the article.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The whole markdown body with the YAML frontmatter block stripped.</summary>
    public string Content { get; set; } = string.Empty;

    public List<string> Keywords { get; set; } = new();

    /// <summary>
    /// <see cref="Keywords"/> joined by spaces. It exists only to feed the
    /// generated <see cref="SearchVector"/>: Postgres requires every function
    /// in a generated column to be IMMUTABLE, and <c>array_to_string</c> is
    /// only STABLE (it depends on the element type's output function), so the
    /// index cannot read the array directly. Written by the ingest alongside
    /// <see cref="Keywords"/>; never read by application code.
    /// </summary>
    public string KeywordsText { get; set; } = string.Empty;

    public List<string> Technologies { get; set; } = new();
    public List<string> Countries { get; set; } = new();
    public List<string> ApplicationAreas { get; set; } = new();

    /// <summary>The <c>bc-version</c> frontmatter value verbatim (e.g. <c>[all]</c>, <c>[23..]</c>), kept for display and for diagnosing a parse we got wrong.</summary>
    public string BcVersionRaw { get; set; } = string.Empty;

    /// <summary>True for the <c>[all]</c> sentinel — the guidance applies to every BC version.</summary>
    public bool BcVersionAll { get; set; }

    /// <summary>
    /// Every explicitly named BC major version, with closed ranges
    /// (<c>[26..28]</c>) already expanded, because BCQuality's matching rules
    /// require expansion before comparison.
    /// </summary>
    public List<int> BcVersions { get; set; } = new();

    /// <summary>
    /// Lower bound of an open-ended range (<c>[26..]</c> gives 26), which is
    /// not enumerable and instead matches any target version at or above it.
    /// Null when the frontmatter carries no open-ended range.
    /// </summary>
    public int? BcVersionFrom { get; set; }

    /// <summary>
    /// SHA-256 over the article text and every sample file that ships with it.
    /// A re-ingest that finds the same hash leaves the row (and its samples)
    /// alone, so a daily refresh over an unchanged repo writes nothing.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Weighted full-text index over title (A), keywords and domain (B),
    /// summary (C), and body (D). A Postgres stored generated column, so it is
    /// maintained by the database and never assigned in C#.
    /// </summary>
    public NpgsqlTsVector? SearchVector { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<BcQualityArticleSample> Samples { get; set; } = new();
}

/// <summary>
/// A sample file that ships beside an article. BCQuality forbids fenced code
/// blocks inside a knowledge article: code lives in siblings named
/// <c>&lt;slug&gt;.&lt;kind&gt;.&lt;ext&gt;</c> (kinds <c>good</c> and
/// <c>bad</c> today, extended by layers). Without these an article that says
/// "see sample: <c>no-nested-grids.bad.al</c>" would be a dead end for an
/// agent with no clone.
/// </summary>
public class BcQualityArticleSample
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public BcQualityArticle? Article { get; set; }

    /// <summary>The <c>&lt;kind&gt;</c> segment of the file name — <c>good</c>, <c>bad</c>, or whatever a layer introduces. Stored verbatim; unknown kinds are carried, not rejected.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The sample's file name, e.g. <c>no-nested-grids.bad.al</c>. Cited by the MCP tools as the article's folder plus this name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The file extension without the dot (<c>al</c>, <c>ps1</c>, <c>js</c>, ...), which is also the sample's language.</summary>
    public string Language { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Provenance for the single mirror of the BCQuality repository: which commit
/// the rows in <c>bcquality_articles</c> came from and how the last refresh
/// went. One row, id 1 — the table is a state record, not a history log.
/// </summary>
public class BcQualityIngestState
{
    /// <summary>Always <see cref="SingletonId"/>. The row is upserted, never inserted twice.</summary>
    public int Id { get; set; }

    public const int SingletonId = 1;

    /// <summary>Full SHA of the BCQuality commit the current articles were read from. Empty until the first successful ingest.</summary>
    public string CommitSha { get; set; } = string.Empty;

    /// <summary>Committer date of <see cref="CommitSha"/>, so operators can see how fresh upstream itself is.</summary>
    public DateTime? CommitDate { get; set; }

    /// <summary>When the last successful ingest finished. Drives the daily refresh cadence.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>When the last refresh attempt ran, successful or not.</summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>Number of articles in the table after the last successful ingest.</summary>
    public int ArticleCount { get; set; }

    /// <summary>Empty after a clean run; otherwise why the last attempt failed, so a failure is visible without trawling logs.</summary>
    public string LastError { get; set; } = string.Empty;
}
