using System.Net;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// The organisation-membership half of <see cref="GitHubAppClient"/>, asked as the
/// app rather than as a person (issue #627, member forks).
///
/// <para>The compile gate builds a pull request from a fork only when its author
/// is a member of the connected organisation. GitHub says so once, in the
/// delivery's <c>author_association</c>, and that delivery is HMAC-verified - but
/// it describes the moment the pull request was opened, and a later push to the
/// same pull request re-uses it. Somebody who has left the organisation in between
/// would still be carrying <c>MEMBER</c>. So the question is asked again here, at
/// build time, with the installation token; the answer costs one call and decides
/// whether anything is cloned.</para>
/// </summary>
public sealed partial class GitHubAppClient
{
    /// <summary>
    /// Whether <paramref name="username"/> is a member of <paramref name="org"/>,
    /// asked with an installation token rather than with a person's own.
    ///
    /// <para>Same endpoint as <see cref="UserIsOrgMemberAsync"/> and the same
    /// three answers - 204 for yes, 404 for no, 302 when the caller is not itself
    /// in the organisation - but a different credential, which is the whole point:
    /// a webhook build has no user behind it, so the only thing that can ask is
    /// the installation. The client does not follow redirects (see
    /// <c>GitHubRegistration</c>), so the 302 stays visible; anything that is not
    /// a 204 is read as no.</para>
    ///
    /// <para>This throws when GitHub could not be reached at all, because "we do
    /// not know" and "no" are different answers and only the caller can decide
    /// what to do with the first. The compile gate treats both as a refusal - see
    /// <c>.design/github-integration-phase2.md</c> (#627).</para>
    /// </summary>
    /// <exception cref="HttpRequestException">GitHub could not be reached.</exception>
    public async Task<bool> InstallationSeesOrgMemberAsync(
        string installationToken, string org, string username, CancellationToken ct = default)
    {
        var status = await ProbeAsync(
            HttpMethod.Get,
            $"orgs/{Uri.EscapeDataString(org)}/members/{Uri.EscapeDataString(username)}",
            installationToken, ct);
        var isMember = status == HttpStatusCode.NoContent;
        _logger.LogInformation(
            "GitHub answered {Status} asking whether {Username} is a member of {Org}; treated as {Verdict}.",
            (int)status, username, org, isMember ? "member" : "not a member");
        return isMember;
    }
}
