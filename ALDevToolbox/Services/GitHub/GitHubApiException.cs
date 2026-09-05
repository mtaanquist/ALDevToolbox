using System.Net;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// A call to GitHub came back with a failure status. Carries the status and
/// the <c>message</c> field GitHub's error bodies always include, so a page can
/// render the real cause ("a repository with that name already exists") rather
/// than a generic failure. Rate-limit headers are logged by the client, not
/// carried here. See <c>.design/github-integration.md</c>.
/// </summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode statusCode, string message, string? documentationUrl = null)
        : base(message)
    {
        StatusCode = statusCode;
        DocumentationUrl = documentationUrl;
    }

    /// <summary>The HTTP status GitHub answered with.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>GitHub's <c>documentation_url</c>, when the error body carried one.</summary>
    public string? DocumentationUrl { get; }
}

/// <summary>
/// The deployment has no usable GitHub App registration — no app id, no private
/// key, or a key the Data Protection ring can no longer decrypt. Distinct from
/// <see cref="GitHubApiException"/> because nothing was asked of GitHub: the fix
/// is on <c>/site-admin/settings/github</c>, not on GitHub's side.
/// </summary>
public sealed class GitHubAppNotConfiguredException : Exception
{
    public GitHubAppNotConfiguredException()
        : base("GitHub is not set up on this server yet.")
    {
    }
}

/// <summary>
/// A write was refused because the file in the repository is no longer the
/// version the toolbox read. Its own type rather than a
/// <see cref="GitHubApiException"/> because it is not a failure to report: it
/// is the answer that stops the Translator committing over somebody else's
/// work, and the page it reaches renders a way back rather than an error.
/// See <c>.design/github-integration.md</c>, issue #625.
/// </summary>
public sealed class GitHubContentConflictException : Exception
{
    public GitHubContentConflictException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    /// <summary>The repository-relative path whose contents moved on.</summary>
    public string Path { get; }
}
