# Artifacts: per-project builds and downloadable `.app` deliverables

This document specifies the **Artifacts** tool and the navigation/entity rework around it. It
promotes the Object Explorer's compile-from-source path (`object-explorer-project-builds.md`) into
a first-class, end-user-facing surface: point a **Project** at one or more Git repositories, build
it (per user, per commit), and get the compiled `.app` files back — GitHub-Releases style, keyed by
commit hash. The compiled output still flows through `ReleaseImportService.ProcessReleaseAsync` so
its objects remain navigable in the Object Explorer; what changes is *who* owns the build, *how*
it's credentialed, and *where* it surfaces.

**Status:** implemented on the `feat/artifacts` branch across four slices (per-user tokens; the
`ProjectBuild` model; the Projects/Artifacts tools + nav rework + OE split; MCP parity + the
existing-data backfill). This doc is the behavioural contract. Where it diverges from
`object-explorer-project-builds.md`, this doc wins and that doc is updated to describe the
post-split OE (symbol navigation only).

**Update (post-launch):** the **Artifacts** tool was renamed to **Pipelines** (the artifact is the
*product* of a build, not the action), and building moved fully onto that tool: a **New build**
action there discovers the project's extensions live and lets the user pick which to compile. The
Projects tool is now setup-only — it no longer triggers builds. The `Project`/`ProjectBuild`
entities, the `/artifacts/build/...` download endpoints, `ArtifactService`, and the `ArtifactsTools`
MCP surface keep their names ("artifact" still names the downloadable `.app`). Page routes are
`/pipelines` and `/pipelines/{projectId}`, with the old `/artifacts*` routes kept as aliases.
Details inline below.

## Why

The compile-from-source pipeline shipped inside the Object Explorer admin surface: a single
per-org PAT, projects managed under `/admin/object-explorer`, and each build landing as a
`project`-kind Release listed alongside Microsoft and third-party imports. Three things make that
the wrong long-term home:

- **Access is personal, not organisational.** A consultant may have access to some customer repos
  and not others. A single shared org PAT can't express that, and a build that "succeeds" using
  someone else's token hides a real access problem. Tokens must be per-user so a build fails for
  the person who lacks access.
- **Projects are an entity, not an admin setting.** The list of customer projects is something
  every developer browses and downloads from — not an Editor/Admin authoring chore. It deserves its
  own tools, ownership, and a download-centric UI.
- **Builds pile up.** Commit-keyed builds produce thousands of `project`-kind Releases over time.
  They must not pollute the Object Explorer's release list or its global compare picker; they're
  reached by deep-link from the artifact that owns them.

The hero task: a BC developer ("the builder") opens **Artifacts**, finds a customer project by
name, and downloads the newest build's `.app`s in a click or two.

## Navigation & tools

The split produces **three** distinct Tools-section entries where there was one overloaded one.
New order (User role and up for the public tools):

`Home · Piper · Templates · Cookbook · Object Explorer · Projects · Artifacts · Translator · MCP`

- **Templates** — the renamed Workspace/Extension *generator* (today's "Projects" item, which
  already routes to `/templates`). Its sub-routes move off the `/projects/*` namespace onto
  `/templates/workspace` and `/templates/extension` to free `/projects` for the entity tool. Only
  `/projects/extension` gets a redirect (preserving its `?template=` query); `/projects/new` can't,
  because that path is now the new-project page — old workspace-generator bookmarks to it land on
  the new tool and should use `/templates/workspace`.
- **Projects** — the customer/project entity: a directory you browse and create in, and where the
  owner configures repositories and settings. *Setup.* (No longer triggers builds — see the
  post-launch update above.)
- **Pipelines** (renamed from **Artifacts**) — the build surface: the **New build** action,
  per-project build history, changelog, logs, downloadable `.app`s, and project-scoped build
  comparison. *Build & deliverables.*

Build **trigger** (the **New build** action with its extension picker) is a Pipelines affordance
(owner/admin). Build **download** is also on Pipelines (any signed-in user).

