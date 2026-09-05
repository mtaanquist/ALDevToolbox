using System.IO.Compression;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What "Add to repository" produced, for the success state to render.</summary>
/// <param name="Repository">The repository the pull request was opened on.</param>
/// <param name="PullRequest">The pull request itself - its number and the link the user needs next.</param>
/// <param name="FolderName">The extension folder the commit added.</param>
/// <param name="FileCount">How many files the commit carried.</param>
/// <param name="ArchiveFileName">The name the same extension would download under.</param>
/// <param name="Archive">
/// The very bytes that were committed, as the ZIP. Carried out rather than
/// thrown away because the MCP tool hands the caller both the pull request and
/// the files, and generating a second time would produce different extension
/// GUIDs - a download that quietly disagreed with the pull request beside it.
/// The web page ignores it.
/// </param>
public sealed record GitHubExtensionDelivery(
    GitHubRepositorySummary Repository,
    GitHubPullRequest PullRequest,
    string FolderName,
    int FileCount,
    string ArchiveFileName,
    byte[] Archive);

/// <summary>
/// Adds a freshly generated extension to an existing repository as a pull
/// request (issue #623).
///
/// <para><strong>Generation is unchanged.</strong> The files are the ones the
/// ZIP is built from - the same in-memory archive, read back entry by entry -
/// so the download and the pull request can never drift apart. Nothing is
/// queued: this runs on the request thread inside the button's own loading
/// state, like every other GitHub call in this milestone.</para>
///
/// <para><strong>The commit is the user's, and never touches the default
/// branch.</strong> The write goes out on the acting user's linked token, so
/// GitHub enforces their own permissions natively and the pull request is
/// genuinely theirs rather than a bot's; and it lands on a branch of its own
/// even when the default branch is unprotected, because "add something to an
/// existing repository" is a proposal, not a fait accompli. Which repositories
/// may be reached at all is <see cref="GitHubRepositoryService.ResolveAsync"/>'s
/// decision, so every caller - page or MCP tool - inherits one gate.</para>
///
/// <para>See <c>.design/github-integration.md</c>.</para>
/// </summary>
public sealed class GitHubExtensionDeliveryService
{
    /// <summary>Branch names are <c>aldt/add-&lt;folder&gt;</c>, per the design doc.</summary>
    private const string BranchPrefix = "aldt/add-";

    /// <summary>
    /// How many times a taken branch name is stepped before giving up. A second
    /// attempt at the same extension is normal (the first pull request is still
    /// open); ten of them means something else is going on and the user should
    /// hear about it rather than collect branches.
    /// </summary>
    private const int MaxBranchAttempts = 10;

    /// <summary>The file that marks a folder as an existing AL extension.</summary>
    private const string ExtensionMarkerFile = "app.json";

    private readonly GenerationService _generation;
    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubAccessService _access;
    private readonly GitHubAppClient _github;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubExtensionDeliveryService> _logger;

