# CLAUDE.md

Guidance for working on this repository: the principles, fences, and conventions to obey in every session. The *map* of the project — folder layout, the design-doc contract and handoff porting guide, the AL/C-AL allow-list and MCP-parity maintenance guides, and the release process — lives in **`PROJECT.md`**. Before adding files, porting a design screen, editing the ported stylesheets, touching the reference extractor or the MCP tools, setting up a local run, or cutting a release, read the matching `PROJECT.md` section first.

## Project at a glance

- **AL Dev Toolbox** — internal Blazor Server tool that generates AL/BC workspaces and standalone extensions from runtime templates.
- Stack: .NET 10, Blazor Server, EF Core 10 + Npgsql against PostgreSQL 18, Tomlyn. Lucide icons are vendored as embedded SVGs (no NuGet dependency); see `Resources/Icons/`.
- Two projects at the repo root: `ALDevToolbox/` (the app, layered by folder) and `ALDevToolbox.Tests/` (xUnit v3 + AwesomeAssertions). The solution file is `ALDevToolbox.slnx` at the repo root. `PROJECT.md` has the folder-by-folder map — match it when adding files.
- Source of truth for behaviour: documents under `.design/` (indexed in `PROJECT.md`). If code disagrees with the design doc, fix one of them — don't leave them out of sync.

## Development principles

### Keep code idiomatic C# / Blazor

- Nullable reference types and implicit usings are enabled. Don't disable them per-file.
- File-scoped namespaces. PascalCase for types/members, `_camelCase` for private fields, `camelCase` for parameters/locals.
- Records for immutable data shapes (plans, DTOs, mustache contexts). Classes for EF entities — EF needs settable properties.
- Constructor-injected dependencies stored as `private readonly`. We don't use primary constructors on services yet; stay consistent until we change them all at once.
- `async`/`await` end-to-end. Every `Task`-returning service method takes a `CancellationToken` and threads it through.
- Use `AsNoTracking()` on every read-only EF query. We've been disciplined about this so far.
- Prefer minimal LINQ over hand-rolled loops, but don't reach for tricks (no `Aggregate`-as-fold games) when a `foreach` is clearer.
- Use structured logging with named placeholders (`_logger.LogInformation("Generated {Workspace}…", plan.WorkspaceName)`), never string interpolation into the message template.

### DRY, but not prematurely

- Factor shared logic out the **second** time it's needed, not the first. The split between workspace and standalone generation reuses `WriteExtensionAsync` because both flows need the same per-extension layout — that's the bar.
- Don't introduce interfaces for services until there's a second implementation or a real test seam. `GenerationService` is a concrete class injected as itself; keep it that way until something forces the change. The one place that *has* cleared this bar is off-site storage: `IOffsiteStorageProvider` (in `Services/Offsite/`) has two real implementations — `S3Provider` and `AzureBlobProvider` — selected per request by `OffsiteStorageProviderFactory` from the `offsite_provider` setting. `OffsiteBackupService` owns all orchestration and only delegates raw transport to the provider; that's the sanctioned extension point for a third backend. This bar is about *premature abstraction*, not about slowing tool work down: when you're building a **new tool** or actively iterating one, add the components, services, and second variants that tool needs and keep moving — the "wait for the second caller" caution targets cross-cutting machinery, not the normal internals of one tool.
- Reusable UI is what `Components/Shared/` is for, and its inventory is real, not aspirational — `SettingRow`, `AuthCard`, `ConfirmDialog`, `DependencyPicker`, `AuditHistoryPanel`, `CodeViewer` and friends already exist. Check the folder before building a control a sibling page already has.
- Three similar lines is fine. A premature abstraction over two callers is worse than the duplication.

### Always have the end user in mind

