using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What "Create repository" produced, for the success state to render.</summary>
/// <param name="Repository">The repository that now exists, including the link the user needs next.</param>
/// <param name="FileCount">How many generated files the repository was filled with.</param>
/// <param name="ArchiveFileName">The name the same workspace would download under.</param>
/// <param name="StandardsFileCount">
/// How many of the organisation's repository standard files were committed on
/// top, in their own commit (issue #628). Zero when none are configured.
/// </param>
/// <param name="StandardsWarning">
/// What GitHub refused while applying the standards, in words the person who
/// pressed the button can act on - or null when nothing was refused. The
/// repository exists and is committed by the time this can be set, so it is a
/// warning on a success rather than a failure.
/// </param>
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
    byte[] Archive,
    int StandardsFileCount = 0,
    string? StandardsWarning = null);

/// <summary>
/// Creates a repository in the connected GitHub organisation and puts a freshly
/// generated workspace in it (issue #622).
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
    /// see CLAUDE.md on mirroring server rules in the form. The hyphen is
    /// escaped for the browser's sake, not .NET's: browsers compile
    /// <c>pattern</c> with the RegExp <c>v</c> flag, under which a bare
    /// <c>-</c> inside a character class is a syntax error - and a pattern that
    /// does not compile is dropped silently, leaving the field claiming that
    /// input the server refuses is fine.</para>
    /// </summary>
    public const string NamePattern = @"^(?!\.{1,2}$)[A-Za-z0-9._\-]{1,100}$";

    private static readonly Regex NameRegex = new(NamePattern, RegexOptions.Compiled);

    private readonly GenerationService _generation;
    private readonly GitHubRepositoryService _repositories;
    private readonly GitHubConnectionService _connection;
    private readonly GitHubAccessService _access;
    private readonly GitHubAppClient _github;
    private readonly GitHubRepositoryStandardsService _standards;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubWorkspaceRepositoryService> _logger;

    public GitHubWorkspaceRepositoryService(
        GenerationService generation,
        GitHubRepositoryService repositories,
        GitHubConnectionService connection,
        GitHubAccessService access,
        GitHubAppClient github,
        GitHubRepositoryStandardsService standards,
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<GitHubWorkspaceRepositoryService> logger)
    {
        _generation = generation;
        _repositories = repositories;
        _connection = connection;
        _access = access;
        _github = github;
        _standards = standards;
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
    /// with those files in it.
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
    /// <exception cref="GitHubApiException">GitHub refused one of the calls that fill the repository.</exception>
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
        // The organisation's own standards go on afterwards, as their own
        // commit, so "the files we generated" stays an honest description of
        // the first one.
        var standards = await ApplyStandardsAsync(token, repository, userId, ct);
        await RecordAsync(repository, plan, files.Count, ct);

        _logger.LogInformation(
            "User {UserId} created the repository {RepoFullName} from workspace '{Workspace}' "
            + "(template '{Template}', {FileCount} files, {Visibility}).",
            userId, repository.FullName, plan.WorkspaceName, plan.TemplateKey, files.Count,
            isPrivate ? "private" : "public");

        return new GitHubWorkspaceRepository(
            repository, files.Count, archiveName, archiveBytes,
            standards.FileCount, standards.Warning);
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
    /// Fills the new repository: one file through the Contents API to give it a
    /// history, then every generated file as a blob, one tree, one commit on top
    /// of that first one, and the default branch moved on to it.
    ///
    /// <para><strong>Why two writes.</strong> A repository created with
    /// <c>auto_init: false</c> has no commits, and the Git Data API refuses
    /// every call on one - <c>409 Conflict: Git Repository is empty.</c> - so
    /// blobs and trees have nothing to attach to. <c>PUT
    /// /repos/{owner}/{repo}/contents/{path}</c> is the one route that works
    /// there, because it creates the initial commit itself. Letting GitHub
    /// auto-initialise instead would plant a README nobody generated, which is
    /// the property the repository is created empty to protect.</para>
    ///
    /// <para>The tree is still built <em>from nothing</em> and carries every
    /// generated file, the seeded one included: layering onto the seed commit's
    /// tree would add a round trip and make "exactly the files we generated" an
    /// accident of what was there before rather than a fact about the tree. The
    /// seed file's entry names the same content, so it is neither duplicated nor
    /// overwritten - the second commit simply adds the rest.</para>
    /// </summary>
    private async Task CommitAsync(
        string token, GitHubRepositorySummary repository, ProjectPlan plan,
        List<GitHubCommitFile> files, int userId, CancellationToken ct)
    {
        if (ChooseSeed(files) is not { } seed)
        {
            _logger.LogWarning(
                "The '{Template}' template generated no files, so {RepoFullName} was left empty.",
                plan.TemplateKey, repository.FullName);
            return;
        }

        // Both commits name the same person: without an author the seed is
        // credited to the app, so a new repository would open on an initial
        // commit by a bot followed by one by the consultant who asked for it.
        var author = await ResolveAuthorAsync(userId, ct);

        GitHubFileWrite seedCommit;
        try
        {
            seedCommit = await _github.PutFileAsync(
                token, repository.Owner, repository.Name, seed.Path, repository.DefaultBranch,
                "Initial commit", seed.Content, baseSha: null, author: author, ct: ct);
        }
        catch (GitHubContentConflictException)
        {
            // The write quoted no base sha, so GitHub only refuses it if that
            // path is already there - which in a repository this new means
            // something else got in first. Same answer as a branch that moved.
            throw RaceRefusal(repository);
        }

        // A workspace of exactly one file is already committed and on the branch.
        if (files.Count == 1) return;

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
            $"Add the {plan.WorkspaceName} workspace", tree, parentSha: seedCommit.CommitSha,
            author: author, ct: ct);

        if (!await _github.UpdateBranchAsync(
                token, repository.Owner, repository.Name, repository.DefaultBranch, commit, ct))
        {
            // Only reachable if something else pushed to the repository in the
            // seconds since it was created, which is not a case to paper over.
            throw RaceRefusal(repository);
        }
    }

    /// <summary>
    /// Puts the organisation's repository standards on the new repository
    /// (issue #628): the standard files as one commit of their own, then the
    /// branch ruleset.
    ///
    /// <para><strong>Files first, ruleset second.</strong> A ruleset that
    /// requires a pull request would refuse a direct push to the default
    /// branch, so creating it before the commit would block the very files it
    /// is meant to sit alongside.</para>
    ///
    /// <para><strong>Its own commit</strong>, layered onto the branch head
    /// rather than built from nothing: the workspace is already there and the
    /// standards are added to it. A standard at a path the generator also
    /// produced therefore replaces it - the organisation's standard wins over
    /// the template. This is also why it is not caught by the one-file early
    /// return in <see cref="CommitAsync"/>: a workspace of a single file still
    /// gets its standards.</para>
    ///
    /// <para><strong>A ruleset refusal is a warning.</strong> By the time this
    /// runs the repository exists and carries the generated workspace, so
    /// failing here would leave a repository behind with a stack trace over it.
    /// GitHub's refusal is logged and returned as a sentence for the success
    /// card instead - typically the installation not being allowed to change
    /// repository settings.</para>
    /// </summary>
    private async Task<(int FileCount, string? Warning)> ApplyStandardsAsync(
        string token, GitHubRepositorySummary repository, int userId, CancellationToken ct)
    {
        var standards = await _standards.GetAsync(ct);
        var ruleset = standards.Ruleset is { IsEmpty: false } configured ? configured : null;
        if (standards.Files.Count == 0 && ruleset is null) return (0, null);

        var fileCount = 0;
        if (standards.Files.Count > 0)
        {
            var head = await _github.GetBranchHeadShaAsync(
                token, repository.Owner, repository.Name, repository.DefaultBranch, ct);
            if (head is null)
            {
                // Only reachable when the workspace commit never happened - a
                // template that generated nothing. Saying so beats teaching this
                // a second code path for a repository with no history.
                _logger.LogWarning(
                    "{RepoFullName} has no commit on {Branch}, so no repository standards were applied.",
                    repository.FullName, repository.DefaultBranch);
                return (0, "Your organisation's repository standards were not added, because the "
                    + "workspace put no files in the repository.");
            }
            var baseTree = await _github.GetCommitTreeShaAsync(
                token, repository.Owner, repository.Name, head, ct);

            var blobs = new List<(string Path, string BlobSha)>(standards.Files.Count);
            foreach (var file in standards.Files)
            {
                ct.ThrowIfCancellationRequested();
                blobs.Add((file.Path, await _github.CreateBlobAsync(
                    token, repository.Owner, repository.Name,
                    Encoding.UTF8.GetBytes(file.Content), ct)));
            }

            var tree = await _github.CreateTreeAsync(
                token, repository.Owner, repository.Name, baseTreeSha: baseTree, blobs, ct);
            var commit = await _github.CreateCommitAsync(
                token, repository.Owner, repository.Name,
                "Apply repository standards", tree, parentSha: head,
                author: await ResolveAuthorAsync(userId, ct), ct: ct);

            if (!await _github.UpdateBranchAsync(
                    token, repository.Owner, repository.Name, repository.DefaultBranch, commit, ct))
            {
                throw RaceRefusal(repository);
            }
            fileCount = standards.Files.Count;
        }

        string? warning = null;
        if (ruleset is not null)
        {
            try
            {
                await _github.CreateRepositoryRulesetAsync(
                    token, repository.Owner, repository.Name, ruleset, ct);
            }
            catch (GitHubApiException ex)
            {
                _logger.LogWarning(
                    ex, "GitHub refused the branch rules on {RepoFullName}.", repository.FullName);
                warning =
                    "The repository is ready, but GitHub would not set your branch rules on it. "
                    + "AL Dev Toolbox may not be allowed to change repository settings in this GitHub "
                    + "organisation - an owner of it can allow that. Until then, set the rules on GitHub.";
            }
        }

        _logger.LogInformation(
            "Applied repository standards to {RepoFullName}: {FileCount} file(s), branch rules {RulesetState}.",
            repository.FullName, fileCount,
            ruleset is null ? "not configured" : warning is null ? "created" : "refused");
        return (fileCount, warning);
    }

    /// <summary>
    /// What to say when somebody else wrote to the repository in the seconds
    /// between its creation and the toolbox filling it in. Not a case to paper
    /// over: whatever is in there now is not what was generated, and the person
    /// has to look.
    /// </summary>
    private static PlanValidationException RaceRefusal(GitHubRepositorySummary repository) =>
        Refuse(RepositoryField,
            $"Something else pushed to {repository.FullName} while the toolbox was filling it in, so "
            + "the generated files were not committed. Open it on GitHub to see what is there.");

    /// <summary>
    /// The one generated file that goes in through the Contents API to give the
    /// repository its first commit.
    ///
    /// <para>Chosen rather than taken at random: this file is what somebody sees
    /// if they open the repository between the two writes, and what the initial
    /// commit contains for the rest of the repository's life. A README is what
    /// GitHub itself would have put there, and a <c>.gitignore</c> is the next
    /// most ordinary thing to find in an initial commit - but which files a
    /// workspace has is up to the template, so the rule falls back to the first
    /// path in order and never depends on a template opting either of them
    /// in.</para>
    /// </summary>
    private static GitHubCommitFile? ChooseSeed(List<GitHubCommitFile> files) =>
        files.FirstOrDefault(f => f.Path.Equals(PlatformOrganizationFiles.ReadmePath, StringComparison.OrdinalIgnoreCase))
        ?? files.FirstOrDefault(f => f.Path.Equals(PlatformOrganizationFiles.GitignorePath, StringComparison.OrdinalIgnoreCase))
        ?? files.OrderBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();

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