    public GitHubExtensionDeliveryService(
        GenerationService generation,
        GitHubRepositoryService repositories,
        GitHubAccessService access,
        GitHubAppClient github,
        IOrganizationContext orgContext,
        ILogger<GitHubExtensionDeliveryService> logger)
    {
        _generation = generation;
        _repositories = repositories;
        _access = access;
        _github = github;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; extension delivery called outside an authenticated request.");

    /// <summary>
    /// Generates <paramref name="plan"/> and opens a pull request adding it to
    /// <paramref name="repoFullName"/>.
    ///
    /// <para>Every refusal is a field-keyed <see cref="PlanValidationException"/>
    /// on <c>GitHubRepository</c>, so a page renders it beside the picker and an
    /// MCP tool reports it as a validation failure - one message, both callers.
    /// The plan's own rules are checked by the generator before anything is
    /// asked of GitHub, and come back keyed to their own fields as usual.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The plan is invalid, or the repository cannot be written to.</exception>
    /// <exception cref="GitHubApiException">GitHub refused one of the calls that make up the commit.</exception>
    public async Task<GitHubExtensionDelivery> AddExtensionAsync(
        StandaloneExtensionPlan plan,
        SiblingWorkspaceContext? sibling,
        string repoFullName,
        CancellationToken ct = default)
    {
        var userId = RequireUserId();

        // The plan's own rules first: an extension nobody could generate is not
        // worth a round trip to GitHub, and the errors it raises are keyed to
        // the fields that caused them rather than to the repository.
        var planErrors = await _generation.ValidateExtensionAsync(plan, ct);
        if (planErrors.Count > 0) throw new PlanValidationException(planErrors.ToDictionary(e => e.Key, e => e.Value));

        // Why-not first, so the answer names the thing the caller can change.
        // Resolving a repository would refuse all four of these identically,
        // and "that is not a repository we can offer you" is a poor way to say
        // "you have not connected your GitHub account".
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) throw Refuse(access.Readiness switch
        {
            GitHubRepositoryReadiness.NotConfigured =>
                "GitHub is not set up on this server yet, so nothing can be added to a repository. "
                + "Ask whoever runs AL Dev Toolbox to set it up.",
            GitHubRepositoryReadiness.NotConnected =>
                "Your organisation has not connected a GitHub organisation yet, so there is nowhere to "
                + "add this. An administrator connects one under Administration -> Repositories.",
            GitHubRepositoryReadiness.LinkNeedsRepair =>
                "Your GitHub account is no longer connected to the toolbox. Connect it again on your "
                + "account page under Repository access, then try this again.",
            _ =>
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The extension is added in your name, so the toolbox needs your GitHub account to do it.",
        });

        var repo = await _repositories.ResolveAsync(repoFullName, ct)
            ?? throw Refuse(
                "That repository is not one the toolbox can offer you. Pick one from the list, "
                + "or ask an owner of your GitHub organisation to give you access to it.");

        var token = await _access.ResolveUserTokenAsync(userId, ct)
            ?? throw Refuse(
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The extension is added in your name, so the toolbox needs your GitHub account to do it.");

        var baseSha = await _github.GetBranchHeadShaAsync(token, repo.Owner, repo.Name, repo.DefaultBranch, ct)
            ?? throw Refuse(
                $"'{repo.FullName}' has no commits on {repo.DefaultBranch} yet, so there is nothing to open a "
                + "pull request against. Push something to it first, then come back.");

        var folderName = GenerationNaming.StripWhitespace(plan.ExtensionName);
        var existing = await _github.GetFileAsync(
            token, repo.Owner, repo.Name, $"{folderName}/{ExtensionMarkerFile}", repo.DefaultBranch, ct);
        if (existing is not null)
        {
            throw Refuse(
                $"'{repo.FullName}' already has an extension in a folder called {folderName}. "
                + "Give this one a different name, or add to the existing extension in your editor instead.");
        }

        var (files, archiveName, archiveBytes) = await BuildFilesAsync(plan, sibling, ct);

        var blobs = new List<(string Path, string BlobSha)>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            blobs.Add((file.Path, await _github.CreateBlobAsync(token, repo.Owner, repo.Name, file.Content, ct)));
        }

        var baseTree = await _github.GetCommitTreeShaAsync(token, repo.Owner, repo.Name, baseSha, ct);
        var tree = await _github.CreateTreeAsync(token, repo.Owner, repo.Name, baseTree, blobs, ct);
        var commit = await _github.CreateCommitAsync(
            token, repo.Owner, repo.Name,
            $"Add the {plan.ExtensionName} extension", tree, baseSha, ct: ct);

        var branch = await CreateBranchAsync(token, repo, folderName, commit, ct);
        var pullRequest = await _github.CreatePullRequestAsync(
            token, repo.Owner, repo.Name,
            title: $"Add the {plan.ExtensionName} extension",
            head: branch,
            baseBranch: repo.DefaultBranch,
            body: BuildBody(plan, sibling, folderName, files.Count),
            ct);

        _logger.LogInformation(
            "User {UserId} added extension '{Extension}' to {RepoFullName} on branch {Branch} as pull request #{PullRequestNumber} ({FileCount} files).",
            userId, plan.ExtensionName, repo.FullName, branch, pullRequest.Number, files.Count);

        return new GitHubExtensionDelivery(
            repo, pullRequest, folderName, files.Count, archiveName, archiveBytes);
    }