- Every list page renders three states: loading, empty (with a useful message that tells the user how to recover), populated. See `TemplatesBrowser.razor` for the shape.
- Forms validate on the server (the source of truth) **and** mirror the rules in HTML attributes (`pattern`, `required`, `min`) so users get instant feedback. Keep the two in sync — the regex in `GenerationService.WorkspaceNameRegex` and the `pattern=` on the input field must match.
- Validation errors return field-keyed dictionaries via `PlanValidationException` so the UI can render them inline next to the field. Don't throw plain strings for things the user typed.
- Helpful copy beats clever copy. Captions under fields, placeholders that show real examples ("e.g. CRONUS A/S"), error messages that say what to do next.
- **Placeholder names and characters in user-facing copy follow a house style — match it, don't reintroduce the old one.** Use **CRONUS** (the standard Business Central demo company) for any placeholder customer/company/workspace name in placeholders, captions, and examples — never "Acme"/"ACME". For punctuation, use **ASCII** by default: straight quotes (`'` and `"`, not `'` `'` `"` `"`) and `...` rather than the `…` ellipsis character. The em-dash `—` and the arrow `→` (e.g. "Account → Repository tokens") are the two non-ASCII characters we *do* use. When you add a `.razor` page or any visible string, grep your new copy for `Acme`, `…`, and curly quotes before committing.
- **Help text is written for the user, not the maintainer.** Field captions and hints exist to help someone who doesn't know the codebase use the tool. Keep them short and plain. AL/BC domain terms the audience already knows are fine (`.app`, `.Source.zip`, DVD, codepage, country code); codebase-internal jargon is not — never surface implementation details like class or method names ("chain walker"), internal marker conventions (`_Exclude_`), serialised filenames, MCP tool names, or AL compiler flags by name (`IncludeSourceInSymbolFile`) in user-facing copy. If a caption is explaining *how the code works*, cut it down to *what the user needs to do*. The Import Release tabs (`Components/Pages/Admin/AdminReleasesImport*.razor`) are the worked example of this tone.
- Visual hierarchy: the **Generate** button is the only primary action on any page. Everything else is the outline button style. Don't introduce a second primary button.
- Keep the user's flow synchronous when it can be — generation runs in-process and streams the ZIP back. Don't add a job queue; if generation ever gets slow, fix the slow part.
- Loading states on long-running buttons (Generate, Export). Confirmation modals on destructive actions.

### Cohesion is not friendliness — the UX definition of done

Reusing the nearest existing component and CSS classes buys **cohesion** (the page looks like the rest of the app). It does **not** buy **usability** (a first-time user knows what to do). These are different properties and the second one is the one that's easy to skip, because it can't be pattern-matched — it requires picturing a specific person doing a specific task for the first time. `AdminProjectDetail.razor` is the cautionary example: it obeys every cohesion rule above yet reused the power-user ghost-row grid for a 0–3-item list and explained a *mechanic* ("start typing in the blank row") instead of offering an obvious `+ Add` button.

So: a page that takes user input isn't done until each of these holds. State them in the PR description.

- [ ] **Named user.** Say who this is for and what they're doing, knowing nothing about our code (e.g. "a BC consultant registering their first customer"). Without a named user, copy defaults to the maintainer's mental model — that's where jargon comes from.
- [ ] **Primary action is obvious**, labelled with a verb the user would use ("Create customer", not "Save"), and there's still only one primary button on the page.
- [ ] **Empty / first-run state tells the user the next step** and gives them a button to take it — not a bare table or grid.
- [ ] **No mechanic needs explaining.** If a caption explains *how the UI works*, the UI is wrong, not the caption. Fix the affordance.
- [ ] **Jargon test passed.** Read every visible word as the named user. Any class/method name, env var, volume name, package or registry name (NuGet), compiler flag, internal marker, or filename convention = fail. (This is the help-text rule above, now mandatory and checked, not aspirational.)
- [ ] **Pattern fits the task,** justified by its shape and frequency — not by which component was nearest. A rarely-edited short list is not a power-user grid even if the grid exists.
- [ ] **Looked at it rendered** — a screenshot or a real run, not just the markup. Spacing, empty states, and button prominence don't show up in `.razor` source.

When you finish a user-facing page, run a fresh-eyes pass with the **`design-review`** subagent (`.claude/agents/design-review.md`): it reviews the rendered page as a newcomer with no implementation context, which is the only reliable way to catch jargon the implementer is blind to. Don't self-certify the jargon test — the person who wrote "downloaded from NuGet" knew what NuGet was. Trust its UX judgments; verify its claims about what the code does before acting on them — it reviews the rendered page without reading the implementation, and it is often wrong about mechanics.

### Stay inside the architectural fences

These are deliberate constraints from `.design/architecture.md` and `.design/templates-and-seeding.md`. Don't quietly relax them.

