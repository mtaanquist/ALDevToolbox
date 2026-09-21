using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Tools;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// How the generated workspace reached the new repository (issue #811).
/// </summary>
public enum GitHubWorkspaceDelivery
{
    /// <summary>
    /// It is on the default branch, in a single initial commit. What happens
    /// unless the organisation's rules stand in the way.
    /// </summary>
    DefaultBranch,

    /// <summary>
    /// The organisation only allows changes to the default branch through a
    /// pull request, so the workspace is waiting in one.
    /// </summary>
    PullRequest,
}

/// <summary>What "Create repository" produced, for the success state to render.</summary>
/// <param name="Repository">The repository that now exists, including the link the user needs next.</param>
/// <param name="FileCount">How many generated files the repository was filled with.</param>
/// <param name="ArchiveFileName">The name the same workspace would download under.</param>
/// <param name="StandardsFileCount">
/// How many of the organisation's repository standard files were committed
/// alongside the workspace (issue #628). Zero when none are configured.
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
/// <param name="SolutionId">
/// The solution the repository was registered on (issue #759), or null when
/// registering it failed - in which case <paramref name="SolutionWarning"/>
/// says so.
/// </param>
/// <param name="SolutionName">That solution's name, for the success state to link.</param>
/// <param name="SolutionCreated">
/// True when the solution was created for this customer rather than picked. The
/// success state says "created" or "registered on" accordingly.
/// </param>
/// <param name="SolutionWarning">
/// Why the repository is not on a solution, in words the person who pressed the
/// button can act on - or null when it is. The repository exists and is
/// committed by the time this can be set, so it is a warning on a success
/// rather than a failure, the same shape as <paramref name="StandardsWarning"/>.
/// </param>
/// <param name="Delivery">
/// Whether the workspace is on the default branch or waiting in a pull request
/// (issue #811). The success state says a different thing for each.
/// </param>
/// <param name="PullRequestUrl">
/// The pull request holding the workspace, when there is one - null whenever
/// <paramref name="Delivery"/> is <see cref="GitHubWorkspaceDelivery.DefaultBranch"/>.
/// </param>
/// <param name="DefaultBranchWarning">
/// What to do on GitHub when the workspace is on its branch but GitHub would
/// not make that branch the default - or null, which is the usual case. A
/// warning on a success, like the other two.
/// </param>
public sealed record GitHubWorkspaceRepository(
    GitHubRepositorySummary Repository,
    int FileCount,
    string ArchiveFileName,
    byte[] Archive,
    int StandardsFileCount = 0,
    string? StandardsWarning = null,
    int? SolutionId = null,
    string? SolutionName = null,
    bool SolutionCreated = false,
    string? SolutionWarning = null,
    GitHubWorkspaceDelivery Delivery = GitHubWorkspaceDelivery.DefaultBranch,
    string? PullRequestUrl = null,
    string? DefaultBranchWarning = null);

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
/// individual should need <c>admin:org</c> on their own account for the workbench
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

    /// <summary>Error key for problems with the solution the caller chose to register on.</summary>
    public const string SolutionField = "SolutionId";

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
    private readonly ProjectService _projects;
    private readonly OrganizationConfigService _orgConfig;
    private readonly ToolEnablement _tools;
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
        ProjectService projects,
        OrganizationConfigService orgConfig,
        ToolEnablement tools,
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
        _projects = projects;
        _orgConfig = orgConfig;
        _tools = tools;
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; repository creation called outside an authenticated request.");

    /// <summary>
    /// The repository name a customer called <paramref name="workspaceName"/>
    /// suggests: the transliterated name in the organisation's repository
    /// style, which "Jørgensen Møbler" can reach as well as "CRONUS" can. The
    /// style defaults to lowercase kebab-case, which is both a legal GitHub
    /// name and the shape people actually name repositories. Only a suggestion
    /// - the user can type anything the rule above allows.
    /// See <see cref="Generation.CustomerNaming"/>.
    /// </summary>
    public static string SuggestName(string? workspaceName, NamingStyle style = NamingStyle.KebabCase) =>
        CustomerNaming.Apply(workspaceName, style);

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
    /// workbench organisation connected, so a caller naming a repository cannot
    /// aim it anywhere else.</para>
    ///
    /// <para>The repository is also what registers the customer as a solution
    /// (issue #759): <paramref name="solutionId"/> names one to add it to, and
    /// leaving it out creates one named after the customer. That happens last,
    /// after the repository exists, and a failure there is a warning on the
    /// result rather than an exception - see
    /// <c>.design/customer-naming.md</c>.</para>
    /// </summary>
    /// <param name="solutionId">
    /// An existing solution to register the new repository on, which the caller
    /// must be allowed to manage. Null creates one for the customer.
    /// </param>
    /// <exception cref="PlanValidationException">The plan, the name, the solution, or the caller's access is not good enough.</exception>
    /// <exception cref="GitHubApiException">GitHub refused one of the calls that fill the repository.</exception>
    public async Task<GitHubWorkspaceRepository> CreateAsync(
        ProjectPlan plan, string repositoryName, bool isPrivate, int? solutionId = null,
        CancellationToken ct = default)
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

        // An organisation that has Solutions switched off gets a repository and
        // nothing else: there is no Solutions surface for a solution to be seen
        // on, so registering one would be inventing work the organisation asked
        // not to have (issue #772). A caller that named one is told why, rather
        // than having the id quietly ignored.
        var solutionsEnabled = await _tools.IsEnabledAsync(ToolKey.Projects, ct);
        if (!solutionsEnabled && solutionId is not null)
        {
            throw Refuse(SolutionField,
                "Solutions are switched off for your organisation, so the repository cannot be "
                + "registered on one.");
        }

        // A solution the caller may not add a repository to is a refusal like
        // any other, so it is ruled out here rather than after a repository
        // exists that has nowhere to go. Same answer whether it is somebody
        // else's or gone: an id they cannot act on.
        if (solutionsEnabled && solutionId is { } chosen && !await _projects.CanManageAsync(chosen, ct))
        {
            throw Refuse(SolutionField,
                "You cannot add a repository to that solution. Pick a different customer, or ask "
                + "whoever owns the solution to add the repository for you.");
        }

        // Why-not first, so the answer names the thing the caller can change.
        var access = await _repositories.GetAccessAsync(ct);
        if (!access.IsReady) throw Refuse(RepositoryField, access.Readiness switch
        {
            GitHubRepositoryReadiness.NotConfigured =>
                "GitHub is not set up on this server yet, so no repository can be created. "
                + "Ask whoever runs AL Workbench to set it up.",
            GitHubRepositoryReadiness.NotConnected =>
                "Your organisation has not connected a GitHub organisation yet, so there is nowhere to "
                + "create this. An administrator connects one under Administration -> Repositories.",
            GitHubRepositoryReadiness.LinkNeedsRepair =>
                "Your GitHub account is no longer connected to the workbench. Connect it again on your "
                + "account page under Repository access, then try this again.",
            _ =>
                "Connect your own GitHub account first, on your account page under Repository access. "
                + "The workbench checks that you are in the GitHub organisation before it creates anything there.",
        });

        var connection = await _connection.GetStatusAsync(ct);
        var orgLogin = connection.OrgLogin!;
        var installationId = connection.InstallationId!.Value;

        if (!await _access.IsOrgMemberAsync(userId, ct))
        {
            throw Refuse(RepositoryField,
                $"GitHub does not list you as a member of {orgLogin}, so the workbench will not create a "
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

        // Asked before anything is written, so the log says what the
        // organisation's rules were when the repository was filled. It steers
        // nothing: the fill attempts the direct route regardless and treats
        // GitHub's own refusal as the authority, because a pull_request rule
        // governs updating a branch and there is no evidence it governs
        // creating one - which is precisely the case this flow is built around.
        await LogBranchRulesAsync(token, repository, ct);

        // The organisation's standards ride in the same commit as the generated
        // files (issue #811), so read them before the fill rather than after it.
        var standards = await _standards.GetAsync(ct);
        var fill = await FillAsync(token, repository, plan, files, standards.Files, userId, ct);
        var rulesetWarning = await ApplyRulesetAsync(
            token, repository, standards.Ruleset is { IsEmpty: false } configured ? configured : null, ct);
        // Last, because a solution with no repository is the orphan the whole
        // ordering exists to avoid. Skipped entirely when the organisation does
        // not use Solutions.
        var solution = solutionsEnabled
            ? await RegisterSolutionAsync(plan, repository, solutionId, ct)
            : default((int? Id, string? Name, bool Created, string? Warning));
        await RecordAsync(repository, plan, files.Count, solution.Id, ct);

        _logger.LogInformation(
            "User {UserId} created the repository {RepoFullName} from workspace '{Workspace}' "
            + "(template '{Template}', {FileCount} files, {Visibility}, solution {SolutionId}, "
            + "delivered {Delivery}).",
            userId, repository.FullName, plan.WorkspaceName, plan.TemplateKey, files.Count,
            isPrivate ? "private" : "public", solution.Id, fill.Delivery);

        return new GitHubWorkspaceRepository(
            repository, files.Count, archiveName, archiveBytes,
            fill.StandardsFileCount, rulesetWarning,
            solution.Id, solution.Name, solution.Created, solution.Warning,
            fill.Delivery, fill.PullRequestUrl, fill.DefaultBranchWarning);
    }

    /// <summary>
    /// Says in the log which rules govern the repository's default branch, so a
    /// support question about a workspace that arrived as a pull request has an
    /// answer without a trip to GitHub's settings.
    ///
    /// <para>Never fails the flow: GitHub not answering is a fact about the
    /// read, not about the repository.</para>
    /// </summary>
    private async Task LogBranchRulesAsync(
        string token, GitHubRepositorySummary repository, CancellationToken ct)
    {
        IReadOnlyList<string>? types;
        try
        {
            types = await _github.GetBranchRuleTypesAsync(
                token, repository.Owner, repository.Name, repository.DefaultBranch, ct);
        }
        catch (GitHubApiException ex)
        {
            _logger.LogInformation(
                ex, "Could not read the branch rules on {RepoFullName}.", repository.FullName);
            return;
        }

        if (types is null)
        {
            _logger.LogInformation(
                "GitHub did not say which rules apply to {Branch} on {RepoFullName}.",
                repository.DefaultBranch, repository.FullName);
            return;
        }

        _logger.LogInformation(
            "{Branch} on {RepoFullName} is governed by {RuleCount} rule(s): {Rules}. "
            + "A pull request rule is expected to send the workspace through one.",
            repository.DefaultBranch, repository.FullName, types.Count,
            types.Count == 0 ? "none" : string.Join(", ", types));
    }

    /// <summary>
    /// Registers the new repository on the customer's solution (issue #759):
    /// on the one the caller picked, or on one created for the customer.
    ///
    /// <para><strong>Nothing here may throw.</strong> The repository exists and
    /// holds the workspace by the time this runs, so a solution that would not
    /// save has to come back as a sentence beside a success - the same shape as
    /// a refused ruleset. The likeliest cause is a name another solution already
    /// uses, which is a thing the person can sort out in a moment and not a
    /// reason to lose the repository they just made.</para>
    ///
    /// <para>The clone URL is what gets stored, because that is the shape the
    /// solution editor validates and what the build pipeline clones - the same
    /// row a person typing the repository in by hand would have produced.</para>
    /// </summary>
    private async Task<(int? Id, string? Name, bool Created, string? Warning)> RegisterSolutionAsync(
        ProjectPlan plan, GitHubRepositorySummary repository, int? solutionId, CancellationToken ct)
    {
        var row = new ProjectRepositoryInput(
            RepositoryProvider.GitHub, repository.CloneUrl, repository.Name);
        try
        {
            if (solutionId is { } id)
            {
                return (id, await _projects.AddRepositoryAsync(id, row, ct), false, null);
            }

            var name = plan.WorkspaceName.Trim();
            var created = await _projects.CreateProjectAsync(new ProjectInput(
                name,
                string.IsNullOrWhiteSpace(plan.ShortName) ? null : plan.ShortName!.Trim(),
                await DefaultCountryAsync(ct),
                [row]),
                ct);
            return (created, name, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "{RepoFullName} was created but could not be registered on a solution.",
                repository.FullName);
            return (null, null, false,
                "The repository is ready, but it is not on a solution yet. Add it from Solutions, on "
                + "that customer's Repositories tab.");
        }
    }

    /// <summary>
    /// The country code a solution created from here compiles against. The New
    /// Workspace form never asks for one, so this is the organisation's own
    /// first import country - the same pre-fill the untracked-repositories panel
    /// offers - and the worldwide base when it has none. Either way it is one
    /// field on the solution's own page, changed in a moment, and a solution
    /// that refused to save over it would be worse than a guess.
    /// </summary>
    private async Task<string> DefaultCountryAsync(CancellationToken ct)
    {
        var settings = (await _orgConfig.GetCurrentAsync(ct)).Settings;
        return OrganizationConfigService.ParseAutoImportCountries(settings.AutoImportCountry)
            .FirstOrDefault() ?? "w1";
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

        // The archive is named after the workspace folder it nests everything
        // under, so the prefix to strip comes off the name the generator just
        // used rather than being derived a second time (and possibly in a
        // different style than the organisation has set).
        var root = Path.GetFileNameWithoutExtension(archive.FileName) + "/";
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

    /// <summary>The throwaway branch the repository's very first commit lands on.</summary>
    private const string SeedBranch = "aldt/seed";

    /// <summary>The branch a workspace waits on when it has to go through a pull request.</summary>
    private const string WorkspaceBranch = "aldt/initial-workspace";

    /// <summary>
    /// Fills the new repository with one commit: every generated file plus the
    /// organisation's standards, on the default branch.
    ///
    /// <para><strong>Why a branch is created rather than updated.</strong> An
    /// organisation ruleset that requires a pull request applies to a
    /// repository the moment it exists, and refuses every <em>update</em> of
    /// the default branch - which is what the workbench used to do, leaving a
    /// repository holding nothing but the file it had seeded (issue #811).
    /// Creating the ref at a finished commit is not an update, so the workspace
    /// arrives whole. It also reads better: one "Initial commit" rather than a
    /// seed, a workspace commit and a standards commit.</para>
    ///
    /// <para><strong>Why there is still a seed.</strong> A repository created
    /// with <c>auto_init: false</c> has no commits, and the Git Data API
    /// refuses every call on one - <c>409 Conflict: Git Repository is
    /// empty.</c> - so blobs and trees have nothing to attach to. <c>PUT
    /// /repos/{owner}/{repo}/contents/{path}</c> is the one route that works
    /// there, because it creates the initial commit itself. It goes to
    /// <see cref="SeedBranch"/> rather than to the default branch, so the
    /// default branch is untouched and can be created outright afterwards.
    /// Letting GitHub auto-initialise instead would plant a README nobody
    /// generated, which is the property the repository is created empty to
    /// protect.</para>
    ///
    /// <para><strong>Why the default branch is then set.</strong> On a
    /// repository with no commits, the first branch to receive one becomes the
    /// default - so after the seed the default branch is
    /// <see cref="SeedBranch"/>, and pointing it back is a change to a
    /// repository setting rather than to a ref.</para>
    ///
    /// <para><strong>Why this works, and how far that reaches.</strong> The
    /// ruleset in issue #811 targets <c>~DEFAULT_BRANCH</c>, and from the seed
    /// until step 4 the default branch is <see cref="SeedBranch"/> - so
    /// creating <c>refs/heads/main</c> is not an operation on a protected
    /// branch at all, and the default-branch switch is a repository-settings
    /// write no branch ruleset governs. Neither step depends on the weaker
    /// claim that creating a ref is not rule-checked. That claim is only needed
    /// by a ruleset naming the branch outright (<c>refs/heads/main</c>) rather
    /// than symbolically, and it has not been verified against a real
    /// organisation - which is exactly what the fallback below covers.</para>
    ///
    /// <para><strong>And if GitHub refuses anyway.</strong> A rule violation on
    /// either of those two steps falls back to a pull request inside the same
    /// operation rather than leaving a half-filled repository behind. Both
    /// routes are equally legitimate under the rule; the difference is only
    /// which one needs a human to press merge.</para>
    ///
    /// <para>See <c>.design/github-integration.md</c>, "#622 New workspace".</para>
    /// </summary>
    private async Task<(int StandardsFileCount, GitHubWorkspaceDelivery Delivery, string? PullRequestUrl, string? DefaultBranchWarning)> FillAsync(
        string token, GitHubRepositorySummary repository, ProjectPlan plan,
        List<GitHubCommitFile> files, IReadOnlyList<GitHubRepositoryStandardFile> standardsFiles,
        int userId, CancellationToken ct)
    {
        var owner = repository.Owner;
        var name = repository.Name;
        var defaultBranch = repository.DefaultBranch;

        var contents = Merge(files, standardsFiles);
        if (ChooseSeed(contents) is not { } seed)
        {
            _logger.LogWarning(
                "The '{Template}' template generated no files, so {RepoFullName} was left empty.",
                plan.TemplateKey, repository.FullName);
            return (0, GitHubWorkspaceDelivery.DefaultBranch, null, null);
        }

        // Every commit names the same person: without an author the seed is
        // credited to the app, so a new repository would open on a commit by a
        // bot rather than by the consultant who asked for it.
        var author = await ResolveAuthorAsync(userId, ct);

        // The seed is expected to create the branch it names, because on an
        // empty repository there is no branch for it to land on otherwise. If
        // that turns out not to hold, the default branch takes the seed as it
        // used to and the pull-request route carries the rest.
        var seedOnDefaultBranch = false;
        try
        {
            // Nothing needs the sha it comes back with: the tree below is built
            // from nothing, and the pull-request route reads the branch head
            // back from GitHub rather than trusting one computed here.
            await _github.PutFileAsync(
                token, owner, name, seed.Path, SeedBranch,
                "Initial commit", seed.Content, baseSha: null, author: author, ct: ct);
        }
        catch (GitHubContentConflictException)
        {
            // The write quoted no base sha, so GitHub only refuses it if that
            // path is already there - which in a repository this new means
            // something else got in first. Same answer as a branch that moved.
            throw RaceRefusal(repository);
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning(
                ex, "GitHub would not start {RepoFullName} on {Branch}; seeding {DefaultBranch} instead.",
                repository.FullName, SeedBranch, defaultBranch);
            seedOnDefaultBranch = true;
            try
            {
                await _github.PutFileAsync(
                    token, owner, name, seed.Path, defaultBranch,
                    "Initial commit", seed.Content, baseSha: null, author: author, ct: ct);
            }
            catch (GitHubContentConflictException)
            {
                throw RaceRefusal(repository);
            }
        }

        var blobs = new List<(string Path, string BlobSha)>(contents.Count);
        foreach (var file in contents)
        {
            ct.ThrowIfCancellationRequested();
            blobs.Add((file.Path, await _github.CreateBlobAsync(token, owner, name, file.Content, ct)));
        }

        // Built from nothing and listing everything the repository should end
        // up with, the seeded file included: layering onto the seed's tree
        // would make "exactly what we generated" an accident of what happened
        // to be there rather than a fact about the tree.
        var tree = await _github.CreateTreeAsync(token, owner, name, baseTreeSha: null, blobs, ct);

        if (!seedOnDefaultBranch)
        {
            var root = await _github.CreateCommitAsync(
                token, owner, name, "Initial commit", tree, parentSha: null, author: author, ct: ct);
            var refCreated = false;
            try
            {
                if (!await _github.CreateBranchAsync(token, owner, name, defaultBranch, root, ct))
                {
                    // The branch is already there, on a repository created
                    // seconds ago: something else got in first.
                    throw RaceRefusal(repository);
                }
                refCreated = true;
                var defaultBranchWarning = await MakeDefaultAsync(token, repository, ct);

                _logger.LogInformation(
                    "Filled {RepoFullName} with {FileCount} file(s) in one commit on {Branch}.",
                    repository.FullName, contents.Count, defaultBranch);
                return (standardsFiles.Count, GitHubWorkspaceDelivery.DefaultBranch, null, defaultBranchWarning);
            }
            catch (GitHubApiException ex) when (ex.IsRuleViolation)
            {
                _logger.LogWarning(
                    ex, "The rules on {RepoFullName} would not take the workspace on {Branch} directly; "
                    + "opening a pull request for it instead.",
                    repository.FullName, defaultBranch);
                // Leave nothing half-made: whatever the pull request is built
                // on, it must not be a default branch this attempt invented.
                if (refCreated) await _github.DeleteBranchAsync(token, owner, name, defaultBranch, ct);
            }
        }

        return (standardsFiles.Count, GitHubWorkspaceDelivery.PullRequest,
            await OpenPullRequestAsync(token, repository, plan, seed, tree, author, contents.Count, ct), null);
    }

    /// <summary>
    /// Makes the branch that now holds the workspace the repository's default
    /// and removes the throwaway one, returning a sentence for the success
    /// state when GitHub would not.
    ///
    /// <para><strong>A refusal here is a warning, not a failure.</strong> The
    /// workspace is already whole on its branch, so throwing would report
    /// "GitHub refused to create the repository" about a repository that
    /// exists and is full, and skip the solution and the audit entry with it.
    /// A rule violation is the one exception: it still goes to the caller,
    /// whose pull-request route puts the repository in shape another way.</para>
    /// </summary>
    private async Task<string?> MakeDefaultAsync(
        string token, GitHubRepositorySummary repository, CancellationToken ct)
    {
        var owner = repository.Owner;
        var name = repository.Name;
        var branch = repository.DefaultBranch;
        try
        {
            await _github.SetDefaultBranchAsync(token, owner, name, branch, ct);
        }
        catch (GitHubApiException ex) when (!ex.IsRuleViolation)
        {
            // Refused is not the same as wrong: if the seed never took the
            // default away, there was nothing to change.
            GitHubRepositorySummary? current = null;
            try
            {
                current = await _github.GetRepositoryAsync(token, owner, name, ct);
            }
            catch (GitHubApiException)
            {
            }

            if (!string.Equals(current?.DefaultBranch, branch, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    ex, "{RepoFullName} holds the workspace on {Branch}, but its default branch is still {Current}.",
                    repository.FullName, branch, current?.DefaultBranch ?? "(unknown)");
                // The throwaway branch stays: it is the default, and GitHub
                // does not delete a default branch.
                return $"The workspace is on {branch}, but GitHub would not make {branch} the default branch. "
                    + $"On GitHub, open {repository.FullName} → Settings → General → Default branch, "
                    + $"switch it to {branch}, then delete the other branch.";
            }
        }

        await _github.DeleteBranchAsync(token, owner, name, SeedBranch, ct);
        return null;
    }

    /// <summary>
    /// Puts the workspace on a branch of its own and opens a pull request for
    /// it - the route for an organisation whose rules refuse everything else
    /// (issue #811).
    ///
    /// <para><strong>The repository is put in its proper shape first.</strong>
    /// The pull request is aimed at the default branch the repository was
    /// created with, not at whatever the seed left as the default: a customer
    /// whose organisation took this route would otherwise be handed a
    /// repository whose default branch is a throwaway name the workbench made up,
    /// with no <c>main</c> and a pull request merging into the wrong thing. So
    /// the real branch is made to exist, made the default, and the throwaway
    /// one goes - and only then is the workspace put up for review.</para>
    ///
    /// <para>The branch is made with a Contents write rather than a ref
    /// creation, because a ref creation is the operation that was just refused,
    /// and a Contents write onto the branch is what the workbench did before this
    /// issue - which the bug report shows this organisation's rules allow (a
    /// <c>.gitignore</c> did land on <c>main</c>; it was the <em>update</em>
    /// after it that was refused). A ref creation at the seed commit is tried
    /// if that fails, and a repository that refuses both is reported rather
    /// than left in a shape nobody can read.</para>
    /// </summary>
    private async Task<string> OpenPullRequestAsync(
        string token, GitHubRepositorySummary repository, ProjectPlan plan,
        GitHubCommitFile seed, string treeSha, GitHubCommitAuthor? author, int fileCount,
        CancellationToken ct)
    {
        var owner = repository.Owner;
        var name = repository.Name;
        var branch = repository.DefaultBranch;

        try
        {
            var head = await _github.GetBranchHeadShaAsync(token, owner, name, branch, ct);
            if (head is null)
            {
                head = await StartDefaultBranchAsync(token, repository, seed, author, ct);
            }

            var current = await _github.GetRepositoryAsync(token, owner, name, ct);
            if (!string.Equals(current?.DefaultBranch, branch, StringComparison.Ordinal))
            {
                // Seeding made the throwaway branch the default; the settings
                // write is not governed by a branch ruleset.
                await _github.SetDefaultBranchAsync(token, owner, name, branch, ct);
            }
            await _github.DeleteBranchAsync(token, owner, name, SeedBranch, ct);

            var commit = await _github.CreateCommitAsync(
                token, owner, name, $"Add the {plan.WorkspaceName} workspace", treeSha,
                parentSha: head, author: author, ct: ct);
            if (!await _github.CreateBranchAsync(token, owner, name, WorkspaceBranch, commit, ct))
            {
                throw RaceRefusal(repository);
            }

            var pullRequest = await _github.CreatePullRequestAsync(
                token, owner, name, $"Add the {plan.WorkspaceName} workspace",
                WorkspaceBranch, branch,
                $"AL Workbench generated this workspace: {fileCount} "
                + $"{(fileCount == 1 ? "file" : "files")} for {plan.WorkspaceName}.\n\n"
                + $"Your GitHub organisation only allows changes to {branch} through a pull request, so "
                + "the files are here rather than committed straight to it. The workspace is in this "
                + $"pull request, and {branch} holds only the one file the repository was started with "
                + "until this is merged.",
                ct);

            _logger.LogInformation(
                "Opened pull request {PullRequestUrl} with {FileCount} file(s) for {RepoFullName}, "
                + "because {Branch} only takes changes through one.",
                pullRequest.HtmlUrl, fileCount, repository.FullName, branch);
            return pullRequest.HtmlUrl;
        }
        catch (GitHubApiException ex)
        {
            // Both routes refused. What is left on GitHub is one placeholder
            // file, and the person needs the repository named so they can find
            // it - not GitHub's wording, which they cannot act on, and not the
            // name of a branch the workbench invented, which means nothing to
            // them.
            _logger.LogWarning(
                ex, "GitHub refused the pull request holding the workspace for {RepoFullName} too.",
                repository.FullName);
            throw Refuse(RepositoryField,
                $"Your GitHub organisation only allows changes to {repository.DefaultBranch} through a "
                + "pull request, and GitHub refused the pull request AL Workbench opened as well. "
                + $"{repository.FullName} was created but is empty apart from a single placeholder file. "
                + "Use Download ZIP above and push the workspace yourself through a pull request, or "
                + "delete the repository on GitHub and try again.");
        }
    }

    /// <summary>
    /// Brings the repository's real default branch into being on the
    /// pull-request route, and returns the commit it points at.
    ///
    /// <para>Two ways round, in the order most likely to be allowed by the
    /// rules that sent the flow here: a Contents write onto the branch, which
    /// is how the workbench used to start a repository, and failing that a ref
    /// creation at the commit the throwaway branch already holds.</para>
    /// </summary>
    private async Task<string> StartDefaultBranchAsync(
        string token, GitHubRepositorySummary repository, GitHubCommitFile seed,
        GitHubCommitAuthor? author, CancellationToken ct)
    {
        var owner = repository.Owner;
        var name = repository.Name;
        var branch = repository.DefaultBranch;
        try
        {
            var written = await _github.PutFileAsync(
                token, owner, name, seed.Path, branch,
                "Initial commit", seed.Content, baseSha: null, author: author, ct: ct);
            return written.CommitSha;
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubContentConflictException)
        {
            _logger.LogWarning(
                ex, "GitHub would not start {Branch} on {RepoFullName} with a file write; "
                + "pointing it at the commit the throwaway branch holds instead.",
                branch, repository.FullName);
        }

        var seedHead = await _github.GetBranchHeadShaAsync(token, owner, name, SeedBranch, ct)
            ?? throw new GitHubApiException(
                System.Net.HttpStatusCode.NotFound,
                "The branch the repository was started on is no longer there.");
        if (!await _github.CreateBranchAsync(token, owner, name, branch, seedHead, ct))
        {
            // Somebody else made it in the meantime; whatever is on it now is
            // what the pull request will be aimed at.
            _logger.LogInformation(
                "{Branch} on {RepoFullName} appeared while the workbench was making it.",
                branch, repository.FullName);
        }
        return seedHead;
    }

    /// <summary>
    /// The generated files with the organisation's standard files laid over
    /// them (issue #628): a standard at a path the generator also produced
    /// replaces it, so the organisation's standard wins over the template.
    ///
    /// <para>One list rather than two commits since issue #811 - the whole
    /// repository has to be described by a single commit for that commit to be
    /// the one the default branch is created at.</para>
    /// </summary>
    private static List<GitHubCommitFile> Merge(
        List<GitHubCommitFile> generated, IReadOnlyList<GitHubRepositoryStandardFile> standards)
    {
        // Git paths are case-sensitive, so "readme.md" and "README.md" are two
        // files and only an exact match is an override.
        var byPath = new Dictionary<string, GitHubCommitFile>(StringComparer.Ordinal);
        var order = new List<string>(generated.Count + standards.Count);
        foreach (var file in generated)
        {
            if (byPath.TryAdd(file.Path, file)) order.Add(file.Path);
        }
        foreach (var standard in standards)
        {
            if (!byPath.ContainsKey(standard.Path)) order.Add(standard.Path);
            byPath[standard.Path] = new GitHubCommitFile(
                standard.Path, Encoding.UTF8.GetBytes(standard.Content));
        }
        return order.Select(path => byPath[path]).ToList();
    }

    /// <summary>
    /// Puts the organisation's branch ruleset on the new repository (issue
    /// #628), after its files.
    ///
    /// <para><strong>Files first, ruleset second.</strong> A ruleset that
    /// requires a pull request would refuse a direct push to the default
    /// branch, so creating it before the commit would block the very files it
    /// is meant to sit alongside.</para>
    ///
    /// <para><strong>A refusal is a warning.</strong> By the time this runs the
    /// repository exists and carries the workspace, so failing here would leave
    /// a repository behind with a stack trace over it. GitHub's refusal is
    /// logged and returned as a sentence for the success card instead -
    /// typically the installation not being allowed to change repository
    /// settings.</para>
    /// </summary>
    private async Task<string?> ApplyRulesetAsync(
        string token, GitHubRepositorySummary repository, GitHubRepositoryRuleset? ruleset,
        CancellationToken ct)
    {
        if (ruleset is null) return null;

        try
        {
            await _github.CreateRepositoryRulesetAsync(
                token, repository.Owner, repository.Name, ruleset, ct);
            _logger.LogInformation("Set the branch rules on {RepoFullName}.", repository.FullName);
            return null;
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning(
                ex, "GitHub refused the branch rules on {RepoFullName}.", repository.FullName);
            return
                "The repository is ready, but GitHub would not set your branch rules on it. "
                + "AL Workbench may not be allowed to change repository settings in this GitHub "
                + "organisation - an owner of it can allow that. Until then, set the rules on GitHub.";
        }
    }

    /// <summary>
    /// What to say when somebody else wrote to the repository in the seconds
    /// between its creation and the workbench filling it in. Not a case to paper
    /// over: whatever is in there now is not what was generated, and the person
    /// has to look.
    /// </summary>
    private static PlanValidationException RaceRefusal(GitHubRepositorySummary repository) =>
        Refuse(RepositoryField,
            $"Something else pushed to {repository.FullName} while the workbench was filling it in, so "
            + "the generated files were not committed. Open it on GitHub to see what is there.");

    /// <summary>
    /// The one generated file that goes in through the Contents API to give the
    /// repository its first commit.
    ///
    /// <para>Chosen rather than taken at random: this file is what a repository
    /// is left holding if everything after the seed is refused, and what the
    /// throwaway branch shows to anybody who looks. A README is what
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
    /// workbench" has an answer months later.
    ///
    /// <para>Written by hand rather than by <c>AuditInterceptor</c> because
    /// nothing of ours changed - the row this describes lives on GitHub, which
    /// is why the repository's full name carries the identity: an id from
    /// GitHub would read as a primary key of ours.</para>
    ///
    /// <para><c>EntityId</c> is the solution the repository was registered on
    /// (issue #759), which is the one row of ours this act does touch, and zero
    /// when registering it failed. Never an id from GitHub.</para>
    /// </summary>
    private async Task RecordAsync(
        GitHubRepositorySummary repository, ProjectPlan plan, int fileCount, int? solutionId,
        CancellationToken ct)
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ChangedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct),
            ChangedByUserId = _orgContext.CurrentUserId,
            OrganizationId = _orgContext.CurrentOrganizationId,
            EntityType = AuditEntityType.GitHubRepository,
            EntityId = solutionId ?? 0,
            Action = AuditAction.Created,
            EntityName = repository.FullName,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Recorded {RepoFullName} in the audit log for workspace '{Workspace}' "
            + "({FileCount} files, solution {SolutionId}).",
            repository.FullName, plan.WorkspaceName, fileCount, solutionId);
    }

    /// <summary>
    /// One sentence for the missing grant, said the same way whether it was
    /// spotted from the recorded permissions or by GitHub refusing the call.
    /// Deliberately not "administration:write" - the person reading it has to
    /// ask somebody for something, not quote a permission name.
    /// </summary>
    private static string NotPermittedMessage(string orgLogin) =>
        $"AL Workbench has not been allowed to create repositories in {orgLogin}. An owner of that "
        + "GitHub organisation can allow it, and then this will work.";

    private static PlanValidationException Refuse(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}
