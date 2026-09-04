# GitHub integration

Milestone "GitHub integration" (issues #620-#625). The toolbox gains a GitHub App
installed on the customer's GitHub organisation, a per-user account link, and four
features that use them: creating a repository from New Workspace, adding an extension
to an existing repository, assisted repository entry on solution pipelines, and the
Translator's repository round trip.

This document is the spec for all six issues. Where it disagrees with an issue body,
this document wins and the deviation is called out below.

## Why a GitHub App and not a PAT

The toolbox already stores per-user GitHub PATs (`UserRepositoryToken`, Account →
Repository tokens) and clones solution repositories with them. That is the right
credential for "clone what I can see" and the wrong one for "create a repository in
our organisation": repository creation is an act of the *organisation*, and no
individual's PAT should have to carry `admin:org` for the toolbox to work.

So there are two credentials, and which one acts is a security decision, not a
convenience one:

| Act | Credential | Why |
| --- | --- | --- |
| List the org's repositories, read org/team membership, create a repository | **Installation token** (the App, acting as the org) | Org-level authority the App was granted at install time |
| Commit, open a pull request, write a file into an existing repository | **The acting user's linked token** | GitHub enforces that user's own repository permissions natively, and a PR authored by the user can't be approved by the same person who wrote it via a bot identity |

The consequence is that a write never needs a separate "may this user write here?"
gate that we could get wrong: the write is attempted as the user and GitHub answers.
Reads through the installation token *do* need a gate, because the installation can
see every repository the App was installed on - that gate is
`GitHubAccessService.CanAccessRepoAsync`.

## Configuration: three layers

1. **Deployment (SiteAdmin).** One GitHub App registered per toolbox deployment:
   app id, client id, client secret, private key (PEM). Lives on the
   `system_settings` singleton, secrets encrypted with the Data Protection key ring
   exactly like `SmtpPasswordEncrypted` and `EntraClientSecretEncrypted`, and
   redacted by `AuditInterceptor`. Losing the `app-keys` volume means re-entering
   them. Page: `/site-admin/settings/github`, a new tab on `SiteAdminSettingsPage`.
2. **Organisation (Admin).** An org Admin connects the toolbox organisation to one
   GitHub organisation: the install/authorise handshake returns an
   `installation_id`, stored on `organization_settings` with the org login and the
   permissions the installation was granted. Page: the existing Administration →
   **Repositories** tab. The installation must sit on a GitHub *organisation*: a
   personal-account install can neither create repositories for a team nor answer
   "is this person in the organisation", so the callback refuses it with a message
   saying where to install instead, rather than storing a connection that half-works.
3. **User (any member).** Each member links their own GitHub account from Account →
   Repository tokens, storing a user-to-server access token and refresh token
   encrypted per user.

### Deviation from #620: no new "GitHub" admin tab

#620 proposes Administration → a new **GitHub** tab. This document puts the org
connection on the existing **Repositories** tab instead. That tab is already
"per-organisation source-control settings" (it holds the allowed-providers choice and
points members at Account → Repository tokens) and today carries a single setting row.
A ninth admin tab whose subject is a subset of an existing tab's subject is worse for
the named user - an admin looking for "where do I connect our GitHub org" will look
under Repositories, not next to Teams and Export.

## Schema

All additions are per-org or per-user; none crosses the tenant fence. The
install handshake (#620) added no `IgnoreQueryFilters()` call at all: both of its
routes run inside the Admin's existing session, so the acting organisation comes
from the caller's own cookie and the normal query filter applies. If the per-user
link (#621) turns out to need a pre-auth lookup, that call site is category 1
(pre-auth routing) and carries the required justification comment.

### `system_settings` (singleton, SiteAdmin-managed)

| Column | Meaning |
| --- | --- |
| `github_app_id` | Numeric App id from the App's settings page. Null until configured. |
| `github_app_slug` | The App's URL slug, used to build the install link (`https://github.com/apps/{slug}/installations/new`). |
| `github_client_id` | OAuth client id for the user-to-server flow (#621). |
| `github_client_secret_encrypted` | Key-ring encrypted. Redacted in audit. |
| `github_private_key_encrypted` | Key-ring encrypted PEM. Redacted in audit. Used only to sign the App JWT. |

### `organization_settings`

| Column | Meaning |
| --- | --- |
| `github_installation_id` | The installation the org connected. Null = not connected; doubles as the feature's master switch, matching how `MachineTranslationTrigger.Off` and `AutoImportReleasesEnabled` work. |
| `github_org_login` | The GitHub organisation login, for display and for `POST /orgs/{login}/repos`. |
| `github_installation_permissions` | JSON blob of the permissions GitHub reported at connect time, so the Repositories tab can say "this installation cannot create repositories" before someone hits it from New Workspace. |
| `github_connected_at` | When the connection was made. Shown on the tab. |

### `user_external_logins`

Reused, not duplicated. Rows for this milestone stamp `provider = "github"`; the
existing Entra rows keep `provider = "entra"`. The `Issuer` column, meaningless for
GitHub, stores the constant `"github.com"` so the `(provider, issuer, subject)` unique
index keeps its shape. `Subject` is the GitHub user id (numeric, stable - never the
login, which is renameable). `DisplayIdentity` is the login, for display only.

Three columns are added for the user-to-server token, all nullable because Entra rows
do not use them:

| Column | Meaning |
| --- | --- |
| `access_token_encrypted` | Key-ring encrypted user-to-server token. Expires in 8h. |
| `refresh_token_encrypted` | Key-ring encrypted refresh token. Expires in 6 months. |
| `access_token_expires_at` | UTC expiry, so refresh happens before a call rather than after a 401. |

All three are redacted by `AuditInterceptor`. A GitHub App whose "expire user
authorization tokens" option is off returns neither an expiry nor a refresh token; the
columns stay null and nothing is refreshed, which is a supported configuration rather
than a broken link.

Because two providers now share this table, every query that means *"this person can
sign in with Microsoft"* filters on `provider = 'entra'`: strong-auth checks, the
Microsoft-only local-login policy guard, the Users tab badge, and the /account link
list. A GitHub row authorises; it signs nobody in.

`is_org_member` (bool, nullable) records whether the user was a member of the
connected GitHub org the last time we asked - at link time, and again whenever
`IsOrgMemberAsync` runs or they press Check again on the Account row - so "why can't I
see any repositories" has an answer there. Null means we never established it (no
connected org, or GitHub would not say), which is deliberately not the same as a no.

## `GitHubAppClient`

One service, hand-rolled on `HttpClient`, registered as a typed client. No Octokit -
the surface we need is a dozen REST calls and Octokit would bring an object model we
would immediately wrap.

- **App JWT.** RS256 over `{iat, exp, iss}` signed with the stored PEM, using
  `System.Security.Cryptography.RSA.ImportFromPem` and base64url encoding. Roughly
  twenty lines; no new package reference. Lifetime 9 minutes (GitHub's limit is 10),
  with `iat` backdated a minute for clock drift. It lives in `GitHubAppJwt`, apart
  from the client, because that is the piece worth testing on its own.
- **Installation token.** `POST /app/installations/{id}/access_tokens` with the JWT.
  Cached in memory per installation id until 5 minutes before expiry.
- **User token refresh.** `POST https://github.com/login/oauth/access_token` with the
  stored refresh token when the access token is within 5 minutes of expiry. The
  refreshed pair is written back encrypted. Transparent to callers.
- **Calls.** `GET /installation/repositories`, `GET /repos/{owner}/{repo}`,
  `GET /repos/{owner}/{repo}/contents/{path}`, `PUT` the same,
  `POST /orgs/{org}/repos`, the Git Data API (`/git/blobs`, `/git/trees`,
  `/git/commits`, `/git/refs`), `POST /repos/{owner}/{repo}/pulls`,
  `GET /orgs/{org}/members/{username}`.
- **Errors.** One `GitHubApiException` carrying status and GitHub's `message`, so
  pages can render "a repository with that name already exists" rather than "500".
  Rate-limit headers are logged, not surfaced.
- Every method takes a `CancellationToken`. Structured logging with named
  placeholders on each outcome.

## `GitHubAccessService`

The per-user half. Owns the link lifecycle (link, unlink, read the current user's
link), transparent token refresh, and:

- `CanAccessRepoAsync(userId, repoFullName, ct)` - `GET /repos/{owner}/{repo}` with
  that user's token. 200 means visible; 404 means not (GitHub returns 404, not 403,
  for repositories you cannot see - do not treat 404 as "gone").
- `IsOrgMemberAsync(userId, ct)` - membership in the connected org.
- `FilterAccessibleAsync(userId, repos, ct)` - used by the repo picker so the list
  the installation returns is narrowed to what the user can actually see.
- `CanAdministerInstallationAsync(userId, installationId, ct)` - the install gate; see
  "Binding the installation to the acting user" below.
- `ResolveUserTokenAsync(userId, ct)` - the decrypted, in-date token for the features
  in #623 and #625 that commit and open pull requests *as the user*. Null when there is
  no usable link, which those features render as "connect your GitHub account first"
  rather than as a failure.

Every one of these returns "no" when GitHub could not be asked. Nothing here promotes
an unanswered question into permission.

Results are remembered for thirty seconds, not for the life of the service. The rule
being served is "a permission revoked on GitHub takes effect on the next page load, not
on the next deploy", and a Blazor Server scope is a *circuit* that can outlive a working
day - so an instance-lifetime cache would be exactly the staleness the rule bans. A short
window collapses the burst of questions one render asks (the picker checks every row)
while guaranteeing the answer is re-asked well before anyone reloads.

## Endpoints

`Endpoints/GitHubAppEndpoints.cs`, modelled on `EntraAuthEndpoints`:

| Route | Purpose |
| --- | --- |
| `POST /admin/github/connect` | Admin-only. Redirects to the App's install page with `state`. |
| `GET /github/setup` | Install callback. Exchanges `installation_id`, writes it to the acting org. |
| `POST /account/github/link` | Starts the user-to-server OAuth handshake. |
| `GET /signin-github` | User OAuth callback. Exchanges the code, stores the token pair. |
| `POST /account/github/unlink` | Deletes the link row. |

Both callbacks validate a signed, single-use `state` carrying the org id (and user id
for the link flow) before touching anything. Antiforgery on every POST via
`ValidateAntiforgeryAsync`, as the existing endpoints do.

Both `state` values are Data-Protection ciphertext over `orgId|userId|nonce|issuedAt`,
paired with a memory-cache entry keyed on the nonce that the callback consumes - so a
state is good once, for fifteen minutes, and only in the session that started it. The
two handshakes use different Data Protection purpose strings, so a state minted for one
cannot be spent on the other. Both install routes require the `Admin` role, which is
also what lets them read the acting org through the normal query filter; the link
routes need only an authenticated user, because the link is about that one person.

Neither handshake sends a `redirect_uri`. GitHub then uses the App's own registered
Setup URL and Callback URL, both shown read-only on `/site-admin/settings/github` for
the SiteAdmin to copy across - one address written down once, rather than two that can
drift apart behind a reverse proxy.

**This is not a sign-in provider.** No cookie is issued by any of these routes, no
`User` row is created, and `AuthService` is not touched. Microsoft Entra ID remains
the one federated sign-in.

### Binding the installation to the acting user (closed in #621)

`state` proves *who started* the handshake. It does not prove *which installation*
came back, and the App JWT is authorised for every installation of the App - so an
Admin who starts Connect legitimately could hand-edit the redirect to
`/github/setup?state=<their own valid state>&installation_id=<someone else's>` and the
call would succeed. Installation ids are small sequential integers, so guessing one is
not work.

#620 shipped two partial mitigations, and neither was the fix:

- The callback refuses an installation that is not on a GitHub organisation, which
  removes the personal-account half of the space.
- `GitHubConnectionService.ConnectAsync` refuses an installation id already held by a
  different toolbox organisation (a category-6 existence-only probe). That makes the
  attack first-come-first-served rather than free, and it makes the collision visible
  to the org that loses - but a customer who has not connected yet is still claimable.

**The gate shipped in #621.** `ConnectAsync` now asks
`GitHubAccessService.CanAdministerInstallationAsync(userId, installationId)`, which
reads `GET /user/installations` with the acting Admin's *own* linked token - the only
credential that can answer whose installation this is - and refuses anything the list
does not contain. Its four refusals are distinct and all render as field-keyed errors
on the Repositories tab: the Admin has not linked a GitHub account, their link no
longer works, GitHub does not list them as an administrator of that installation, or
GitHub could not be asked at all. **An answer we could not get is a refusal, never a
pass.**

The consequence is deliberate: connecting a GitHub organisation now requires the Admin
to have linked their own GitHub account first. That is a precondition, so the
Repositories tab states it *before* the round trip - while GitHub is set up for the
deployment but the Admin is unlinked, the Connect button is replaced by the reason and
a link to Account -> Repository access. Sending them to GitHub and refusing them on the
way back would be the same rule with worse manners.

## The shared repository picker

`Components/Shared/RepositoryPicker.razor`, built in #623 and reused by #624 and
#625. Typeahead over `GET /installation/repositories` (installation token), filtered
through `GitHubAccessService.FilterAccessibleAsync`, debounced client-side. Renders
the three states every list in this app renders:

- **Not connected** - "No GitHub organisation is connected yet." plus a link to the
  Repositories tab for admins, and plain text for everyone else.
- **Not linked** - "Link your GitHub account to see the repositories you can access,"
  with a link to Account.
- **Connected, linked, no results** - "No repositories match" / "You do not have
  access to any repositories in this organisation."

As built (#623) there are two more, because both are real and neither is answered
by the three above: the *deployment* has no GitHub App at all (only whoever runs
the server can fix that, so no button is offered), and the user's link exists but
its credentials no longer work (connect again). All five come from
`GitHubRepositoryService.GetAccessAsync`, which reads the database and never calls
GitHub - so the picker renders its guidance while GitHub is down. A sixth state
belongs to GitHub itself being unreachable when the list is fetched: that one
offers a retry rather than failing the page around it.

The list is fetched when the user first reaches for the control, not on page load.
Narrowing through `FilterAccessibleAsync` costs one call to GitHub per repository,
and most visits to a page carrying the picker never open it.

The picker holds one repository at a time, which is what a form with a repository
field wants. A page that adds several in a row - #624's solution repositories -
turns on two optional modes rather than working around the single-selection
shape: `IsAlreadyAdded` marks the repositories the caller already has and makes
those rows unclickable, so a duplicate is impossible rather than refused after
the click, and `ClearAfterSelect` hands the pick over and puts the search box
straight back. Both default to today's behaviour; nothing else about the
parameter surface changes once a second caller is compiling against it.

## Feature flows

### #622 New workspace → create the repository

Generation is unchanged: in memory, synchronous. After the file set exists, one
commit is built through the Git Data API (blobs → tree → commit → the
`refs/heads/{default branch}` ref) into a repository created with the
installation token via `POST /orgs/{org}/repos`. Ordered, in-process, behind the
Generate button's existing loading state. No queue.

Before creating, the user must be a member of the connected GitHub org
(`IsOrgMemberAsync`). Failure modes rendered inline next to the field, not as a
generic error: name already taken, the installation lacks `administration:write`,
the user is not an org member.

The created repository is recorded in the audit log against the generation.

Four details settled while building it (`GitHubWorkspaceRepositoryService`).

**The first commit rides the installation token too**, which is the one place a
write does not go out as the user. The credential table above is about writing
into a repository that already exists, where GitHub enforcing the user's own
permissions is exactly what we want. This repository is seconds old and was
created by the app: depending on the organisation's base permission the person
who asked for it may have no access to it yet, so asking with their token would
fail for a reason they could do nothing about. The commit is instead *credited*
to them - author name and their `users.noreply.github.com` address, taken from
their link - so the history still says who asked. The membership check is what
their own token is for.

**The workspace's folder comes off the paths.** The ZIP nests everything under
the workspace folder because that folder is what a user unzips; a repository
*is* that folder, so `CRONUSCustomer/app.json` is committed as `app.json`.
`workspace.aldt.toml` is among them, which is what lets #623 hydrate from the
repository later.

**Nothing is created until every refusal has been ruled out** - plan, name,
readiness, membership, and the recorded installation permissions - so a
generator failure or a name GitHub would rewrite never leaves an empty
repository behind. The one refusal that cannot be pre-empted is the name being
taken, which GitHub reports as a 422 whose `message` says only that creation
failed and whose `errors[]` carries the reason.

**The organisation is not a parameter.** Callers name a repository, never an
owner; the organisation is the connected one. That is how the MCP tool inherits
the gate - there is nothing in `generate_workspace` for an agent to aim
somewhere else, and `create_repository` with a slash in it is refused as a name
GitHub would not keep rather than quietly re-aimed.

### #623 New extension → add to an existing repository

Two halves, both optional:

- **Hydrate.** Picking a repository fetches `workspace.aldt.toml` from its root and
  hydrates the form exactly as the existing upload path does. A repository without
  the file gets a plain message and the form stays manual - not an error.
- **Deliver.** "Add to repository" commits the new extension folder (and the
  `.code-workspace` folder entry, when the template maintains one) onto
  `aldt/add-<extension>` **with the user's token**, then opens a pull request. Never
  a push to the default branch, even when it is unprotected. The success state links
  to the PR.

Three details settled while building it. The commit carries the same file set the
ZIP does - literally, by reading the generated archive back - **minus** the
workspace-root files a template opts into (a `.gitignore`, a README stub, the
shared ruleset): a repository already has a root of its own, which is the same
reason sibling mode leaves them out. A folder that already exists in the
repository is refused rather than overwritten, since a tree write would silently
replace whatever is in it. And a branch name already taken is stepped
(`-2`, `-3`, ...) rather than moved: the first attempt's pull request may be
under review.

`GitHubRepositoryService.ResolveAsync` is the gate both callers go through. It
refuses anything outside the connected GitHub organisation - the picker offers
nothing else, so neither does the MCP tool - and then asks GitHub, with the
user's own token, whether they can open it.

### #624 Pipelines → assisted repository entry

The repository field on the solution pipeline editor gains the picker. Selecting a
repository fills the clone URL and display name and suggests GitHub's default branch.
Free text remains available and unchanged for Azure DevOps and for GitHub
repositories outside the connected org.

The clone itself keeps using the user's PAT (`UserRepositoryTokenService`). Moving
solution builds onto the linked token changes who a build fails for and is a separate,
later decision.

### #625 Translator → open from and save to a repository

- **Open.** Picker, then the `*.xlf` files found one level under `Translations/`.
  The `.g.xlf` source file is recognised and offered as the base. Loads exactly as an
  upload does. The blob SHA is remembered on the session.
- **Save.** One Contents API `PUT` with the remembered SHA, onto
  `aldt/translate-<lang>`, with the user's token. The branch is reused while its PR
  is open (found by head), so repeated saves add commits to one PR rather than opening
  five. A 409 is the conflict signal and surfaces as "this file changed in the
  repository since you opened it" with a re-open - never a silent overwrite.

## MCP parity

- `generate_workspace` gains the create-repository option (#622). It returns the
  repository *and* the ZIP whose files are in it, for the same reason
  `generate_extension` does.
- `generate_extension` gains the add-to-repository option (#623). It returns the
  pull request *and* the ZIP that went into it - generating a second time would
  mint different extension GUIDs, so a download offered beside the pull request
  has to be those same bytes.
- Both resolve the repository through the same service the pages use, so the access
  gate is inherited rather than re-implemented in the tool class - see the resolver
  rule in PROJECT.md.
- The Translator and pipeline-editor changes are UI assistance over data agents
  already reach; no new tool.

## Fences

- **Tenant isolation.** Every new column is per-org or per-user. Neither the install
  handshake nor the per-user link adds an `IgnoreQueryFilters()` call: all four routes
  run inside the caller's own authenticated session, so the acting organisation and user
  come from their cookie and the normal query filter applies. The milestone's only new
  call site is #620's category-6 uniqueness probe.
- **Secrets.** Three new key-ring-encrypted secrets (client secret, private key, and
  the per-user token pair). Same pattern as SMTP and the machine-translation key, all
  redacted in audit. Approved for this milestone; the PR body says so.
- **No new external dependency.** No Octokit, no JWT package - the App JWT is signed
  with `System.Security.Cryptography`.
- **No queue.** Every GitHub call in this milestone runs on the request thread inside
  an existing loading state.
- **Network.** Outbound `api.github.com` and `github.com` are already documented
  requirements for solution builds.

## Out of scope

Webhooks (nothing here needs GitHub to call us), branch protection, CODEOWNERS, CI
workflow files, editing an existing extension in a repository, multi-file translation
batches, and auto-creating a pipeline per repository. Phase 2 (#626-#633) covers the
next layer.
