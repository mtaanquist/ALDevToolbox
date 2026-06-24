# Object Explorer: compile customer releases from Azure DevOps / GitHub

This document specifies a new Object Explorer ingest path: define a **Customer**, point it at one or more Git repositories (Azure DevOps or GitHub), and have the toolbox clone the source, resolve the right Business Central symbols, compile each extension, and ingest the resulting `.app` files as a `customer`-kind Release. It is the compile-from-source sibling of the Microsoft-artifacts import documented in `object-explorer.md` ("Importing from Microsoft artifacts").

**Status:** **implemented and shipped** (PR-stack `feat/customer-builds-*` → `feat/oe-customer-build-polish`), validated end-to-end on staging. The sections below are the original proposal; **read "As built" first** for where the implementation diverged. The core property held: compiled `.app`s flow through the same `ReleaseImportService.ProcessReleaseAsync` every other source already uses — zero new ingest code.

## As built

What shipped differs from the proposal in a few deliberate places; the rest landed as written.

- **Symbol country reuses `OrganizationSettings.AutoImportCountry`** — *not* a new `DefaultArtifactCountry` column. The fallback chain is per-Customer `DefaultArtifactCountry` → org `AutoImportCountry` → `w1`. (Adding a separate column was deemed premature; the existing per-org country is the same concept.)
- **One artifact per build, not per extension.** `CustomerBuildService` resolves a single Microsoft artifact for the highest `application` Major.Minor across the discovered apps, extracts *every* MS `.app` from it (application + platform) into one package-cache dir, and adds the repos' committed `.alpackages/` on top. `alc` resolves each app's deps against that shared cache.
- **Parent import is inline, reusing the same download.** `CustomerBuildService.EnsureParentReleaseAsync` ingests the parent first-party Release *within the build* from the artifact zips it already downloaded for symbols — it does **not** call `ArtifactReleaseImporter.ImportAsync` (which would enqueue a second job). Best-effort: a failed parent import logs and the build continues with `ParentReleaseId = null`.
- **Build provenance captured.** `oe_customer_build_results` carries `repo_url`, `commit_sha`, `commit_date` (per built app, read via `git show -s` right after clone) in addition to `(release_id, app_name, app_id, status, message)` — surfaced on the manage page and seeding a future Artifacts surface.
- **Build state lives in Status, and customer labels aren't unique.** The provisional label is just the customer name (final: `"{Customer} on BC {Major}.{Minor}"`); the OE list/manage render an ingesting customer release as **"Building…"**. Customer-kind releases are **exempt from the unique label index** (`deleted_at IS NULL AND kind <> 'customer'`) so a rebuild reuses the clean label — the release id is their identity. First-party label uniqueness (the artifact-sweep dedup backstop) is unchanged.
- **Admin UI is tabbed.** The admin Object Explorer is `Releases / Customers / Import` (`AdminObjectExplorerHeader`). Customers is a projects list → customer detail page (`AdminCustomerDetail`: build trigger, AL-compiler status/Update, repos via the ghost-row table editor, build history). The Import sub-nav dropped its Customer pill.
- **User-facing Customer tab is a project drill-in.** `/object-explorer` Customer tab shows one card per customer → click → that customer's release cards (no hero). A customer release shows its source repo URL(s) under the title.
- **Deferred:** the **manual-symbols recovery** path (manage-page per-row "upload the missing dependency `.app` and re-compile just that extension") is *not yet built* — it's the remaining follow-up (was "PR 3b"). Retry-build (re-clone HEAD, rebuild in place) shipped.

## Why

Importing a customer's solution into the Object Explorer today is a manual slog. An operator has to: clone each repo by hand; download the matching symbol packages from the customer's BC environment; make sure the resource-exposure policy is set so the compiled output actually carries source; compile every extension in dependency order; zip the whole structure into the shape the importer expects; and upload it. Every step is a chance to get the symbol versions wrong or to drop source on the floor.

The Microsoft-artifacts import already proved the toolbox can own this kind of fetch-and-ingest pipeline for *prebuilt* Microsoft releases. Customer solutions are the missing half: the source lives in a repo, not on a CDN, and it has to be compiled before there's an `.app` to ingest. The version of symbols to compile against isn't a guess — each extension's `app.json` declares its `application` (and `platform`) version, which maps straight onto a Microsoft OnPrem artifact the toolbox can already resolve and download (`BcArtifactService`). So the whole flow is mechanisable: clone → read `app.json` → resolve+download symbols → compile → ingest.