- **The PostgreSQL database is the only persistence layer for templates, modules, the catalogue, per-folder file contents, organisations, users, signup requests, password reset tokens, login attempts, organisation settings, organisation assets, and organisation files.** Both authoring surfaces (the structured admin form and the TOML editor) write through the same `TemplateInput` pipeline into the DB. The on-disk `Templates.seed/` bootstrap was retired: the singleton **system org** (`organizations.is_system = true`, stamped on the Default org by migration `20260513000000_MoveSeedToSystemOrg`) holds the canonical templates that other orgs fork via `TemplateImportService`. New orgs start empty; admins import on demand from `/admin/templates`.
- **The ruleset and `.gitignore` ship as code.** They live as embedded resources under `Resources/` because they're per-deployment policy. Per-folder example AL file *contents* live in the `template_files` table and are admin-editable. The logo, organisation defaults block, and always-included file list live in the database (`organization_assets`, `organization_settings`, `organization_files`) and are admin-editable. Binary files inside template folders are out of scope for v1 — text content only.
- **`defaults_json` and `app_source_cop_json` stay as JSON columns.** Don't normalise them into separate tables — the AL ecosystem changes those shapes too often.
- **Multi-tenant by default.** Every editable entity carries an `organization_id`. EF query filters on `AppDbContext` scope reads to `IOrganizationContext.CurrentOrganizationId`; pre-login flows that genuinely need cross-org reads (login, signup, bootstrap) call `IgnoreQueryFilters()` explicitly. Service code that mutates state must run inside an authenticated request — `RequireOrganizationId()` throws otherwise. The `SINGLE_TENANT_MODE=1` env var (an immutable boot-time singleton, `ISingleTenantMode`) only *hides and disables* multi-tenant **surfaces** for internal single-org hosting — storage quotas, per-tenant snapshots, and self-service org creation at signup. It does **not** relax the tenant-isolation fence (the query filters and `IgnoreQueryFilters()` rules below still apply unchanged); see `.design/deployment.md`.
- **`IgnoreQueryFilters()` is the tenant-isolation fence.** The EF query filter is the *only* thing that keeps a request from one org's user reading another org's data. Every existing `IgnoreQueryFilters()` call site is deliberate and reviewed: pre-auth flows (login, signup, password reset, bootstrap), the SiteAdmin console (`/site-admin/*`), the `OAuthClaimsTransformer` user-lookup that validates the token's `org` claim, migrations, and the Microsoft sign-in half of `EntraSignInService` (`IsSignInAvailableAsync`, `GetLoginSurfaceAsync`, `ResolveChallengeAsync`, `GetClientSecretAsync`, `CompleteAsync`) plus `AuthService.IsLocalLoginDisabledAsync` — those run in the login page and the OIDC callback, before any cookie exists, and routing a sign-in to exactly one org *is* the job. The account-linking half of the same service (`ResolveChallengeForCurrentOrgAsync`, `ListLinksAsync`, `LinkAsync`, `UnlinkAsync`) runs authenticated and stays **inside** the filter; its one exception is the existence-only uniqueness probe in `LinkAsync`, documented at the call site. **Never add a new `IgnoreQueryFilters()` call without explicit confirmation from the maintainer** — especially not inside an MCP tool, an admin service, an endpoint, or anything that runs under a normal authenticated request. If a query feels like it needs to escape the filter, the answer is almost always to scope it tighter, not to remove the fence. The same rule applies to constructing an `AmbientOrganizationContext` with someone else's org id from inside a request — don't.
- **Email/password accounts, three roles (`User`, `Editor`, `Admin`), admin-approved signups.** `User` uses the generator only; `Editor` additionally sees the content-authoring admin pages (templates, modules, catalogue, snippets, app versions, object explorer) but not the Administration tab, Dashboard, or audit log; `Admin` sees everything in the org. Bootstrap admin via `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env vars, applied only on a fresh database.
- **Microsoft Entra ID is the one federated sign-in, opt-in per organisation.** Email/password stays the default and the fallback; an org Admin turns Microsoft sign-in on from Administration → Identity. **The per-org tenant allow-list (`organization_settings.entra_allowed_tenant_ids`) is the security boundary** — the app registration is multi-tenant, so any Microsoft work account produces a valid token and the allow-list is the only thing that keeps strangers out. An org can additionally set `local_login_policy = EntraOnly` to refuse password login, password resets, and magic links for its members; SiteAdmin password login always survives as break-glass, and passkeys keep working. No other IdP, no SAML, no group-to-role mapping — adding a second provider is a conversation first. See `.design/auth-and-audit.md`.
- **One app container, one db container, named volumes per concern.** From P4.16, the data layer is Postgres in a sibling compose service backed by the `pg-data` named volume. Two more app-side volumes carry persisted state: `app-keys` for the Data Protection key ring (M17) and `app-backups` for `pg_dump` output (M18). Off-site object storage (S3 / Azure Blob, via `IOffsiteStorageProvider`) is a *sanctioned, opt-in* extension point for backups — configured per deployment, not a runtime dependency of the app. The live rule isn't "no S3/Redis ever"; it's **don't add a *new* external infra dependency (a broker, a cache, a third datastore) without asking first**.
- **SiteAdmin is a separate, cross-org role from Admin.** SiteAdmin (M17) sees `/site-admin/*` regardless of which org they belong to; Admin (M13) is org-scoped to its own org. The bootstrap admin is stamped `IsSiteAdmin = true`; later promotions come from `/site-admin/users`. The "last SiteAdmin" guard refuses to demote the final one. Pre-login flows and the SiteAdmin console call `IgnoreQueryFilters()` explicitly — everywhere else, the EF query filter on `AppDbContext` scopes to `IOrganizationContext.CurrentOrganizationId`.
- **System settings are a singleton row.** SMTP overrides, the signup-auto-approve default, the backup schedule and retention all live on the single `system_settings` row, managed via `SystemSettingsService`. The SMTP password column is encrypted with the Data Protection key ring; losing `app-keys` requires re-entering it. The `/site-admin/settings` form is the only writer.
- **Two operator endpoints.** `/healthz` (M21) is 200 when the database is reachable *and* the Data Protection key ring round-trips; 503 otherwise. `/readyz` (M21) is only green once startup work (migrations + first-run seed + bootstrap admin) has finished — reverse proxies should gate traffic on it. The Dockerfile `HEALTHCHECK` polls `/healthz`.
- **Generation is synchronous; the in-process background workers are the sanctioned async exception.** Workspace/extension generation is read-only against the DB and runs in memory — keep it that way (no queue, no job table). Everything heavier that legitimately runs off the request thread does so through an in-process channel-backed queue/worker pair — there are now four: `ReleaseImportQueue` (clone/compile/ingest, entered via `ProjectBuildImporter.StartBuildAsync`), `ProjectDiscoveryQueue`/`Worker`, `DeliveryQueue`/`Worker` (publishing builds to a BC environment — see `.design/saas-delivery.md`), and `OffsiteRestoreJobs`. All are in-process, so the "no external services" fence holds. Reuse one of *these* workers for new in-process background work; standing up an **external** queue or broker (Redis, a cloud queue) is still a conversation first.
- **No client-side framework beyond Blazor itself.** No React, no JS bundler. Tiny `.razor.js` companion files (like `ReconnectModal.razor.js`) are fine when needed.

If a milestone seems to demand crossing one of these lines, stop and confirm with the maintainer before doing it.

## Code conventions

These are the patterns the existing code has settled on. New code should match unless there's a reason to break.

### Services

- One class per service in `Services/`, registered as `Scoped` in `Program.cs`. EF context is scoped; services holding it must be too.
- Read methods return `Task<List<T>>` or `Task<T?>`. Write methods return `Task` and throw on validation failure (don't return result objects).
- Validation lives at the top of the service method, throws `PlanValidationException(Dictionary<string,string>)`. The form-layer validators are convenience; the service is the source of truth.
- Each service logs its outcomes at `Information` for successful operations with structured fields (workspace name, template key, file count, duration). Warnings for skippable problems (missing example folder); exceptions for refusals.

### Entities and value objects

- EF entities have public mutable properties because EF needs them. Initialise reference types to sane empty defaults (`= string.Empty`, `= new()`) so newly-constructed entities aren't `null`-laden.
- Value objects (`TemplateDefaults`, `AppSourceCopSettings`, etc.) are plain classes with `[JsonPropertyName]` annotations because they round-trip through `defaults_json` / `app_source_cop_json` and need to match AL's camelCase.
- Plans (`ProjectPlan`, `StandaloneExtensionPlan`, `DependencyEntry`) are `record`s — immutable, value equality, easy to compare in tests later.
- Soft-delete is `DeletedAt` (nullable). `Deprecated` is a separate boolean. They mean different things; don't conflate them. End-user dropdowns hide both; admin lists show deprecated and (with a toggle) deleted.

### Persistence

- All column and table names are snake_case, configured explicitly in `OnModelCreating`. Don't rely on EF's default naming.
- JSON value-object conversions use `HasConversion<JsonValueConverter>` with a single shared `JsonSerializerOptions`. Keep read and write options identical; otherwise round-trips drift.
- Indexes are declared in `OnModelCreating` (`(template_id, ordering)`, audit `(entity_type, entity_id, timestamp)`). Add new ones the same way.
- Migrations are committed to the repo. Run `dotnet ef migrations add <Name>` for every schema change; never edit a migration after it's been merged.
- **Re-stamp the migration timestamp after generating it.** Our migrations are hand-dated *into the future* (the `YYYYMMDDHHMMSS` prefixes run ahead of the wall clock), but `dotnet ef migrations add` stamps the new one with today's real date — which sorts it *before* the existing migrations, so EF applies it out of order and it fails (e.g. dropping a column a "later" migration hasn't created yet). After adding a migration, rename it to a timestamp just after the current latest: update the prefix on both the `.cs` and `.Designer.cs` files **and** the id inside the `[Migration("…")]` attribute (EF orders by that attribute, not the filename). Check `ls Data/Migrations/` for the highest existing prefix first and pick one **strictly greater** — don't reuse the current max. Two PRs in flight that both branch off the same base will each grab "current max + one day" and collide on merge; a handful of merged prefixes already share a value (e.g. `20260730000000`) because of exactly this. Those collisions are harmless — the full `[Migration]` id (prefix **plus** the name suffix) is what EF orders by and what keys `__EFMigrationsHistory`, so a shared numeric prefix with distinct names still sorts deterministically and never edit a merged migration to "fix" it — but they make the next author's "what's the highest prefix" ambiguous, so when you see a duplicate, step your new prefix clearly above the whole group. The model snapshot is order-independent, so it doesn't need touching.
- Startup runs `MigrateAsync()` and ensures the Default org exists with `IsSystem = true` (it's the singleton system org other orgs fork from). Both steps must remain idempotent — assume the app restarts often.

### Pages and components

- One page per route file. `@page` directive at the top, `@inject` services, `@code` block at the bottom for state and lifecycle.
- Hydrate state in `OnInitializedAsync`. Render `Loading…` / empty / data states explicitly — don't render an empty grid when the data is `null`.
- For form posts that return file downloads (Generate), use a minimal API endpoint in `Program.cs` rather than a Blazor component event — `FileStreamResult` with `Content-Disposition: attachment` is simpler than wrestling with `IJSRuntime` downloads. Always validate antiforgery first.
- CSS layers, in load order: `tokens.css` (the design system's token contract, byte-locked to the handoff copy), `components.css` / `shell.css` / `pages-*.css` (ported archetype sheets, also byte-locked — see the handoff section of `PROJECT.md` before editing any of them), then `app.css` for app-specific global rules and `Component.razor.css` for component-scoped styles. Tokens only — if you need a new colour, it goes through the design project, not a raw hex in a page sheet.
- Icons: Lucide SVGs vendored under `Resources/Icons/`, rendered inline by `Components/Shared/Icon.razor` via the singleton `IconCatalog`. No mixing icon families. The same icon name is used for the same concept across pages (e.g. `folder-plus` for "create workspace"). To add an icon, drop the SVG from lucide.dev (at the pinned version in `Resources/Icons/VERSION.txt`) into that folder — the csproj globs `*.svg` as embedded resources. A missing icon logs a warning and renders an invisible placeholder rather than throwing, but the catalogue test will fail the build if any call site references an icon that hasn't been vendored.

### Comments and docs

- XML `///` comments on public service methods, public entity properties whose meaning isn't obvious from the name, and tricky private helpers (mustache substitution, ID-range allocation). Explain *why* and *what's surprising*, not *what the code does*.
- Reference `.design/*.md` documents from code comments when behaviour is specified there — keeps maintainers from reverse-engineering decisions.
- Don't restate the design docs inside CLAUDE.md, code comments, or commit messages. Link, don't copy.

## Tests and verification

Milestone 12 stood up `ALDevToolbox.Tests/` and backfilled tests for the tricky algorithms — ID-range allocation, mustache substitution, audit snapshots, TOML round-trip, and the `PlanValidationException` field-key contract. Milestone P4.16 swapped the in-memory SQLite fixture for a real Postgres host (Testcontainers locally; service container in CI). Patterns are documented in `ALDevToolbox.Tests/README.md`.

The bar from M13 onward: every service method added ships with tests for the happy path and for any validation rule it introduces. Not a coverage metric — a posture. If the code has a rule, the rule has a test.

- `dotnet test` runs locally (no flags needed) and is part of CI (`.github/workflows/build.yml`). A red test run fails the build the same way a red compile does. xUnit v3 runs on Microsoft.Testing.Platform, so the old VSTest flags (`--filter "FullyQualifiedName~X"`, `--logger`) now exit 5 having run nothing — `ALDevToolbox.Tests/README.md` has the replacements.
- Verify generation by building a workspace, extracting the ZIP, and opening it in VS Code with the AL extension. The output structure must match `generation-engine.md`.
- Manual smoke test the end-user flows after touching shared services (generation, seed). Click through New Workspace, New Extension, Templates Browser.
- Local Docker run (`docker compose up`) before merging anything that touches startup, env vars, or volumes.

When picking which tests to add for a new feature, prefer tests that go through the public API (the service method, the endpoint, the round-trip) over tests that reach into private helpers. Internals will refactor; the contract shouldn't.

## Pull request hygiene

- One milestone per PR (or one coherent slice of one). Don't roll three milestones into a single review.
- Name branches with a type prefix that fits the work, slash-separated from a short kebab-case description: `feat/translator-xliff-editor`, `fix/audit-diff-empty-state`, `chore/bump-npgsql`, `docs/release-flow`, `refactor/generation-service`, `test/id-range-allocation`, `ci/ghcr-release`, `perf/object-explorer-ingest`. Use `feat` for a new user-visible capability, `fix` for a bug, `chore` for deps/tooling/housekeeping, `docs` for docs-only, `refactor` for behaviour-preserving restructuring, `test` for test-only work, `ci` for workflow/pipeline changes, `perf` for performance work. Pick the one that best describes the change; when a branch spans a couple, name it for the primary one.
- PR title: short, present tense ("Milestone 4: live preview"). Body: what changed, what was deliberately left out, how to verify.
- Commit messages explain *why*. The diff already shows *what*.
- If you change `.design/`, call it out in the PR body — design changes deserve review attention, not just the code.
- We squash-merge, so a merged branch shares no commit ancestry with main — `git log main..branch` will look "ahead" even when the content already landed. After a PR merges, that branch is done: start follow-up work from a fresh branch off main, never push new commits onto an already-merged branch. (The repo has *auto-delete head branches* on to enforce this.)
- Auditing whether a stray branch is unmerged means comparing *content*, not commits — check whether main already contains the equivalent change, since the squash drops the original SHAs.
- The squash rule is for PR-sized branches landing on main, and the `protect-main` ruleset enforces it (squash-only, linear history). A long-lived integration branch is the one place merge commits appear: merges *from* main into such a branch are merge commits, because that ancestry stops the same files re-conflicting on every subsequent merge (#595 is the worked example). The branch still *lands* on main as a squash (#596); the per-PR history stays readable through the landing PR's commit list.
- Releases are cut by tagging main — the process, version scheme, and staging previews are in `PROJECT.md`.

## When in doubt

- Smaller is better. The "Deliberately small" list at the bottom of `completed-milestones.md` is the tie-breaker.
- If you're about to add a feature flag, an interface, a queue, or a config knob "for the future" — don't. Add it when the future arrives. (This guards against *speculative cross-cutting* machinery. Building or iterating a tool, add the components, services, and variants that tool needs without ceremony — momentum inside a tool isn't what this is about.)
- Ask before crossing one of the **safety fences** (tenant isolation / `IgnoreQueryFilters()`, secrets and the Data Protection key ring, migration discipline) and before introducing a **new external dependency**. You do *not* need to ask to add a second variant *within* a tool you're building or iterating — prefer momentum there. Reserve the ask for changes that ripple across tools or touch a safety fence.
