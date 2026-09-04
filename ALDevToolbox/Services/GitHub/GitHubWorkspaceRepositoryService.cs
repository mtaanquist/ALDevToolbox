using System.IO.Compression;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What "Create repository" produced, for the success state to render.</summary>
/// <param name="Repository">The repository that now exists, including the link the user needs next.</param>
/// <param name="FileCount">How many files the first commit carried.</param>
/// <param name="ArchiveFileName">The name the same workspace would download under.</param>
/// <param name="Archive">
/// The very bytes that were committed, as the ZIP. Carried out rather than
/// thrown away because the MCP tool hands the caller both the repository and
/// the files, and generating a second time would mint different extension
/// GUIDs - a download that quietly disagreed with the repository beside it.
/// The web page ignores it.
/// </param>
public sealed record GitHubWorkspaceRepository(
    GitHubRepositorySummary Repository,
    int FileCount,
    string ArchiveFileName,
    byte[] Archive);

/// <summary>
/// Creates a repository in the connected GitHub organisation and puts a freshly
/// generated workspace in it, in one commit (issue #622).
///
/// <para><strong>Generation is unchanged.</strong> The files are the ones the
/// ZIP is built from - the same in-memory archive, read back entry by entry -
/// so the download and the repository can never drift apart. Nothing is
/// queued: this runs on the request thread inside the button's own loading
/// state.</para>
///
/// <para><strong>The organisation acts, not the person.</strong> Both calls go
/// out on the installation token, which is the credential split the design doc
/// settles: creating a repository is an act of the organisation, and no
/// individual should need <c>admin:org</c> on their own account for the toolbox
/// to work. The first commit rides the same token deliberately - the repository
/// is seconds old and was made by the app, so the person who asked for it may
/// have no permissions on it yet, and asking with their token would fail for a
/// reason they could do nothing about. What their own account <em>is</em> used
/// for is the gate: GitHub is asked whether they are a member of the
/// organisation before anything is created, and the commit is credited to them
/// so the history says who asked. This is the mirror image of
/// <see cref="GitHubExtensionDeliveryService"/>, where a write into somebody's
/// <em>existing</em> repository goes out as the user and GitHub enforces their
/// permissions natively.</para>
///
/// <para>See <c>.design/github-integration.md</c>.</para>
/// </summary>
public sealed class GitHubWorkspaceRepositoryService
{
    /// <summary>Error key for problems with the repository name the user typed.</summary>
    public const string NameField = "GitHubRepositoryName";

    /// <summary>Error key for problems with GitHub itself rather than with one field.</summary>
    public const string RepositoryField = "GitHubRepository";

    /// <summary>
    /// GitHub's own rule for a repository name: letters, digits, and the three
    /// punctuation marks it keeps, up to 100 characters, and never <c>.</c> or
    /// <c>..</c> (which are directory names, not repositories). Anything else
    /// GitHub silently rewrites, and a repository whose name is not the one the
    /// user typed is worse than a refusal.
    ///
    /// <para>The <c>pattern</c> attribute on the New Workspace field is this
    /// same expression, so the browser refuses exactly what the server would -
    /// see CLAUDE.md on mirroring server rules in the form.</para>
    /// </summary>
    public const string NamePattern = @"^(?!\.{1,2}$)[A-Za-z0-9._-]{1,100}$";

    private static readonly Regex NameRegex = new(NamePattern, RegexOptions.Compiled);

    private readonly GenerationService _generation;
    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubConnectionService _connection;
    private readonly GitHubAccessService _access;
    private readonly GitHubAppClient _github;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubWorkspaceRepositoryService> _logger;

    public GitHubWorkspaceRepositoryService(
        GenerationService generation,
        GitHubRepositoryService repositories,
        GitHubConnectionService connection,
        GitHubAccessService access,
        GitHubAppClient github,
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<GitHubWorkspaceRepositoryService> logger)
    {
        _generation = generation;
        _repositories = repositories;
        _connection = connection;
        _access = access;
        _github = github;
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; repository creation called outside an authenticated request.");

    /// <summary>
    /// The repository name a workspace called <paramref name="workspaceName"/>
    /// suggests: its words joined with hyphens, which is both a legal GitHub
    /// name and the shape people actually name repositories. Only a suggestion -
    /// the user can type anything the rule above allows.
    /// </summary>
    public static string SuggestName(string? workspaceName)
    {
        var kept = new string((workspaceName ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray());
        // Collapse the runs a multi-word name leaves behind, then trim the ends:
        // "CRONUS  Customer A/S" would otherwise suggest "CRONUS--Customer-A-S-".
        while (kept.Contains("--", StringComparison.Ordinal))
        {
            kept = kept.Replace("--", "-", StringComparison.Ordinal);
        }
        kept = kept.Trim('-', '.');
        return kept.Length > 100 ? kept[..100].TrimEnd('-', '.') : kept;
    }

    /// <summary>
    /// Generates <paramref name="plan"/> and creates
    /// <paramref name="repositoryName"/> in the connected GitHub organisation
    /// with those files as its first commit.
    ///
    /// <para>Nothing is created until every refusal has been ruled out, so a
    /// plan the generator rejects, a name GitHub would rewrite, or a user who
    /// is not in the organisation never leaves an empty repository behind.
    /// Every refusal is a field-keyed <see cref="PlanValidationException"/> -
    /// on <see cref="NameField"/> when the user can fix it by typing something
    /// else, on <see cref="RepositoryField"/> when they cannot - so a page
    /// renders it beside the right control and an MCP tool reports it as a
    /// validation failure.</para>
    ///
    /// <para>The organisation is never a parameter: it is the one this
    /// toolbox organisation connected, so a caller naming a repository cannot
    /// aim it anywhere else.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">The plan, the name, or the caller's access is not good enough.</exception>
    /// <exception cref="GitHubApiException">GitHub refused one of the calls that make up the commit.</exception>
    public async Task<GitHubWorkspaceRepository> CreateAsync(
        ProjectPlan plan, string repositoryName, bool isPrivate, CancellationToken ct = default)
    {
        var userId = RequireUserId();

        // The plan's own rules first: a workspace nobody could generate is not
        // worth a round trip to GitHub, and its errors are keyed to the fields
        // that caused them rather than to the repository.
        var planErrors = await _generation.ValidateWorkspaceAsync(plan, ct);
        if (planErrors.Count > 0) throw new PlanValidationException(planErrors.ToDictionary(e => e.Key, e => e.Value));

        var name = (repositoryName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw Refuse(NameField, "Give the repository a name.");
        }
        if (!NameRegex.IsMatch(name))
        {
            throw Refuse(NameField,
                "GitHub repository names can only contain letters, digits, hyphens, underscores and full "
                + "stops, and can be at most 100 characters long.");
        }

        // Why-not first, so the answer names the thing the caller can change.
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) throw Refuse(RepositoryField, access.Readiness switch
        {
            GitHubRepositoryReadiness.NotConfigured =>
                "GitHub is not set up on this server yet, so no repository can be created. "
                + "Ask whoever runs AL Dev Toolbox to set it up.",
            GitHubRepositoryReadiness.NotConnected =>
                "Your organisation has not connected a GitHub organisation yet, so there is nowhere to "
                + "create this. An administrator connects one under Administration -> Repositories.",
            GitHubRepositoryReadiness.LinkNeedsRepair =>
                "Your GitHub account is no longer connected to the toolbox. Connect it again on your "
                + "account page under Repository access, then try this again.",
            _ =>
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The toolbox checks that you are in the GitHub organisation before it creates anything there.",
        });

        var connection = await _connection.GetStatusAsync(ct);
        var orgLogin = connection.OrgLogin!;
        var installationId = connection.InstallationId!.Value;

        if (!await _access.IsOrgMemberAsync(userId, ct))
        {
            throw Refuse(RepositoryField,
                $"GitHub does not list you as a member of {orgLogin}, so the toolbox will not create a "
                + "repository there for you. Ask an owner of that organisation to add you, then try again.");
        }

        // The connection records what the installation was granted, so the
        // hopeless case can be refused before a round trip. Only when GitHub
        // actually reported the permissions - an older connection recorded
        // none, and refusing on a blank is refusing on no evidence.
        if (connection.Permissions.Count > 0 && !connection.CanCreateRepositories)
        {
            throw Refuse(RepositoryField, NotPermittedMessage(orgLogin));
        }

        // Generate before creating anything: a generator failure now costs
        // nothing, while the same failure after the repository exists would
        // leave an empty one behind with no way to retry into it.
        var (files, archiveName, archiveBytes) = await BuildFilesAsync(plan, ct);

        var token = await _github.GetInstallationTokenAsync(installationId, ct);
        var created = await _github.CreateOrganizationRepositoryAsync(
            token, orgLogin, name, isPrivate, string.IsNullOrWhiteSpace(plan.Brief) ? null : plan.Brief.Trim(), ct);
        var repository = created.Outcome switch
        {
            GitHubRepositoryCreationOutcome.Created => created.Repository!,
            GitHubRepositoryCreationOutcome.NameTaken => throw Refuse(NameField,
                $"{orgLogin} already has a repository called {name}. Pick a different name."),
            _ => throw Refuse(RepositoryField, NotPermittedMessage(orgLogin)),
        };

        await CommitAsync(token, repository, plan, files, userId, ct);
        await RecordAsync(repository, plan, files.Count, ct);

        _logger.LogInformation(
            "User {UserId} created the repository {RepoFullName} from workspace '{Workspace}' "
            + "(template '{Template}', {FileCount} files, {Visibility}).",
            userId, repository.FullName, plan.WorkspaceName, plan.TemplateKey, files.Count,
            isPrivate ? "private" : "public");

        return new GitHubWorkspaceRepository(repository, files.Count, archiveName, archiveBytes);
    }

    /// <summary>
    /// The generated file set, read straight back out of the archive the
    /// download would have handed over. Reading the ZIP rather than teaching
    /// the generator a second output shape is deliberate: there is then exactly
    /// one description of what a generated workspace contains -
    /// <c>workspace.aldt.toml</c> among them, which is what lets the New
    /// Extension page fill itself in from this repository later.
    ///
    /// <para>The archive nests everything under the workspace folder, because
    /// that folder is what a user unzips. A repository <em>is</em> that folder,
    /// so the prefix comes off: <c>CRONUSCustomer/app.json</c> is committed as
    /// <c>app.json</c>.</para>
    /// </summary>
    private async Task<(List<GitHubCommitFile> Files, string ArchiveName, byte[] Archive)> BuildFilesAsync(
        ProjectPlan plan, CancellationToken ct)
    {
        var archive = await _generation.GenerateWorkspaceAsync(plan, ct);
        await using var stream = archive.Stream;
        stream.Position = 0;

        var root = GenerationNaming.StripWhitespace(plan.WorkspaceName) + "/";
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
                var path = entry.FullName.StartsWith(root, StringComparison.Ordinal)
                    ? entry.FullName[root.Length..]
                    : entry.FullName;
                files.Add(new GitHubCommitFile(path, buffer.ToArray()));
            }
        }
        return (files, archive.FileName, stream.ToArray());
    }

    /// <summary>
    /// The first commit: every file as a blob, one tree built from nothing, one
    /// parentless commit, and the default branch pointed at it. The repository
    /// has no history to branch from, so this is the whole of it.
    /// </summary>
    private async Task CommitAsync(
        string token, GitHubRepositorySummary repository, ProjectPlan plan,
        List<GitHubCommitFile> files, int userId, CancellationToken ct)
    {
        var blobs = new List<(string Path, string BlobSha)>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            blobs.Add((file.Path, await _github.CreateBlobAsync(
                token, repository.Owner, repository.Name, file.Content, ct)));
        }

        var tree = await _github.CreateTreeAsync(
            token, repository.Owner, repository.Name, baseTreeSha: null, blobs, ct);
        var commit = await _github.CreateCommitAsync(
            token, repository.Owner, repository.Name,
            $"Add the {plan.WorkspaceName} workspace", tree, parentSha: null,
            author: await ResolveAuthorAsync(userId, ct), ct: ct);

        if (!await _github.CreateBranchAsync(
                token, repository.Owner, repository.Name, repository.DefaultBranch, commit, ct))
        {
            // Only reachable if something else pushed to the repository in the
            // seconds since it was created, which is not a case to paper over.
            throw Refuse(RepositoryField,
                $"Something else pushed to {repository.FullName} while the toolbox was filling it in, so "
                + "the generated files were not committed. Open it on GitHub to see what is there.");
        }
    }

    /// <summary>
    /// The person the commit is credited to, so a repository's history names
    /// whoever asked for it rather than the app that made the call. Their
    /// GitHub <c>noreply</c> address is used deliberately: it links the commit
    /// to their account without publishing an address they did not give us.
    /// Null when the link says nothing usable, which is not worth failing over.
    /// </summary>
    private async Task<GitHubCommitAuthor?> ResolveAuthorAsync(int userId, CancellationToken ct)
    {
        var link = await _access.GetLinkStatusAsync(ct);
        if (string.IsNullOrWhiteSpace(link.Login))
        {
            _logger.LogInformation("User {UserId} has no GitHub login on file; the commit is the app's.", userId);
            return null;
        }
        var email = link.GitHubUserId is { } id
            ? $"{id}+{link.Login}@users.noreply.github.com"
            : $"{link.Login}@users.noreply.github.com";
        return new GitHubCommitAuthor(link.Login!, email);
    }

    /// <summary>
    /// Records the repository in the audit log, so "who created this from the
    /// toolbox" has an answer months later.
    ///
    /// <para>Written by hand rather than by <c>AuditInterceptor</c> because
    /// nothing of ours changed - the row this describes lives on GitHub. That
    /// is also why <c>EntityId</c> is zero and the repository's full name
    /// carries the identity: there is no primary key of ours to point at, and
    /// an id from GitHub would read as one.</para>
    /// </summary>
    private async Task RecordAsync(
        GitHubRepositorySummary repository, ProjectPlan plan, int fileCount, CancellationToken ct)
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ChangedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct),
            ChangedByUserId = _orgContext.CurrentUserId,
            OrganizationId = _orgContext.CurrentOrganizationId,
            EntityType = AuditEntityType.GitHubRepository,
            EntityId = 0,
            Action = AuditAction.Created,
            EntityName = repository.FullName,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Recorded {RepoFullName} in the audit log for workspace '{Workspace}' ({FileCount} files).",
            repository.FullName, plan.WorkspaceName, fileCount);
    }

    /// <summary>
    /// One sentence for the missing grant, said the same way whether it was
    /// spotted from the recorded permissions or by GitHub refusing the call.
    /// Deliberately not "administration:write" - the person reading it has to
    /// ask somebody for something, not quote a permission name.
    /// </summary>
    private static string NotPermittedMessage(string orgLogin) =>
        $"AL Dev Toolbox has not been allowed to create repositories in {orgLogin}. An owner of that "
        + "GitHub organisation can allow it, and then this will work.";

    private static PlanValidationException Refuse(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}