## Roles & ownership

- A `Project` records `CreatedByUserId`. Any signed-in user may create a project, browse all
  projects in their org, and download any build's deliverables.
- Adding/removing repositories, triggering builds, editing settings, and deleting a project are
  restricted to the project **owner** or an org **Admin**. Enforced in the service layer (source of
  truth) and mirrored in the UI (the affordances are hidden for everyone else).
- No new role: "Admin" is the existing org `Admin`.

## Credentials: per-user repository tokens

The per-org `OrganizationSettings.AzureDevOpsPatEncrypted` / `GitHubPatEncrypted` columns are
**retired**. Two things replace them:

- **`UserRepositoryToken`** — a per-`(user, organization, provider)` token, encrypted with the Data
  Protection key ring under per-provider purpose strings
  (`ALDevToolbox.UserRepositoryToken.GitHub` / `…AzureDevOps`), mirroring the SMTP-password
  pattern in `SystemSettingsService` and the per-(user, org) scoping of `PersonalAccessToken`. A
  unique index on `(UserId, OrganizationId, Provider)` keeps it one token per provider per user.
  Managed by the user on an account page; the view exposes only presence + last-used, never
  ciphertext. The audit interceptor redacts the ciphertext column.
- **Org allowed-providers setting** — a multi-select (`GitHub` / `AzureDevOps`, at least one
  required) on `OrganizationSettings`, managed by an Admin. It gates which token fields a user sees,
  which providers the add-repo picker offers, and rejects tokens/repos for a disallowed provider
  with a field-keyed `PlanValidationException`. The common case (one provider) means a user never
  sees an irrelevant token box.

A build clones each repo as the **triggering user**, resolving that user's token for the repo's
provider. A missing or unauthorised token fails the build visibly, attributed to the right person.
The token is decrypted only inside the build service, only for the clone, injected as a transient
`http.extraHeader` credential — never on disk, never logged, never in the clone URL.

## The build entity

Builds are split off `Release` into a first-class **`ProjectBuild`**. This is the central
modelling decision: a build is a *set* of `(repository, commit)` pairs with logs, a changelog, and
multiple downloadable `.app`s — none of which `Release` models — while still producing exactly one
`project`-kind `Release` for object navigation.

New entities under `Domain/Entities/ObjectExplorer/` (`oe_` tables, org-scoped via the standard
query filter):

- **`ProjectBuild`** — `ProjectId`, `StartedByUserId`, `Branch`, `Status`
  (`queued|building|ready|failed`), `BcVersion`, `StartedAt`, `FinishedAt`, `FailureMessage`,
  **`RequestedAppIdsJson`** (the per-build extension selection — a JSON array of app-id GUIDs, or
  `null` for "build everything"; see "Extension selection" below), and **`ReleaseId`** (nullable FK
  to the produced `Release` — *the Object Explorer hook*).
- **`ProjectBuildRepoCommit`** — `(ProjectBuildId, ProjectRepositoryId, CommitHash, CommittedAt)`.
  The per-repo keying; a build is identified by this set, not a single hash.
- **`ProjectBuildCommit`** — the changelog: `(ProjectBuildId, ProjectRepositoryId, ShortHash,
  Message, Author, CommittedAt)`, captured at build time.
- **`ProjectBuildArtifact`** — a downloadable deliverable: `(ProjectBuildId, FileName, AppName,
  AppVersion, RuntimeVersion, SizeBytes, Content)`. `*.dep.app` is excluded at ingest, so it never
  appears as a download.
- **`ProjectBuildLog`** — `(ProjectBuildId, ProjectRepositoryId?, Content)` — captured clone +
  `alc` stdout/stderr, with a `Raw log` download.

`Project` drops `AutoBuildEnabled` (builds are user-initiated only). The existing
`oe_project_build_results` table is superseded by `ProjectBuildRepoCommit` + `ProjectBuildArtifact`
and migrated onto them.

