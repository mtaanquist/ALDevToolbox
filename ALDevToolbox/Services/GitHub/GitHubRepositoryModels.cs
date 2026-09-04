namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One repository, as much of it as any GitHub feature in the toolbox needs.
///
/// <para>The same shape comes back from three different GitHub routes - the
/// installation's repository list, a single repository read, and a freshly
/// created one - so it is written once here rather than three times at the
/// call sites.</para>
/// </summary>
/// <param name="FullName"><c>owner/name</c>, the form every route and every
/// stored reference uses.</param>
/// <param name="Owner">The account the repository belongs to.</param>
/// <param name="Name">The repository name on its own.</param>
/// <param name="DefaultBranch">
/// The branch a pull request targets. Never written to directly - see
/// <see cref="GitHubExtensionDeliveryService"/>.
/// </param>
/// <param name="IsPrivate">Rendered as a hint in the picker, so a consultant
/// can tell two similarly named repositories apart.</param>
/// <param name="Description">GitHub's one-line description, when it has one.</param>
/// <param name="HtmlUrl">The repository's page, for links out.</param>
/// <param name="CloneUrl">The HTTPS clone URL, which issue #624 fills a pipeline's field from.</param>
public sealed record GitHubRepositorySummary(
    string FullName,
    string Owner,
    string Name,
    string DefaultBranch,
    bool IsPrivate,
    string? Description,
    string HtmlUrl,
    string CloneUrl);

/// <summary>
/// One file read through the Contents API: its decoded text and the blob
/// <c>sha</c> that a later write has to quote to prove it changed the version
/// it read (issue #625 leans on the sha; #623 only reads).
/// </summary>
public sealed record GitHubFileContent(string Path, string Text, string Sha);

/// <summary>One file to commit: its repository-relative path and its bytes.</summary>
public sealed record GitHubCommitFile(string Path, byte[] Content);

/// <summary>A pull request the toolbox opened, as the success state needs it.</summary>
public sealed record GitHubPullRequest(int Number, string HtmlUrl, string HeadBranch);