    /// <summary>
    /// The generated file set, read straight back out of the archive the
    /// download would have handed over. Reading the ZIP rather than teaching
    /// the generator a second output shape is deliberate: there is then exactly
    /// one description of what a generated extension contains.
    ///
    /// <para>The workspace-root files a template opts into (a .gitignore, a
    /// README stub, the shared ruleset) are left out. A standalone download
    /// carries them because the extension folder <em>is</em> the root of what
    /// the user unzips; a repository already has its own root, and committing a
    /// second copy one level down would be noise at best.</para>
    /// </summary>
    private async Task<(List<GitHubCommitFile> Files, string ArchiveName, byte[] Archive)> BuildFilesAsync(
        StandaloneExtensionPlan plan, SiblingWorkspaceContext? sibling, CancellationToken ct)
    {
        var archive = await _generation.GenerateExtensionAsync(
            plan, sibling, includeWorkspaceRootFiles: false, ct);
        await using var stream = archive.Stream;
        stream.Position = 0;

        var files = new List<GitHubCommitFile>();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                // Directory entries have an empty name; the generator writes
                // none, but a ZIP reader should not assume that.
                if (string.IsNullOrEmpty(entry.Name)) continue;
                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                await entryStream.CopyToAsync(buffer, ct);
                files.Add(new GitHubCommitFile(entry.FullName, buffer.ToArray()));
            }
        }
        return (files, archive.FileName, stream.ToArray());
    }

    /// <summary>
    /// Points a new branch at the commit, stepping the name when it is taken.
    /// A previous attempt's branch is never moved: its pull request may already
    /// be under review.
    /// </summary>
    private async Task<string> CreateBranchAsync(
        string token, GitHubRepositorySummary repo, string folderName, string commitSha, CancellationToken ct)
    {
        var baseName = BranchPrefix + Slug(folderName);
        for (var attempt = 1; attempt <= MaxBranchAttempts; attempt++)
        {
            var branch = attempt == 1 ? baseName : $"{baseName}-{attempt}";
            if (await _github.CreateBranchAsync(token, repo.Owner, repo.Name, branch, commitSha, ct))
            {
                return branch;
            }
            _logger.LogInformation(
                "Branch {Branch} already exists on {RepoFullName}; trying the next name.", branch, repo.FullName);
        }

        throw Refuse(
            $"'{repo.FullName}' already has branches named {baseName} through {baseName}-{MaxBranchAttempts}. "
            + "Tidy those up on GitHub, or give this extension a different name.");
    }

    /// <summary>The pull request's description: what it adds, and where it came from.</summary>
    private static string BuildBody(
        StandaloneExtensionPlan plan, SiblingWorkspaceContext? sibling, string folderName, int fileCount)
    {
        var lines = new List<string>
        {
            $"Adds the **{plan.ExtensionName}** extension in `{folderName}/` ({fileCount} files), "
                + $"object IDs {plan.IdRangeFrom}-{plan.IdRangeTo}.",
        };
        if (!string.IsNullOrWhiteSpace(plan.Brief))
        {
            lines.Add(plan.Brief.Trim());
        }
        if (sibling is not null)
        {
            lines.Add($"The `{GenerationNaming.StripWhitespace(sibling.WorkspaceName)}.code-workspace` file is "
                + "updated so the new folder opens with the rest of the workspace.");
        }
        lines.Add("Generated by AL Dev Toolbox.");
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// A branch-safe form of the folder name. The extension name is already
    /// letters and digits by the time it gets here, so this is a belt on top of
    /// the generator's rule rather than the rule itself.
    /// </summary>
    private static string Slug(string folderName)
    {
        var slug = new string(folderName
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray())
            .Trim('-', '.');
        return slug.Length == 0 ? "extension" : slug;
    }

    private static PlanValidationException Refuse(string message) =>
        new(new Dictionary<string, string> { ["GitHubRepository"] = message });
}