This graduates the roadmap item "Import a workspace straight from Azure DevOps (PAT)". It is adjacent to "Source-only ingest for uncompiled apps" but supersedes it for the case where we can compile: compiling produces a real `SymbolReference.json`, so we get full-fidelity ingest (resolved types, exact references) rather than the lower-fidelity header-scan path the source-only idea settled for.

## Scope

In scope:

- A **Customer** entity that groups one or more repository URLs, managed from the Object Explorer admin surface much like the Artifacts tab manages countries/versions.
- **Per-org shared credentials**: one Azure DevOps Personal Access Token and one GitHub PAT per organisation, stored encrypted. Each repo names its provider; the build uses the matching org PAT.
- Cloning **HEAD of the default branch** of each repo (stated assumptions — no branch, tag, or commit selection in v1).
- **Per-extension symbol resolution** from each `app.json`'s `application` version via the existing Microsoft-artifact download, plus third-party dependency symbols sourced from a `.alpackages/` cache committed in the repo when present.
- **Compilation** with the BC Development Tools (`Microsoft.Dynamics.BusinessCentral.Development.Tools`, the `alc` compiler), in inter-app dependency order, emitting source into the output symbol package.
- Ingesting the compiled `.app`s into a `customer`-kind Release whose parent is the matching Microsoft artifact Release (so cross-release reference resolution works unchanged).
- **Per-extension partial failure** with two recovery paths: retry the build, or manually supply the missing dependency symbols for a failed extension and re-run it. A failed extension never blocks its successful siblings.

Out of scope (v1):

- **Auto-loading the latest from a repo on a schedule.** A daily "rebuild every Customer from HEAD" worker is a future nice-to-have (see "Future"), not v1.
- **Branch / tag / commit selection.** Always default branch, always HEAD.
- **Private NuGet symbol feeds.** Third-party symbols come from the repo's committed `.alpackages/` or the extension fails — we don't authenticate to AppSource symbol feeds.
- **Editing or persisting the cloned source as a workspace.** The clone is a transient build input, deleted after the build; only the compiled `.app` output is ingested.

## Credentials

Per-org shared PATs, stored exactly like the existing machine-translation API key:

- Two new nullable columns on `OrganizationSettings`: `AzureDevOpsPatEncrypted` and `GitHubPatEncrypted`, encrypted with the Data Protection key ring under a new purpose string on `OrganizationConfigService` (e.g. `GitRepositoryPatProtectionPurpose`), mirroring `MachineTranslationApiKeyEncrypted`. Losing the `app-keys` ring requires re-entering them. The audit interceptor redacts both ciphertext columns so they never land in history (same treatment as the SMTP password and the MT key).
- Administration grows a dedicated **Repositories** tab (`/admin/administration/repositories`, Admin-only) with a "Repository access" section: set or clear each PAT, with the view record exposing only `bool HasAzureDevOpsPat` / `bool HasGitHubPat` — never the plaintext. (A dedicated tab rather than sharing the machine-translation page: repo PATs are a distinct concern from translation, and the tab is the natural home for the per-org customer-build settings that follow, e.g. the default artifact country.)
- Each Customer repo declares its `provider` (`azure_devops` | `github`). The build picks the matching org PAT. A repo whose provider has no PAT configured fails that repo with a clear, recoverable message ("No GitHub PAT configured for this organisation — set one under Settings → Repository access") rather than a generic auth error.

The PAT is decrypted only inside the build service, only for the duration of the clone, and is injected as a transient credential (an `http.extraHeader` Authorization on the `git` invocation) — never written to disk, never logged, never put in the clone URL where it could leak into process listings.

## Customer and repository entities

C# entities live in `Domain/Entities/ObjectExplorer/` alongside the other `oe_*` types; SQL tables take the `oe_` prefix; both are org-scoped via the standard `ScopeToOrganization<>` query filter (same shape as `BcArtifactVersion`).

```
Organization ──┐
               └── has many ──> Customer  (table: oe_customers)
                                  ├── (organization_id, name, default_artifact_country?,
                                  │    created_at, updated_at, deleted_at)
                                  └── has many ──> CustomerRepository  (table: oe_customer_repositories)
                                                     └── (customer_id, provider, url, display_name)
```

