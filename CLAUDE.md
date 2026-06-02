# CLAUDE.md

Guidance for working on this repository. Read this before touching code, especially when picking up a new milestone. The design lives in `.design/`; this file is about *how* we build, not *what* we build.

## Project at a glance

- **AL Dev Toolbox** — internal Blazor Server tool that generates AL/BC workspaces and standalone extensions from runtime templates.
- Stack: .NET 10, Blazor Server, EF Core 10 + Npgsql against PostgreSQL 18, Tomlyn. Lucide icons are vendored as embedded SVGs (no NuGet dependency); see `Resources/Icons/`.
- Two projects at the repo root: `ALDevToolbox/` (the app, layered by folder) and `ALDevToolbox.Tests/` (xUnit + FluentAssertions, established in Milestone 12). The solution file is `ALDevToolbox.slnx` at the repo root.
- Source of truth for behaviour: documents under `.design/`. If code disagrees with the design doc, fix one of them — don't leave them out of sync.

## Where things live

App folders are relative to `ALDevToolbox/`.

| Folder                       | What goes there                                                              |
|------------------------------|------------------------------------------------------------------------------|
| `Components/Pages/`          | Routable pages (one `.razor` per route).                                     |
| `Components/Layout/`         | Shell layout, sidebar, top bar, reconnect modal.                             |
| `Components/Shared/`         | Reusable components (`TabBar`, future `FolderTreePreview`, `DependencyPicker`). |
| `Endpoints/`                 | Minimal-API endpoint groups (`AccountEndpoints`, `GenerationEndpoints`, `SiteAdminEndpoints`, …) registered from `Program.cs` via `Map*Endpoints()` extensions. |
| `Services/`                  | Application services (`GenerationService`, `TemplateImportService`, `TemplateService`, …). |
| `Domain/Entities/`           | EF Core entity classes (mutable, persisted).                                 |
| `Domain/ValueObjects/`       | Immutable records / JSON-mapped value objects, exceptions, plans.            |
| `Domain/Seed/`               | Tomlyn POCOs that mirror the TOML schema for the admin editor and export.   |
| `Data/`                      | `AppDbContext`, design-time factory, migrations.                             |
| `Data/Configurations/`       | Per-entity `IEntityTypeConfiguration<T>` classes (one file per entity).      |
| `Resources/`                 | Embedded static assets (ruleset, `.gitignore` template).                     |
| `wwwroot/`                   | Global CSS, favicon.                                                         |

Test folders are relative to `ALDevToolbox.Tests/`.

| Folder            | What goes there                                                          |
|-------------------|--------------------------------------------------------------------------|
| `Builders/`       | Entity / plan builders pre-populated with sane defaults.                 |
| `Infrastructure/` | Reusable test plumbing (currently `TestDb` — Testcontainers / service-container Postgres fixture). |
| `Generation/`     | `GenerationService` tests.                                               |
| `Audit/`          | `AuditInterceptor` tests.                                                |
| `Toml/`           | `TemplateTomlMapper` tests.                                              |
| `Validation/`     | `PlanValidationException` field-key tests.                               |

When you add a new file, match the folder. Resist creating top-level folders — the layered split is intentional. Test patterns are documented in `ALDevToolbox.Tests/README.md`; new service tests should follow them.

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
- Don't introduce interfaces for services until there's a second implementation or a real test seam. `GenerationService` is a concrete class injected as itself; keep it that way until something forces the change.
- Reusable UI is what `Components/Shared/` is for. The design doc names the components we should pull out (`FolderTreePreview`, `DependencyPicker`, `AuditHistoryPanel`, `JsonEditor`) — use those names so they're easy to find.
- Three similar lines is fine. A premature abstraction over two callers is worse than the duplication.

### Always have the end user in mind

- Every list page renders three states: loading, empty (with a useful message that tells the user how to recover), populated. See `TemplatesBrowser.razor` for the shape.
- Forms validate on the server (the source of truth) **and** mirror the rules in HTML attributes (`pattern`, `required`, `min`) so users get instant feedback. Keep the two in sync — the regex in `GenerationService.WorkspaceNameRegex` and the `pattern=` on the input field must match.
- Validation errors return field-keyed dictionaries via `PlanValidationException` so the UI can render them inline next to the field. Don't throw plain strings for things the user typed.
- Helpful copy beats clever copy. Captions under fields, placeholders that show real examples ("e.g. Acme Customer"), error messages that say what to do next.
- Visual hierarchy: the **Generate** button is the only primary action on any page. Everything else is the outline button style. Don't introduce a second primary button.
- Keep the user's flow synchronous when it can be — generation runs in-process and streams the ZIP back. Don't add a job queue; if generation ever gets slow, fix the slow part.
- Loading states on long-running buttons (Generate, Export). Confirmation modals on destructive actions.

