namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One repository in the connected GitHub organisation that looks like an AL
/// extension and is not part of any solution yet — what the sweep found, so the
/// Solutions page can offer it without calling GitHub on every render.
///
/// <para>A candidate is a <em>finding</em>, not a decision: the row exists while
/// the repository still matches, is dropped again on the first sweep that no
/// longer finds an <c>app.json</c> in it, and is deleted outright once somebody
/// tracks it as a solution. <see cref="IgnoredAt"/> is the one thing a person
/// puts here, and it survives re-discovery so a repository turned down once
/// stays turned down.</para>
///
/// <para>Per organisation, like everything else editable: the EF query filter
/// scopes reads to <see cref="OrganizationId"/>. See
/// <c>.design/github-integration-phase2.md</c>, issue #629.</para>
/// </summary>
public class GitHubRepositoryCandidate
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary><c>owner/name</c>, the form every GitHub route and stored reference uses.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>The repository's page on GitHub, for the link out of the panel.</summary>
    public string HtmlUrl { get; set; } = string.Empty;

    /// <summary>The HTTPS clone URL — what a solution repository stores, and what "already tracked" is matched on.</summary>
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>The branch the probe read, and the one a build would clone.</summary>
    public string DefaultBranch { get; set; } = string.Empty;

    /// <summary>The <c>name</c> from the manifest that was found — what the solution is offered to be called.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>The manifest's app id, so two repositories shipping the same extension are recognisable.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Where the manifest sat: <c>app.json</c> at the root, or <c>&lt;folder&gt;/app.json</c>.</summary>
    public string AppJsonPath { get; set; } = string.Empty;

    /// <summary>When the sweep first saw this repository (UTC).</summary>
    public DateTime DiscoveredAt { get; set; }

    /// <summary>When the sweep last confirmed it still matches (UTC).</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>When somebody turned it down (UTC). Null while it is still offered.</summary>
    public DateTime? IgnoredAt { get; set; }

    /// <summary>Who turned it down, so the decision has a name against it.</summary>
    public int? IgnoredByUserId { get; set; }
}
