namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One file the organisation wants in every repository the toolbox creates
/// (issue #628) - a workflow, a CODEOWNERS, a pull-request template.
///
/// <para>Deliberately not an <see cref="OrganizationFile"/>: those are the
/// always-included files the *generator* emits into a workspace, opted into per
/// template and present in the ZIP. These never appear in a ZIP and ignore the
/// template entirely; they are committed into the repository after the generated
/// files, so a standard at a path the template also produced replaces it - the
/// organisation's standard wins. See
/// <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public class GitHubRepositoryStandardFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// Repository-relative path with forward slashes
    /// (<c>.github/workflows/build.yml</c>, <c>CODEOWNERS</c>). No leading
    /// slash and no <c>..</c> segments. Unique per organisation.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Raw file body, committed verbatim. No mustache substitution.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Position in the admin's list. Only affects how the editor shows them.</summary>
    public int Ordering { get; set; }

    public DateTime UpdatedAt { get; set; }
}