- `oe_customers` — org-scoped, soft-deletable (`deleted_at`, consistent with the rest of the codebase). `name` is the customer-facing label used to build the Release label. `default_artifact_country` is the optional per-Customer country override described under "Symbol resolution".
- `oe_customer_repositories` — a child table rather than a JSON column, because repos are listed, validated (per-provider URL shape), and managed individually. `provider` is a short enum string (`azure_devops` | `github`). One Customer may span both providers.

The Release itself reuses the existing `oe_releases` schema (per `object-explorer.md`): `Kind = "customer"`, `CustomerName` set from the Customer's name, and `ParentReleaseId` pointing at the matching Microsoft artifact Release. No new Release columns. The build-result detail (below) is the only release-adjacent new table. Release label: `"{CustomerName} on BC {Major}.{Minor}"` — derived from the `application` version the apps compiled against.

## Build pipeline

The build is a new `ReleaseImportSource.CustomerBuild` job, drained by the existing `ReleaseImportWorker` alongside the `BcArtifact` case. A coordinator `CustomerReleaseImporter` (mirroring `ArtifactReleaseImporter`) creates the `ingesting` Release, captures the org identity, and enqueues the durable job (`PersistedImportJobs` + `ReleaseImportQueue`, so it survives a restart). The build logic lives in a new `CustomerBuildService` whose output is the `List<AppFileUpload>` the importer already consumes, plus a per-extension result list.

### 1. Clone

Shell out to `git`, following the external-process pattern `BackupService` uses for `pg_dump`/`pg_restore` (resolve the binary from a `GIT_PATH` env var, default to a PATH lookup). Clone HEAD of the default branch of each of the Customer's repos into a temp directory under the OS temp path (mirroring the `oe-artifact-` / `oe-dvd-` temp-file convention), with the PAT injected as a transient `http.extraHeader`. Everything is deleted in a `finally` block exactly as `ReleaseImportWorker` cleans up staged zips and downloaded archives.

### 2. Discover apps

Walk each clone for `app.json` files (skipping `.alpackages/`, `.vscode/`, test-app folders by the same heuristics the workspace walker already applies). Each `app.json` is one extension to build. Read its `id`, `name`, `publisher`, `version`, `application` (and `platform`) version, and `dependencies`.

### 3. Resolve symbols (per extension)