## Build flow

The clean seam in `Services/ObjectExplorer/` is preserved: `ProjectBuildService.BuildAsync`
produces uploads, and `ReleaseImportService.ProcessReleaseAsync` ingests them into a `Release`
unchanged. The lifecycle is wrapped in `ProjectBuild`:

1. `StartBuildAsync(projectId)` (owner/admin) creates a `ProjectBuild` (`queued`, with
   `StartedByUserId`) and the `Release` (`Kind=project`, `Status=ingesting`), links them via
   `ProjectBuild.ReleaseId`, and enqueues the existing `ReleaseImportJob`.
2. The worker runs `BuildAsync`, which now also:
   - clones each repo with the **triggering user's** token and records HEAD per repo
     (`ProjectBuildRepoCommit`);
   - computes the changelog per repo as `git log <prev>..<new>` against the project's **last
     successful build**, with a merge-base ancestry check and guards for first-build /
     force-push (non-ancestor) / very large ranges (cap ~100, "…and N more")
     (`ProjectBuildCommit`);
   - captures clone + `alc` output (`ProjectBuildLog`);
   - excludes `*.dep.app` and **retains** the real `.app` bytes (`ProjectBuildArtifact`) — new,
     since today's uploads stream into ingest and aren't kept;
   - still returns `outcome.Uploads` for `ProcessReleaseAsync`.
3. `ProjectBuild.Status` flips ready/failed alongside the Release flip. The detail page's bounded
   status poll drives "Building…" → "Ready" live.

### Extension selection

A project's repositories often contain extensions you no longer want to compile (a retired legacy
app). The **New build** action lets the user pick which to build:

- **Live discovery.** Opening **New build** runs `ProjectBuildService.DiscoverExtensionsAsync` — a
  shallow (`--depth 1`, blobless, single-branch) clone of each repo, walked for `app.json` — and
  shows the discovered extensions as a checklist (all selected by default). It runs **in the
  request** (the Blazor circuit), *not* through `ReleaseImportWorker`, so the picker is responsive
  and never queues behind a long build; it enforces the same owner/Admin + per-provider-token gates
  as a build and surfaces clone/no-token/"no app.json" failures inline. The shallow discovery clone
  is intentionally separate from the build's full clone (the changelog needs history).
- **Persisted on the build.** The picked app-ids are stored on `ProjectBuild.RequestedAppIdsJson`
  (the build row is the source of truth, so a restart-resumed job rebuilds the same subset). When
  *every* extension is selected the value is `null` — "build everything" — so an app added to a repo
  after discovery is still built. App-ids are compared normalised (trimmed, de-braced, lower-cased)
  so a selection captured at discovery matches the manifest read at build time.
- **Applied in `BuildAsync`.** After discovery, the worker narrows the discovered set to the
  selection before resolving symbols and compiling; excluded apps are noted in the build log.

## Object Explorer split

- Project management moves out of `/admin/object-explorer/*` into the Projects/Artifacts tools; the
  old `AdminProjects` / `AdminProjectDetail` pages are removed.
- **`Kind=project` Releases are unlisted across the Object Explorer's *global* surfaces** — the
  release browser, the org-wide A/B compare picker, and any other org-wide release dropdown or
  search scope. Audit `ObjectExplorerService` listings, the global compare picker, and any
  `WHERE kind …` enumeration, and exclude project releases from the global ones.
- **Comparing two builds of the same project is kept** — it's genuinely useful — but scoped to that
  project. Artifacts offers a "Compare builds" picker listing only *this project's* builds;
  selecting two reuses `ReleaseComparisonService.CompareReleases` on the underlying Release ids and
  the existing compare view. Only the picker is project-scoped; the global picker never lists
  project builds.
- The only path into a build's objects is: open the artifact → deep-link to
  `/object-explorer/release/{ReleaseId}` (or the project-scoped compare view). `OeReleaseDetail`
  stays reachable by id and gains a "back to artifact" affordance so a deep-linked user isn't
  stranded in an unlisted release.
