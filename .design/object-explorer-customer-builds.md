# Object Explorer: compile customer releases from Azure DevOps / GitHub

This document specifies a new Object Explorer ingest path: define a **Customer**, point it at one or more Git repositories (Azure DevOps or GitHub), and have the toolbox clone the source, resolve the right Business Central symbols, compile each extension, and ingest the resulting `.app` files as a `customer`-kind Release. It is the compile-from-source sibling of the Microsoft-artifacts import documented in `object-explorer.md` ("Importing from Microsoft artifacts").

**Status:** proposal. No code in this doc has been written. Once approved it adds a new `ReleaseImportSource` and the supporting Customer model; it does not change the existing ingest pipeline — compiled `.app`s flow through the same `ReleaseImportService.ProcessReleaseAsync` every other source already uses.

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

The compile step needs two binaries the image doesn't ship today (`mcr.microsoft.com/dotnet/aspnet:10.0` carries only `curl` and `postgresql-client-18`):

- **Bake `git` and the BC Development Tools (`alc`) into the Dockerfile.** `alc` is a .NET tool and runs cross-platform, so it sits naturally on the existing .NET base image; the cost is image size, accepted as a documented build dependency. New env knobs `GIT_PATH` / `AL_COMPILER_PATH` resolve the binaries (default to a PATH lookup), following the `BackupService` `pg_dump` pattern.
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
3. **Compiler version selection.** The BC Development Tools package is versioned per BC major, and a given `alc` compiles a bounded range of `application` versions. Decide whether to bake a single compiler version, or select/pin one per the resolved `application` version (multiple `alc` versions in the image).
4. **Missing parent artifact.** This doc proposes auto-importing the matching Microsoft artifact Release when it's absent (so the chain is closed). Confirm that's preferred over refusing with a "import BC {x.y} ({cc}) first" prompt.

## Verification

When the feature is implemented, coverage follows the patterns in `ALDevToolbox.Tests/`:

- `CustomerBuildServiceTests` — over a synthetic two-app repo (`Core` + `ContiniaExts`) where Continia's symbols are absent: assert `Core` compiles and ingests, `ContiniaExts` lands a `failed` build-result row with a missing-symbols message, and the Release goes `ready` with a partial badge.
- Symbol-resolution test — assert the `application` version in `app.json` maps to the expected artifact URL via `BcArtifactService`, and the country resolves per-Customer → org default → `w1`.
- Credentials test — assert the PAT round-trips through the Data Protection columns, the view record exposes only `HasAzureDevOpsPat` / `HasGitHubPat`, and the audit interceptor redacts the ciphertext.
- Worker test — assert a `CustomerBuild` job is durable across a restart (re-enqueued by the reconciler) and that retry re-runs the build in place.

End-to-end smoke (manual, after CI green): configure a GitHub PAT, create a Customer pointing at a real two-extension repo, run the import, and confirm the Release appears with both extensions' objects browsable and cross-module references into the parent BC release resolving — plus a deliberately broken dependency to confirm the partial-failure path and the manual-symbol recovery.