- **Platform + foundational symbols** come from the `application` version via `BcArtifactService.ResolveOnPremAsync` + `DownloadArtifactSetAsync`, which already return the platform `System.app` plus the localized Base Application / Business Foundation apps. The **country** is resolved per-Customer override → org `DefaultArtifactCountry` (a new `OrganizationSettings` column) → `w1`. A dedicated default is cleaner than reusing `AutoImportCountry`, because auto-import can be off while customer builds are on.
- **Third-party dependency symbols** (`dependencies` in `app.json`) come from a `.alpackages/` cache committed in the repo when present. When a dependency's symbols can't be found there, **only that extension fails** — its siblings still build and ingest. (Concrete case from the request: a repo with `Core` and `ContiniaExts` where Continia's symbols are missing still imports `Core`.)
- The matching Microsoft artifact Release becomes the new Release's `ParentReleaseId`. If that artifact Release isn't imported yet, the build imports it first by reusing `ArtifactReleaseImporter.ImportAsync` (auto-import the parent rather than refusing — see Open questions), so the chain is closed before the customer apps land.

### 4. Compile

Topologically order the repo's apps by their inter-app `dependencies` and compile each with `alc` against the resolved symbol set. The compile requests **source embedded in the output symbol package** — this is the "resource-exposure policy correct" concern from the request made mechanical: the produced `.app` carries its `src/` tree, which the ingest pipeline prefers (per the Storage-policy table in `object-explorer.md`). Each produced `.app` becomes an `AppFileUpload`.

### 5. Ingest

The `AppFileUpload` list flows into the existing `ReleaseImportService.ProcessReleaseAsync` — **zero new ingest code**.

> **Design property: `AppFileUpload` is the common ingest currency.** Every release source — manual upload, DVD URL, Microsoft artifacts, and now customer builds — converges on the same `List<AppFileUpload> → ProcessReleaseAsync` seam. The existing `ReleaseImportSource.BcArtifact` case already proves it (download → uploads → ingest). A future, richer source (a more elaborate artifact pipeline, a different VCS, a binary drop) plugs in by producing that list and adding a `ReleaseImportSource` variant; it never touches ingest code. Customer builds compose with the artifact path directly — step 3 downloads Microsoft artifacts as the *symbol input* to the compile, then feeds the compiled output back through the same ingest.

## Partial failure, retry, and manual symbols

The error handling the request calls "critical" is per-extension, not per-build.

- A new `oe_customer_build_results` table records one row per extension in a build: `(release_id, app_name, app_id, status, message)` with `status ∈ {compiled, ingested, failed}`. Successful `.app`s ingest normally; a failed extension leaves a row with a friendly reason — missing third-party symbols, a compile error, or a missing PAT.
- A Release with some failed extensions still goes **`ready`** (its successes are immediately usable) but carries a "partial" badge and a build-results panel on its manage page. We do not hide a half-good import behind `failed`.
- Two recovery actions on the manage page:
  - **Retry the build** — reuse the existing failed-release retry shape (`ReopenForRetryAsync` + re-enqueue), here re-enqueuing a `CustomerBuild` job. Useful when the failure was transient (a flaky clone, a parent artifact that has since been imported).
  - **Manually supply symbols** — upload the missing dependency `.app`(s) for a specific failed extension, then re-run just that extension's compile. This is the "manually choose symbols" fallback: when a third-party dependency isn't in the repo's `.alpackages/` and isn't on a Microsoft artifact, the operator drops in the symbols they have and the single extension compiles and ingests without redoing the whole build.

## Toolchain and deployment

The compile step needs `git` (added to the image) plus the AL compiler (`alc`). The compiler is **not** baked into the image — it's provisioned at runtime into a persistent volume so a new compiler version never requires an image rebuild.

**Compiler packaging (verified empirically, June 2026).** The cross-platform compiler ships in the `Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux` NuGet package as `lib/<tfm>/alc` (plus `altool`). Key facts found by testing in the actual `aspnet:10.0` runtime image:
  - **`dotnet tool install` is the wrong delivery.** v17.x/v18.x **fail to install as a .NET tool** ("Package … is not a .NET tool" — microsoft/AL#8242) because they ship the binaries under `lib/` rather than `tools/`. But that's only the tool-installer's check: **downloading the `.nupkg` (a zip) and extracting `lib/<tfm>/alc` yields a fully working compiler.** Verified `alc` from `18.0.37.11445-beta` prints `Microsoft (R) AL Compiler version 18.0.37.11445` and exits 0 on `aspnet:10.0`.
  - **The newest packages are .NET 10 native.** `18.x` ships `lib/net10.0/` → runs directly on `aspnet:10.0`, no roll-forward, widest runtime band. Older ones ship `lib/net8.0/` → run with `DOTNET_ROLL_FORWARD=LatestMajor` scoped to the `alc` subprocess. The provisioner prefers `net10.0`, falls back to `net8.0`+roll-forward.

**Runtime provisioning (`AlCompilerProvisioner`).** A service that:
  - Queries the NuGet flat-container index (`https://api.nuget.org/v3-flatcontainer/microsoft.dynamics.businesscentral.development.tools.linux/index.json`) for available versions; the array is SemVer-ascending so the newest (incl. prerelease) is the last entry. Default policy: **track newest** (overridable by an `AL_COMPILER_VERSION` env pin).
  - Downloads the `.nupkg` with `HttpClient` and extracts `lib/<tfm>/` with `System.IO.Compression.ZipFile` into the `app-altool` volume (no `unzip`/SDK needed), then `File.SetUnixFileMode`s the execute bit on `alc`. Records the installed version in a marker file.
  - Exposes status — installed version, newest-available, update-available — surfaced on the admin UI as the **running version check the operator asked for**; an Update action re-provisions. Updating never rebuilds the image.
  - `AL_COMPILER_PATH` overrides the resolved `alc` for air-gapped installs (pre-seed the volume); `GIT_PATH` resolves `git` (default PATH lookup), following the `BackupService` `pg_dump` pattern.
  - **Graceful degradation:** if NuGet is unreachable and the volume is empty, the Customer-build feature reports itself unavailable with a clear message and the app still boots — the compiler isn't a hard startup dependency.

**Invocation.** Per-project `alc /project:<dir> /packagecachepath:<symbols> /out:<app>` in the build's own topological order (the dependency-ordering `al workspace compile` only exists in v17+ and isn't needed — per-project compile is required anyway to attribute per-app failures). Image change is just `apt-get install -y git`.
- **Graceful degradation when the toolchain is absent.** If `alc`/`git` can't be resolved (e.g. a custom image build that dropped them), the Customer-build feature reports itself unavailable with a clear message and the app still boots — the compiler isn't a hard startup dependency the way the database is. (A `/healthz` signal for "build toolchain present" is optional and can wait.)
- **Memory.** The container's 4 GiB ceiling (`compose.yml`) is already sized for heavy Object Explorer ingest. A compile plus a downloaded symbol set may push it; the operational note is to bump the reservation or stream the symbol cache to disk rather than hold it in memory. Recorded as an operational concern for the implementation, not a blocker.

### Architectural fences crossed (maintainer-approved)

Two fences from `CLAUDE.md` are deliberately crossed here, with sign-off:

1. **A background worker that compiles.** This is *not* the "synchronous generation" fence — that rule is about the workspace/extension generator, which stays synchronous and in-memory. The build worker is architecturally identical to the existing `ReleaseImportWorker` / `BackupScheduler` / `OffsiteRestoreWorker` (`BackgroundService` + durable queue + heartbeat). It's heavier (it shells out and compiles), but it's the same shape.
2. **`git` + `alc` baked into the image as a new build dependency.** New tooling in the container, accepted as the cost of owning the compile step.

## MCP parity

Nothing new is required on the MCP surface. This is an admin/authoring flow; the *result* — a `customer`-kind Release with its modules, objects, symbols, and references — is already exposed through the existing Object Explorer MCP tools (`list_releases`, `list_release_modules`, `find_references`, …). Per the `CLAUDE.md` rule, authoring flows that aren't part of the AL-reading surface skip MCP, and the read surface here is unchanged.

## Future

- **Auto-build scheduler.** A daily sweep mirroring `ReleaseAutoImportScheduler` that re-pulls each Customer's repos from HEAD and rebuilds, so the Object Explorer always reflects the latest source. The hard part is dedup/idempotency: HEAD moves, and `app.json`'s version may not bump per commit, so the Release label needs a commit-SHA-or-date suffix to avoid clobbering the previous build — flagged below.

## Open questions

1. **Label / dedup when HEAD moves.** A customer build has no stable version key (the `app.json` version may not change per commit). For the v1 manual trigger, re-running replaces in place; for the future auto-build scheduler, the label likely needs a `@{shortSha}` or date suffix. Decide the dedup key before the scheduler lands.
2. **Mixed-country repos.** Country is resolved per-Customer → org default → `w1`. The remaining edge is a *single repo* whose apps target different localizations — unlikely in practice; note and defer rather than over-engineer.
3. **Compiler version selection.** ~~Single baked vs per-version.~~ **Resolved:** the compiler is provisioned at runtime from NuGet into a volume (not baked), tracking the newest available version (incl. prerelease), with an admin version-check/Update affordance and an `AL_COMPILER_VERSION` pin override. A new compiler version never needs an image rebuild; bounded-band compile failures still fall to the per-app partial-failure path. See Toolchain.
4. **Missing parent artifact.** ~~Auto-import vs refuse.~~ **Resolved:** auto-import the matching Microsoft artifact Release inline within the build worker, so the parent Release is `ready` before the customer apps ingest.

## Verification

When the feature is implemented, coverage follows the patterns in `ALDevToolbox.Tests/`:

- `CustomerBuildServiceTests` — over a synthetic two-app repo (`Core` + `ContiniaExts`) where Continia's symbols are absent: assert `Core` compiles and ingests, `ContiniaExts` lands a `failed` build-result row with a missing-symbols message, and the Release goes `ready` with a partial badge.
- Symbol-resolution test — assert the `application` version in `app.json` maps to the expected artifact URL via `BcArtifactService`, and the country resolves per-Customer → org default → `w1`.
- Credentials test — assert the PAT round-trips through the Data Protection columns, the view record exposes only `HasAzureDevOpsPat` / `HasGitHubPat`, and the audit interceptor redacts the ciphertext.
- Worker test — assert a `CustomerBuild` job is durable across a restart (re-enqueued by the reconciler) and that retry re-runs the build in place.

End-to-end smoke (manual, after CI green): configure a GitHub PAT, create a Customer pointing at a real two-extension repo, run the import, and confirm the Release appears with both extensions' objects browsable and cross-module references into the parent BC release resolving — plus a deliberately broken dependency to confirm the partial-failure path and the manual-symbol recovery.