- Each build still creates a full Release + module/object ingest — visibility changes, not whether
  the Release exists.

## UI

Recreate the Claude Design handoff screens as idiomatic Blazor (real `.razor` components,
server-side `@code`, existing CSS tokens, Lucide via `Icon.razor`, downloads via a minimal-API
endpoint) — not a port of the prototype's structure. Every list page renders loading / empty /
populated; one primary button per page.

- **Projects** (`Components/Pages/Projects/`): `ProjectsBrowser` (`/projects`) — searchable
  directory, `+ New project` primary, latest-build status chip linking into Pipelines;
  `ProjectDetail` (`/projects/{id}`) — repositories editor (allowed-provider picker), owner,
  settings, danger-zone delete, and a "View builds & downloads" link. *Setup only — Save is the
  primary action; building moved to Pipelines.*
- **Pipelines** (`Components/Pages/Pipelines/`, renamed from Artifacts): `PipelinesBrowser`
  (`/pipelines`, alias `/artifacts`) — cross-project landing summarising each project's latest build
  with a quick `Download all` (latest *successful* build); `PipelineBuilds`
  (`/pipelines/{projectId}`, alias `/artifacts/{projectId}`) — the **New build** primary action
  (live-discovery extension picker, a `.confirm-modal` panel), latest-build card with per-`.app`
  download + Download all (outline), the per-repo changelog, build history (failures shown
  honestly), the BUILD LOG card with `Raw log`, project-scoped Compare builds, and the OE deep-link.
- Shared: `BuildStatusPill`, `CommitRef` (mono short hash + branch) under `Components/Shared/`.

Each user-facing page states the CLAUDE.md "UX definition of done" and gets a fresh-eyes
`design-review` pass on the rendered screens (light + dark).

## Downloads

Minimal-API `ArtifactEndpoints` (mirroring `GenerationEndpoints` / `EndpointHelpers`:
`ValidateAntiforgeryAsync`, `WriteAttachmentHeaders`, `.RequireAuthorization()`): single `.app`,
a build's Download-all zip, and the raw log. Each re-checks org scope before streaming bytes from
`ProjectBuildArtifact` / `ProjectBuildLog`.

## MCP parity

A new `ArtifactsTools` surface over the same services: list projects, list a project's builds
(commit set, status, changelog), fetch/download a build's `.app`s, and compare two builds of the
same project. Per the CLAUDE.md parity rule — agents want the same answers as the web UI.

## Migration

- Backfill `Project.CreatedByUserId` (ownerless → an org Admin).
- For each existing `Kind=project` Release, synthesise a `ProjectBuild` from the
  `oe_import_jobs.project_id → release_id` mapping and migrate `oe_project_build_results`
  provenance into `ProjectBuildRepoCommit`. Older builds lack retained `.app` bytes, full logs, and
  changelog — surface "captured before logs were kept" rather than fabricating them. The backfill
  ships as the idempotent data migration `BackfillArtifactsData`. `oe_project_build_results` itself
  is **retained**, not dropped — the Object Explorer release-manage page still reads it; the
  migration copies *from* it. Dropping it is a later cleanup once that page is cut over.
- Drop the org PAT columns; users re-enter personal tokens. Seed the org allowed-providers set from
  the providers existing repos already use (default both if none).

## Out of scope (v1)

- **Build/Release retention & pruning.** Every build keeps a full Release + ingest; at thousands of
  builds this grows unbounded. A retention policy (prune old ingests, keep only the `.app` +
  metadata past N builds) is a follow-up.
- **Project visibility** (private vs org-wide) and **Workspace/Extension cross-links** on the
  project — improvised prototype UI, excluded.
- **Background/auto builds** — removed with `AutoBuildEnabled`; builds are user-initiated only.
- **Branch/tag/commit selection** — unchanged from the OE build path (default branch, HEAD).