### Stay inside the architectural fences

These are deliberate constraints from `.design/architecture.md` and `.design/templates-and-seeding.md`. Don't quietly relax them.

- **The PostgreSQL database is the only persistence layer for templates, modules, the catalogue, per-folder file contents, organisations, users, signup requests, password reset tokens, login attempts, organisation settings, organisation assets, and organisation files.** Both authoring surfaces (the structured admin form and the TOML editor) write through the same `TemplateInput` pipeline into the DB. The on-disk `Templates.seed/` bootstrap was retired: the singleton **system org** (`organizations.is_system = true`, stamped on the Default org by migration `20260513000000_MoveSeedToSystemOrg`) holds the canonical templates that other orgs fork via `TemplateImportService`. New orgs start empty; admins import on demand from `/admin/templates`.
- **The ruleset and `.gitignore` ship as code.** They live as embedded resources under `Resources/` because they're per-deployment policy. Per-folder example AL file *contents* live in the `template_files` table and are admin-editable. The logo, organisation defaults block, and always-included file list live in the database (`organization_assets`, `organization_settings`, `organization_files`) and are admin-editable. Binary files inside template folders are out of scope for v1 — text content only.
- **`defaults_json` and `app_source_cop_json` stay as JSON columns.** Don't normalise them into separate tables — the AL ecosystem changes those shapes too often.
- **Multi-tenant by default.** Every editable entity carries an `organization_id`. EF query filters on `AppDbContext` scope reads to `IOrganizationContext.CurrentOrganizationId`; pre-login flows that genuinely need cross-org reads (login, signup, bootstrap) call `IgnoreQueryFilters()` explicitly. Service code that mutates state must run inside an authenticated request — `RequireOrganizationId()` throws otherwise.
- **`IgnoreQueryFilters()` is the tenant-isolation fence.** The EF query filter is the *only* thing that keeps a request from one org's user reading another org's data. Every existing `IgnoreQueryFilters()` call site is deliberate and reviewed: pre-auth flows (login, signup, password reset, bootstrap), the SiteAdmin console (`/site-admin/*`), the `OAuthClaimsTransformer` user-lookup that validates the token's `org` claim, and migrations. **Never add a new `IgnoreQueryFilters()` call without explicit confirmation from the maintainer** — especially not inside an MCP tool, an admin service, an endpoint, or anything that runs under a normal authenticated request. If a query feels like it needs to escape the filter, the answer is almost always to scope it tighter, not to remove the fence. The same rule applies to constructing an `AmbientOrganizationContext` with someone else's org id from inside a request — don't.
- **Email/password accounts, three roles (`User`, `Editor`, `Admin`), admin-approved signups.** `User` uses the generator only; `Editor` additionally sees the content-authoring admin pages (templates, modules, catalogue, snippets, app versions, object explorer) but not the Administration tab, Dashboard, or audit log; `Admin` sees everything in the org. No external IdP. Bootstrap admin via `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env vars, applied only on a fresh database.
- **One app container, one db container, named volumes per concern.** From P4.16, the data layer is Postgres in a sibling compose service backed by the `pg-data` named volume. Two more app-side volumes carry persisted state: `app-keys` for the Data Protection key ring (M17) and `app-backups` for `pg_dump` output (M18). No external services beyond that — no Redis, no S3.
- **SiteAdmin is a separate, cross-org role from Admin.** SiteAdmin (M17) sees `/site-admin/*` regardless of which org they belong to; Admin (M13) is org-scoped to its own org. The bootstrap admin is stamped `IsSiteAdmin = true`; later promotions come from `/site-admin/users`. The "last SiteAdmin" guard refuses to demote the final one. Pre-login flows and the SiteAdmin console call `IgnoreQueryFilters()` explicitly — everywhere else, the EF query filter on `AppDbContext` scopes to `IOrganizationContext.CurrentOrganizationId`.
- **System settings are a singleton row.** SMTP overrides, the signup-auto-approve default, the backup schedule and retention all live on the single `system_settings` row, managed via `SystemSettingsService`. The SMTP password column is encrypted with the Data Protection key ring; losing `app-keys` requires re-entering it. The `/site-admin/settings` form is the only writer.
- **Two operator endpoints.** `/healthz` (M21) is 200 when the database is reachable *and* the Data Protection key ring round-trips; 503 otherwise. `/readyz` (M21) is only green once startup work (migrations + first-run seed + bootstrap admin) has finished — reverse proxies should gate traffic on it. The Dockerfile `HEALTHCHECK` polls `/healthz`.
- **Synchronous generation.** No background workers, no queues. Generation is read-only against the DB and happens in memory.
- **No client-side framework beyond Blazor itself.** No React, no JS bundler. Tiny `.razor.js` companion files (like `ReconnectModal.razor.js`) are fine when needed.

If a milestone seems to demand crossing one of these lines, stop and confirm with the maintainer before doing it.

## Conventions established by milestones 1–3

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
- Startup runs `MigrateAsync()` and ensures the Default org exists with `IsSystem = true` (it's the singleton system org other orgs fork from). Both steps must remain idempotent — assume the app restarts often.

### Pages and components

- One page per route file. `@page` directive at the top, `@inject` services, `@code` block at the bottom for state and lifecycle.
- Hydrate state in `OnInitializedAsync`. Render `Loading…` / empty / data states explicitly — don't render an empty grid when the data is `null`.
- For form posts that return file downloads (Generate), use a minimal API endpoint in `Program.cs` rather than a Blazor component event — `FileStreamResult` with `Content-Disposition: attachment` is simpler than wrestling with `IJSRuntime` downloads. Always validate antiforgery first.
- CSS lives in `wwwroot/app.css` for global rules and `Component.razor.css` for component-scoped styles (Blazor's CSS isolation handles the rest). Stick to the CSS custom properties at the top of `app.css` — if you need a new colour, add a variable.
- Icons: Lucide SVGs vendored under `Resources/Icons/`, rendered inline by `Components/Shared/Icon.razor` via the singleton `IconCatalog`. No mixing icon families. The same icon name is used for the same concept across pages (e.g. `folder-plus` for "create workspace"). To add an icon, drop the SVG from lucide.dev (at the pinned version in `Resources/Icons/VERSION.txt`) into that folder — the csproj globs `*.svg` as embedded resources. A missing icon logs a warning and renders an invisible placeholder rather than throwing, but the catalogue test will fail the build if any call site references an icon that hasn't been vendored.

### Comments and docs

- XML `///` comments on public service methods, public entity properties whose meaning isn't obvious from the name, and tricky private helpers (mustache substitution, ID-range allocation). Explain *why* and *what's surprising*, not *what the code does*.
- Reference `.design/*.md` documents from code comments when behaviour is specified there — keeps maintainers from reverse-engineering decisions.
- Don't restate the design docs inside CLAUDE.md, code comments, or commit messages. Link, don't copy.

## Keeping the AL reference extractor's allow-lists current

The Object Explorer's reference extractor (`Services/Al/AlReferenceExtractor.cs`) reports an Unresolved count after each Phase-2 import. New BC releases occasionally ship new built-in methods, scalar types, runtime APIs, or platform virtual tables that need to land in our allow-lists to keep that number trustworthy. Two files cover the surface:

- **`Services/Al/AlBuiltinMethods.cs`** — every category of "built-in name we expect to skip" (method sets per receiver kind, scalar types, system functions, statement keywords, DSL keywords, static-receiver names). The class-level doc-comment has a labelled `EXTENDING WHEN MICROSOFT ADDS NEW METHODS / TYPES` checklist mapping each kind of addition to the right `HashSet`.
- **`Services/ObjectExplorer/ReleaseImportService.cs`** — `PlatformVirtualTables` (the named id → name map for the `2000000001..2000000999` runtime tables) and `FoundationalAppNames` (Microsoft umbrella apps every extension implicitly depends on). Both have `EXTENDING` notes at their definition.

`AlReferenceExtractor.IsPlatformVirtualTableId` is the range-check safety net for the platform-table ids — even if a numeric id isn't named, the diagnostic silences. Add to the named list when the symbol package resolves the id to a name (so `Record Field`-style chains work), not just to silence noise.

When new noise patterns appear in the Phase-2 sample log, prefer extending one of these allow-lists over adding bespoke code paths to the walker. The diagnostic itself (`AlReferenceExtractor.CaptureUnresolved`) is intentionally cheap and structured so operators can grep the log by `Reason=` and trace each new bucket back to a list above.

The legacy **C/AL TXT** ingest path (`Services/Cal/`) has its own parallel allow-list — **`Services/Cal/CalBuiltinMethods.cs`** — because classic C/AL's runtime surface and casing differ from AL (uppercase `SETRANGE`/`FINDFIRST`, `FIND('-')`, the `DATABASE::`/`CODEUNIT::` static receivers). Its class-level doc-comment carries the same `EXTENDING WHEN A NEW C/AL RELEASE ADDS NAMES` checklist mapping each kind of addition to the right `HashSet` (`ReceiverMethods`, `BareFunctions`, `FieldNameTakingMethods`, `StaticReceivers`, `Keywords`). `CalReferenceExtractor` counts unresolved receivers the same way; extend this list — not the walker — when a real C/AL export surfaces a new built-in as noise.

## Keeping MCP parity with the web UI

The MCP server (`Services/Mcp/Tools/*Tools.cs`) is a parallel front-end on the same services the Blazor pages use — agents reach the Object Explorer (and friends) through these tools. When you add a feature that's user-visible in the web UI — a new reference kind, an outline section, a derived relationship, a filter — check whether it should also show up through MCP, and wire it through in the same PR. Two patterns matter:

- **Service-level features come for free.** If the new behaviour lives behind an existing service method (e.g. `FindReferencesAsync` matching a new `reference_kind`), the matching MCP tool usually picks it up automatically. Verify it actually reaches the MCP path — the tool may call a sibling method that doesn't see the new bucket.
- **New DTOs and query paths need plumbing.** When a feature lands a new field on a DTO (e.g. `ObjectOutline.ImplementedBy`) or a separate query method (`FindReferencesForSymbolAsync` vs `FindReferencesAsync`), the MCP tool has to be updated to populate the field or route to the right query. Otherwise the web UI shows the relationship and MCP agents stay blind to it.

Skip the MCP path only when it genuinely doesn't apply — pure UI affordances (resizers, badge styling, keyboard shortcuts), authoring flows that already have a dedicated MCP tool, or per-org admin pages that aren't part of the AL-reading surface. When in doubt, expose it through MCP; agents tend to want the same answers humans do.

## Working with the design docs

`.design/` is the spec. Treat it as the contract:

- `architecture.md` — stack and layering decisions, request flow.
- `domain-model.md` — every table, column, validation rule.
- `generation-engine.md` — what the ZIP must look like and how to build it.
- `templates-and-seeding.md` — TOML schema and the seed contract.
- `auth-and-audit.md` — how the password gate and audit interceptor work.
- `ui-design.md` — page layout, copy, components to factor out.
- `completed-milestones.md` — the record of what each shipped milestone added (M1–M21).
- `roadmap.md` — uncommitted forward-looking ideas (successor to the old `milestones.md` plan).

When implementing a milestone:

1. Re-read the relevant design docs first.
2. If the design says something the code can't easily satisfy, write the question into the PR description and pause for input rather than improvising.
3. If a design choice has aged badly, update the design doc in the same PR as the code change — don't leave the doc claiming something the code no longer does.

## Tests and verification

Milestone 12 stood up `ALDevToolbox.Tests/` (xUnit + FluentAssertions) and backfilled tests for the tricky algorithms — ID-range allocation, mustache substitution, audit snapshots, TOML round-trip, and the `PlanValidationException` field-key contract. Milestone P4.16 swapped the in-memory SQLite fixture for a real Postgres host (Testcontainers locally; service container in CI). Patterns are documented in `ALDevToolbox.Tests/README.md`.

The bar from M13 onward: every service method added ships with tests for the happy path and for any validation rule it introduces. Not a coverage metric — a posture. If the code has a rule, the rule has a test.

- `dotnet test` runs locally (no flags needed) and is part of CI (`.github/workflows/build.yml`). A red test run fails the build the same way a red compile does.
- Verify generation by building a workspace, extracting the ZIP, and opening it in VS Code with the AL extension. The output structure must match `generation-engine.md`.
- Manual smoke test the end-user flows after touching shared services (generation, seed). Click through New Workspace, New Extension, Templates Browser.
- Local Docker run (`docker compose up`) before merging anything that touches startup, env vars, or volumes.

When picking which tests to add for a new feature, prefer tests that go through the public API (the service method, the endpoint, the round-trip) over tests that reach into private helpers. Internals will refactor; the contract shouldn't.

## Pull request hygiene

- One milestone per PR (or one coherent slice of one). Don't roll three milestones into a single review.
- PR title: short, present tense ("Milestone 4: live preview"). Body: what changed, what was deliberately left out, how to verify.
- Commit messages explain *why*. The diff already shows *what*.
- If you change `.design/`, call it out in the PR body — design changes deserve review attention, not just the code.
- We squash-merge, so a merged branch shares no commit ancestry with main — `git log main..branch` will look "ahead" even when the content already landed. After a PR merges, that branch is done: start follow-up work from a fresh branch off main, never push new commits onto an already-merged branch. (The repo has *auto-delete head branches* on to enforce this.)
- Auditing whether a stray branch is unmerged means comparing *content*, not commits — check whether main already contains the equivalent change, since the squash drops the original SHAs.

## When in doubt

- Smaller is better. The "Deliberately small" list at the bottom of `completed-milestones.md` is the tie-breaker.
- If you're about to add a feature flag, an interface, a queue, or a config knob "for the future" — don't. Add it when the future arrives.
- Ask before crossing an architectural fence. Ask before introducing a new dependency. Ask before adding a second way to do something the codebase already does.
