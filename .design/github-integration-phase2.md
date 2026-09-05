# GitHub integration, phase 2

Milestone "GitHub integration, phase 2" (issues #626-#633). Builds on the App, the
per-organisation connection, the per-user link and the shared repository picker that
`github-integration.md` describes; read that document first, because every rule in it
still holds here - the credential split, the single resolver
(`GitHubRepositoryService.ResolveAsync`), "404 is never gone", "an answer we could not
get is a refusal", the field-keyed `PlanValidationException` refusals.

This document is the spec for the eight issues. Where it disagrees with an issue body,
this document wins and the deviation is called out. Each section carries a **Decisions**
list settled before the code was written and an **As built** list appended by the
implementer.

## What phase 2 changes about the phase 1 fences

Phase 1 kept two fences that phase 2 relaxes deliberately, both inside the sanctioned
shapes `CLAUDE.md` already names:

- **"No queue - every GitHub call runs on the request thread."** Three features here
  are background work by nature: repository discovery (#629), translation-memory ingest
  (#631) and the pull-request compile gate (#627). They run on the existing in-process
  bases (`PolledScheduler`, `JobQueue`/`QueueDrainWorker`) - no broker, no external
  queue. Everything a *person* triggers still runs on the request thread behind a
  loading state.
- **"Webhooks are out of scope - nothing needs GitHub to call us."** #627 is the first
  inbound call. It is one anonymous `POST`, verified by HMAC over a deployment-wide
  secret, that only ever enqueues.

Two things phase 2 does **not** do without the maintainer's explicit yes, and the
implementations are written so they do not need to:

- **No new `IgnoreQueryFilters()` call site.** Every scheduler enumerates
  `organizations` (a table with no tenant filter) and enters an `AmbientOrganizationScope`
  per org before reading that org's settings, the way `EnvironmentRefreshScheduler`
  does. The webhook worker resolves an `installation_id` back to an organisation the
  same way. A single cross-org lookup would be cheaper; it is a fence crossing and is
  listed as an ask, not done.
- **One new key-ring-encrypted secret**, the webhook secret (#627), on
  `system_settings`, redacted by `AuditInterceptor` like the App's private key. The issue
  asked for exactly this; the PR body says so.

## Shared plumbing

- **`GitHubAppClient` is `partial`.** Each feature adds its REST calls in its own file
  (`GitHubAppClient.Releases.cs`, `GitHubAppClient.Checks.cs`, `GitHubAppClient.Rulesets.cs`,
  `GitHubAppClient.Blobs.cs`) so parallel work does not fight over one 1,200-line file.
  Same conventions: credential first, `JsonDocument` reads, anonymous-object writes,
  `GitHubApiException` on refusal, a `CancellationToken` on every method.
- **`app.json` parsing moves out of `ProjectBuildService`.** `ParseManifest`,
  `AppJsonManifest`, `AppJsonDependency` and the test-folder exclusion rules become a
  static `AppJsonManifestParser` (`Services/ObjectExplorer/AppJsonManifestParser.cs`);
  the build service delegates. #629 lifts it; #630 extends `AppJsonDependency` with the
  dependency `version` the current parser drops.
- **Translation-file rules move out of `GitHubTranslationService`.** "A `.xlf` whose
  parent folder is `Translations`, at any depth" and the language/source detection
  become a static `TranslationFileRules`; the Translator page and the ingest (#631) both
  use it.
- **Which GitHub permissions the App needs grows.** Phase 1 needed `administration`,
  `contents`, `metadata`, `pull_requests`, `members`. Phase 2 adds `checks: write`
  (#627) and uses `contents: write` for Releases (#632) and `administration: write` for
  rulesets (#628). `GitHubPermissionLabels` describes the new ones; the SiteAdmin
  walkthrough names them.

## #629 Discover AL repositories across the connected organisation

Named user: a BC consultant who has just connected their GitHub organisation and wants
to see which of its repositories the toolbox does not know about yet.

**Decisions**

- The existing "project discovery" (`ProjectDiscoveryQueue`, `oe_projects.discovered_*`)
  is *extension* discovery inside one solution and keeps its name. This feature is
  **repository discovery**: `RepositoryDiscoveryService`, `RepositoryDiscoveryScheduler`,
  table `github_repository_candidates`.
- A candidate row is per org: `full_name`, `html_url`, `clone_url`, `default_branch`,
  `app_name`, `app_id`, `app_json_path`, `discovered_at`, `last_seen_at`, `ignored_at`,
  `ignored_by_user_id`. A repository that stops matching (app.json removed, repository
  gone) is removed on the next sweep; an ignored one stays ignored until it is tracked.
- **The probe is one recursive tree read per repository** (`ListTreeAsync` at the
  default branch), looking for `app.json` at the root or exactly one folder down,
  skipping test folders by the same rules the build uses. The manifest is then read
  once for its name and id. No clone.
- **The sweep runs daily** (`RepositoryDiscoveryScheduler : PolledScheduler`, fixed hour,
  `DISABLE_GITHUB_REPOSITORY_DISCOVERY_SCHEDULER=1` opt-out) and **on demand** from the
  Solutions page ("Check GitHub now"), on the request thread behind a loading state.
  Both use the installation token: listing the organisation's repositories is an act of
  the organisation.
- **The panel narrows to the viewer.** "N AL repositories in {org} are not tracked yet"
  is computed *after* `GitHubAccessService.FilterAccessibleAsync` for the person
  looking, so a repository they cannot open is neither counted nor listed. Narrowing is
  per user by construction, so it happens at render time in the panel's own service
  scope, not in the sweep.
- **"Already tracked" is decided against every solution in the org**, not just the ones
  the viewer can see - otherwise a repository a Private solution already tracks would be
  offered as untracked and leak that solution's existence. The subtraction runs over
  `oe_project_repositories` under the ordinary tenant filter, matched on the normalised
  clone URL; the display list is what the GitHub check narrows.
- **Track as solution** is a compact inline form, not a bare click: the solution name
  (from `app.json`) and the artifact country (from the organisation's auto-import
  country when set) are pre-filled, and the person confirms. `ProjectService.CreateProjectAsync`
  requires a country, so a silent default would be a guess made for them. **Ignore**
  stamps `ignored_at` and the row disappears from the panel.
- The panel is an interactive child component above the list on `/solutions`
  (`ProjectsBrowser` stays static SSR). It renders nothing at all unless the
  organisation allows GitHub repositories, has connected one, and the viewer has linked
  their account - the same rule as #624's picker.
- No MCP tool: discovery proposes and a person decides; agents list repositories through
  #633's `list_repositories`.

**As built**

_(appended by the implementer)_

## #628 Repository standards when the toolbox creates a repository

Named user: an org Admin who wants every repository the toolbox creates to look like the
ones their team already maintains by hand.

**Decisions**

- **Per-organisation, two parts.** A **branch ruleset** for the default branch, stored as
  one JSON column `organization_settings.github_repository_ruleset_json`
  (`GitHubRepositoryRuleset`: require a pull request, required approvals, require linear
  history, required status checks, block force pushes), and a **set of files** added to
  every new repository, stored in a new table `github_repository_standard_files`
  (`path`, `content`, `ordering`, `updated_at`; unique `(organization_id, path)`). The
  files are plain text, not mustache: they are the same in every repository by
  definition.
- **Not `organization_files`.** That table's rows are template opt-ins the generator
  emits; standards are applied to a repository regardless of template and never appear
  in a ZIP. A third `OrganizationFileScope` would have muddled the "Always-included
  files" page for both audiences.
- **Applied after the workspace commit, as its own commit** ("Apply repository
  standards"), on the installation token, then the ruleset via
  `POST /repos/{owner}/{repo}/rulesets`. The second commit keeps "the files we
  generated" an honest description of the first, and it is not skipped by the one-file
  early return in `CommitAsync`. A standards file at a path the generator also produced
  replaces it - the organisation's standard wins over the template.
- **A ruleset refusal is a warning, not a failure.** By then the repository exists and is
  committed, so the success card says so and names what GitHub refused (typically the
  missing `administration: write` grant), rather than leaving a repository behind with
  a stack trace over it. Files are committed before the ruleset is attempted, so a
  ruleset that requires a pull request never blocks the standards commit.
- **Admin surface:** a "Repository standards" row on Administration -> Repositories,
  in the connected block after "What the toolbox may do there", linking to a dedicated
  editor page (`/admin/administration/repositories/standards`) modelled on
  `AdminTemplateFiles`. The row summarises what is configured ("3 files and a branch
  ruleset").
- **New Workspace shows one line**, not options: "Will be created with your
  organisation's repository standards" in the create-repository card's caption, only
  when anything is configured. The MCP `generate_workspace` result gains the same fact.
- Retro-fitting existing repositories stays out of scope, as the issue says.

**As built**

_(appended by the implementer)_

## #626 Cookbook: apply a recipe to a repository, and update every repository that took it

Named user: a consultant on a customer project who found the right recipe and wants it
in the customer's repository as a reviewable change, not a ZIP on their desktop.

**Decisions**

- **One operation, two callers.** `GitHubRecipeDeliveryService.ApplyAsync(recipeId,
  repoFullName, customerName?)` commits the recipe's files at their `RelativePath` onto
  `aldt/recipe-<slug>` **with the user's token** and opens a pull request into the default
  branch. The download modal calls it once; the admin "Open update pull requests" calls
  it once per repository that took the recipe. The pull request body says which it is.
- **Branch reuse, then stepping.** If `aldt/recipe-<slug>` has an open pull request, the
  commit is added to it (find-before-create, as the Translator does). If the branch
  exists with no open pull request (merged or closed), the name is stepped (`-2`, `-3`)
  as extension delivery does, so history is never rewritten.
- **Files are written over, never merged.** A repository that has since diverged in the
  recipe's files gets the pull request anyway; the diff shows the conflict. No three-way
  merge (the issue says so).
- **Attribution is a place.** `recipe_downloads` gains `repository` (the full name) and
  the `Source` enum gains `Repository`; `project_id` is still resolved from the customer
  name when one is given. "Where this recipe has been used" on the admin page shows the
  repository, and the distinct repositories are what "Open update pull requests" iterates.
- **The update button appears only when it can act**: the recipe has been applied to at
  least one repository and the Admin looking has a working GitHub link. It opens one
  pull request per repository, as the Admin, and reports per repository - one refusal
  does not stop the others.
- **`apply_recipe`** is the MCP twin, in `CookbookTools`, taking a recipe id, a
  repository as `owner/name` and an optional customer name, returning the pull request
  (`RepositoryDeliveryResult`). It routes through the same service, so the resolver gate
  is inherited. `.design/cookbook.md`'s "downloads are a web-UI flow only" sentence is
  corrected.

**As built**

- `GitHubRecipeDeliveryService.ApplyAsync(recipeId, repoFullName, customerName?)`
  returns `GitHubRecipeDelivery(Repository, PullRequest, IsNewPullRequest, FileCount)`.
  It needed no new REST call: every primitive the commit uses - branch head, commit
  tree, blob, tree, commit, create/update ref, find/create pull request - was already
  on `GitHubAppClient` from phase 1, so this feature adds no
  `GitHubAppClient.<Feature>.cs` file and leaves that class as it found it.
- **Branch choice is one read-only walk, not two rules.** `aldt/recipe-<slug>`,
  `-2`, `-3`... are tried in order and each is asked two questions: is a pull request
  open on it (join that one, and commit on the branch's own head), and does the branch
  exist at all (if not, cut it from the default branch head). The first name to answer
  either is the target. That is a superset of the spec's "reuse the base name, else
  step it", and it covers the case the literal reading gets wrong: once the base
  name's pull request is merged, a third apply would open a *third* pull request
  beside the still-open `-2` rather than joining it. Ten names is the cap, as
  extension delivery uses.
- **The tree's base is the target's own tree**, not always the default branch's -
  otherwise the second commit on a reused branch would silently revert the first.
  Files are still written over rather than merged, as the issue asks.
- **"Which it is" is on the commit message, not the pull-request body.** A body is
  only ever written when the pull request is opened, so by the time a second commit
  joins one the words are already there and could not be corrected; the body says
  what the pull request brings, and the commit says "Apply" or "Update".
- **The attribution row is written after the pull request exists.** A row for a
  commit that never landed would send the next round of fixes to a repository that
  never took the recipe, which is exactly the question the row is kept to answer.
- `recipe_downloads.repository` (text, 300, nullable) and `RecipeUseSource.Repository`,
  in migration `20260920000000_AddRecipeDownloadRepository`. No new index: the admin
  card reads one recipe's rows, which the existing
  `(organization_id, recipe_id, downloaded_at)` index already serves.
- **The zip-slip-safe path join and the title slug moved to
  `Services/Cookbook/RecipePaths.cs`.** A recipe now leaves the app two ways and both
  have to produce the same paths; `CookbookEndpoints.BuildSafeEntryPath` stays as a
  one-line delegate so the existing tests still name the rule where they found it.
- **The download modal gains an "or" section, not a second dialog.** It renders only
  when `GetAccessAsync` says Ready - somebody with no GitHub account connected is not
  told about a door they cannot open - and its button is an outline one, so the
  download stays the dialog's only primary action. The customer name above is shared
  by both paths. On success the dialog closes and the page carries the pull-request
  link, because that link is what the consultant sends on.
- **The admin card iterates in the page, not in a service.** "Open all" awaits one
  apply per repository in order and records each outcome against its row; a refusal is
  caught per repository so one archived repository does not stop the rest. No queue -
  this is a person pressing a button behind a loading state, like every other GitHub
  call a person triggers.
- `apply_recipe` returns the shared `RepositoryDeliveryResult`, which gained an
  `IsNewPullRequest` defaulting to true (a `generate_*` tool always opens a fresh pull
  request, so the default is right there and the DTO stays one shape).
- Tests: `GitHubRecipeDeliveryTests` (13), `ApplyRecipeToolTests` (5),
  `RecipeDownloadTests` (+8 for the column and the distinct list), and two bUnit files
  - `RecipeDetailRepositoryTests` and `AdminRecipeUpdateRepositoriesTests` - which are
  this feature's rendered evidence, there being no browser in the build environment.

## #632 Publish build artifacts as GitHub Releases, and deliver from them

Named user: a consultant whose customer wants every shipped `.app` on the repository's
Releases page, and who sometimes has to redeploy a version the toolbox did not build.

**Decisions**

- **Publish is a build-pipeline option that names a repository.** `oe_pipelines` gains
  `github_release_repository_id` (nullable FK to `oe_project_repositories`); non-null
  means "publish every successful build there". The editor offers the solution's GitHub
  repositories inside the connected organisation, so a solution with several
  repositories says which one gets the Release.
- **The tag is `v<version>`** where the version is the built app's `app.json` version. A
  build with several artifacts publishes when they all carry the same version and is
  otherwise recorded as "not published: the apps have different versions" - the build
  itself still succeeds. A publish failure of any kind is a build log section and a note
  on the build page, never a failed build: the `.app` exists and downloads regardless.
- **Idempotent on re-run.** An existing Release at the tag has its assets replaced
  (delete then upload) and its body updated; the tag is never moved. Installation token
  throughout - a Release is an act of the organisation, and there may be no user (a
  webhook build).
- **Deliver from a Release is a release-pipeline source.** `oe_release_pipelines` gains
  `artifact_source` (`build` | `github_release`) and `github_release_repository_id`;
  `build_pipeline_id` becomes nullable and is required exactly when the source is
  `build`. Choosing a tag downloads the `.app` assets and **stages them as a
  `ProjectBuild` row** (status `ready`, no pipeline, `github_release_tag` set) with
  `ProjectBuildArtifact` rows, so `DeliveryService` and every downstream reader work
  unchanged. `ScheduleDeliveryAsync` accepts a staged build for a Release-sourced
  pipeline in place of the build-pipeline check.
- **Asset downloads follow the redirect by hand.** The typed client does not follow
  redirects (a 302 is an answer elsewhere), so the asset call reads `Location` and fetches
  the storage URL without the Authorization header.
- **Refusals are plain.** A "restrict tag creation" rule surfaces as GitHub's own message
  on the build; a Release whose assets are not `.app` files is refused at staging time.
- **MCP:** `BuildRow` gains `GitHubReleaseTag`/`GitHubReleaseUrl`; a new
  `stage_github_release(releasePipelineId, tag)` returns the staged build so
  `publish_build` can take it; `list_github_releases(releasePipelineId)` lists the tags.
  `.design/saas-delivery.md`'s "the artifact source is this build pipeline's builds" is
  widened accordingly.

**As built**

- **Publishing hangs off the build worker, not off the build service.**
  `ReleaseImportWorker` calls `GitHubReleaseService.PublishBuildAsync` immediately after
  `MarkBuildReadyAsync`, inside the same DI scope and ambient organisation identity. That
  is the one point where a build is final *and* nothing downstream is waiting, so a
  publish can only add to it. The call is wrapped twice - the service catches GitHub's
  refusals itself, the worker catches everything else - because "a publish failure is
  never a build failure" has to hold for a bug in the publisher too.
- **The outcome lives in three places, and each is for a different reader.**
  `oe_project_builds.github_release_tag` / `_url` / `_error` are what the build card
  renders ("Published as v1.2.3.0", linked, or "Not published to GitHub: ..."); a
  `oe_project_build_logs` row headed "GitHub Release" is what the raw log download shows;
  the returned `GitHubReleasePublishResult` is what a caller acts on. The log row is
  appended after `PersistLogsAsync` has run rather than fed through it, so publishing does
  not have to be part of the build service's log lifecycle.
- **The tag doubles as the staged-build marker, and `pipeline_id is null` is the other
  half of it.** A build with a tag and a pipeline was compiled here and published there; a
  build with a tag and no pipeline was downloaded from a Release. `ScheduleDeliveryAsync`
  distinguishes them on exactly that pair, so nothing new had to be added to say which
  kind of build a delivery is publishing.
- **Staging is idempotent on `(tag, release URL)`, not on the tag alone.** Two release
  pipelines in one solution can draw from different repositories, and both their `v1.0.0.0`
  releases are real; matching the URL as well keeps "which one did we deploy" a question
  with one answer without inventing a repository column on the build.
- **The `.app`'s manifest is read directly, not through `AppPackageReader`.** Staging wants
  a name and a version; the full reader parses every symbol in the package, which on a
  base-app-sized `.app` is minutes of work for two strings. `GitHubReleaseService` opens the
  zip behind the 40-byte NAVX header, reads `NavxManifest.xml`, and falls back to the
  `Publisher_Name_Version.app` file-name convention and then to the bare file name. No new
  package.
- **The publish path never asks `GitHubRepositoryService.ResolveAsync`.** That resolver is
  the *user*-credential gate, and a build worker has no user - a webhook build will have
  none at all. The repository is instead pinned to the pipeline's own
  `oe_project_repositories` row (which only a manager of that solution can set), checked to
  be on GitHub, and checked to sit inside the connected organisation's login before any
  call is made. The two agent-facing calls, which do run as a person, go through
  `ProjectAccess.EnsureCanManageAsync` on the owning solution.
- **The editors show nothing they cannot act on.** Both the publish select and the
  release-pipeline source choice are rendered only when
  `GitHubReleaseService.ListRepositoryOptionsAsync` returns something, which needs a
  connected GitHub organisation *and* a repository of this solution inside it. An
  organisation on Azure DevOps sees the two dialogs exactly as they were - the same rule
  #624 settled for the picker. A release pipeline with no build pipeline is no longer a
  dead end either: it now offers the GitHub source instead of only pointing at the
  Pipelines tab.
- **`build_pipeline_id` became nullable rather than gaining a sentinel**, and
  `ReleasePipelineService.ValidateAsync` writes exactly one of the two source columns -
  switching an existing pipeline to Releases clears the build pipeline it used to draw
  from. `artifact_source` is backfilled to `build` by the migration, so every pipeline that
  existed before this reads as what it already was.
- **Asset uploads and downloads are the two calls that leave `api.github.com`.** The upload
  goes to the `upload_url` GitHub hands back (on `uploads.github.com`, with the
  `{?name,label}` template stripped and the name passed as a query parameter, body as raw
  bytes); the download follows the 302 to `objects.githubusercontent.com` by hand and
  fetches it **with no Authorization header**, since the storage URL is already signed and
  handing it our installation token would be giving a credential to a service that never
  asked. A test asserts that call carries no credential.
- **`contents: write` is what a Release needs**, and `GitHubPermissionLabels` now says so in
  words ("Read and write files, and publish releases, in repositories") rather than adding a
  permission name to the tab.
- Deliberately left out: publishing a build to several repositories, a per-pipeline tag
  template (the tag is `v<version>`), draft or pre-release Releases, and any retro-fitting
  of Releases for builds that finished before this shipped.

## #627 Compile a pull-request branch and post the result as a check run

Named user: a team with no CI of its own that wants "does this still compile?" answered
on every pull request, inline in the Files tab.

**Decisions**

- **One deployment-wide webhook secret** on `system_settings`
  (`github_webhook_secret_encrypted`), entered on `/site-admin/settings/github` beside
  the client secret, with the **Webhook URL** shown read-only from `PublicOrigin` (falling
  back to the request host when unset) so the operator copies one address. Redacted in
  audit; blank keeps, a clear flag wipes.
- **`POST /github/webhook`** is anonymous, antiforgery-disabled, rate-limited, size-capped
  (1 MB), verifies `X-Hub-Signature-256` with a constant-time compare over the raw body,
  and writes a body on every response so the status-pages middleware does not rewrite a
  401 into a 400. It is on the maintenance-mode allow-list: accepting a delivery is
  enqueueing, and GitHub disables hooks that keep failing. `ping` answers 200.
  `pull_request` with action `opened`, `synchronize` or `reopened` enqueues; everything
  else is 204.
- **The org is resolved from `installation.id` inside the worker**, by the per-org
  ambient loop (no `IgnoreQueryFilters()`). The repository is matched to
  `oe_project_repositories` rows by normalised clone URL under that org's filter; every
  solution that tracks it gets a build and a check run named
  "AL Dev Toolbox / {solution}".
- **The clone uses the installation token** (`x-access-token:<token>` in the same
  `http.extraHeader` shape as a PAT). A webhook has no user, and the check runs as the
  App - the honest model the issue names. Manual builds keep the user's PAT; nothing
  about them changes.
- **A PR build is a `ProjectBuild`** with `trigger = pull_request`, `Branch` = head ref,
  `pull_request_number`, `head_sha`, `check_run_id`, no pipeline and no user, all apps
  selected. It rides the existing `ReleaseImportQueue` through a new
  `ReleaseImportSource.PullRequestBuild` case so its symbols resolve and its Release
  ingests exactly as a manual build's do.
- **Supersession.** A `GitHubWebhookQueue` keyed on `(installation, repository, PR)`
  records the latest head SHA per key; a job whose SHA is no longer the latest is skipped
  when dequeued, and an in-flight build for the same key is cancelled through a linked
  token the compile loop already honours. One build per PR head at a time.
- **Diagnostics become rows.** `alc`'s `path(line,col): severity CODE: message` lines
  are parsed into `oe_project_build_diagnostics` (build, repository, path, line, column,
  severity, code, message) for every build, PR or manual. The check run posts them as
  annotations in batches of 50 (GitHub's cap per request), and the build page shows the
  count.
- **Check run lifecycle:** `in_progress` when the build starts, `completed` with
  `success` / `failure` (any compile error) / `neutral` (build could not run, e.g. no
  symbols), and a summary naming the apps compiled. `checks: write` is the new App
  permission this needs.
- **No MCP surface.** There is no user.

**As built**

_(appended by the implementer)_

## #631 Translation memory from every .xlf in the organisation's repositories

Named user: a translator who wants the organisation's past translations to surface as
suggestions without anyone uploading anything.

**Decisions**

- **Scope is tracked repositories inside the connected GitHub organisation**, not the
  whole installation. Tracking a repository as a solution is the organisation's own act
  and is the gate an installation-token read needs.
- **One recursive tree read per repository**, filtered by `TranslationFileRules`; only
  blobs whose SHA differs from the last ingested one are read. A new table
  `translation_memory_sources` (`repository`, `path`, `blob_sha`, `last_ingested_at`,
  `unit_count`) carries that state. Files GitHub will not inline are read through a new
  `GetBlobAsync` (raw blob by SHA); a truncated tree is logged and skipped.
- **Attribution is two new nullable columns** on `translation_memory`:
  `source_repository` and `source_path`, plus `Origin` set to "{repository} / {folder}".
  The unique pair index is unchanged, so a pair seen in two files keeps the most recent
  file's attribution - "where did this come from" has one answer, and it is a real one.
- **The ingest runs daily** (`TranslationMemoryIngestScheduler : PolledScheduler`,
  `DISABLE_TRANSLATION_MEMORY_INGEST_SCHEDULER=1`), per org through the ambient loop,
  and **on demand** from the admin Translation memory page ("Refresh from
  repositories") behind a loading state. It feeds `TranslationMemoryService.UpsertAsync`
  exactly as an import does and never fails the caller.
- **Suggestions say where a match came from**: the chip's caption becomes
  "from {repository} / {folder}" with a link to the file on GitHub when the entry has a
  source. `MemoryEntryView` and the MCP `TranslationMemoryHit` gain the two fields.

**As built**

_(appended by the implementer)_

## #630 Dependency drift: pull requests bumping app.json when a new release is imported

Named user: a consultant who wants to know which customer repositories still target last
year's Business Central after a new release lands in the Object Explorer.

**Decisions**

- **The scan runs when a first-party release reaches `ready`** (the completion point in
  `ReleaseImportService.ProcessReleaseAsync`) and on demand. It reads every tracked GitHub
  repository's `app.json` files through the API on the installation token (the same
  read discovery does), parses them with `AppJsonManifestParser` (extended so dependency
  versions survive), and compares `application` / `platform` against the release and
  `dependencies[*].version` against the well-known-dependency defaults.
- **Drift is a per-org table**, `github_repository_drift` (repository, path, field,
  current, proposed, release id, detected_at), so the Solutions page can say "12
  repositories still on 27.x" from the database.
- **Open update pull requests** edits the `app.json` in place (only the fields that
  moved, preserving formatting elsewhere) on `aldt/bump-bc-<major.minor>` **with the
  user's token**, one pull request per repository, reused while open. The body says what
  moved and links to the release's compare view (`ObjectExplorerLinks.ReleaseCompare`).
- **Only repositories whose `application` range excludes the new version are proposed**
  by default; the "optional" in the issue is the default because the alternative
  proposes a pull request that changes nothing.
- The check-run combination (#627) comes for free: the pull request is a pull request.

**As built**

_(appended by the implementer)_

## #633 MCP parity for the GitHub workflows

**Decisions**

- `list_repositories` wraps `GitHubRepositoryService.ListAccessibleAsync` and reports the
  readiness reason when the list is empty for a fixable reason ("link your GitHub
  account"), so an agent can tell the person what to do.
- `create_repository` and `add_extension_to_repository` are the standalone twins of the
  options on `generate_workspace` / `generate_extension`: same plan input, same service
  call, same result shape. They exist so an agent can find them by name; the options
  stay.
- `list_translation_files(repository)` and `open_translation_pr(repository, path,
  targetLanguage, edits[], summary)` cover the Translator. The agent supplies per-unit
  edits, applied with `XliffTargetWriter.ApplyEdits` against the file the tool has just
  read, so every other byte is unchanged and the SHA the write quotes is the one it read.
- `apply_recipe` ships with #626.
- Every tool acts as the calling user, describes what it will create on GitHub before it
  does it, and is in `McpToolCatalog`.

**As built**

_(appended by the implementer)_

## Fences

- **Tenant isolation.** Every new table and column is per-org or per-user. No new
  `IgnoreQueryFilters()` call site; see "What phase 2 changes" above for the one ask.
- **Secrets.** One new key-ring-encrypted secret (the webhook secret), redacted in audit.
- **No new external dependency.** Releases, check runs, rulesets and webhooks are all
  hand-rolled REST on `HttpClient`, and the HMAC is `System.Security.Cryptography`.
- **Background work stays in-process** on the existing worker bases.
- **Network.** `api.github.com`, `github.com` and now `uploads.github.com` and
  `objects.githubusercontent.com` (Release assets) outbound; one inbound route.

## Out of scope

Retro-fitting standards onto existing repositories, auto-creating solutions without a
click, three-way merges of recipe files, multi-file translation batches, editing an
existing extension in a repository, and any MCP surface for the webhook flow.
