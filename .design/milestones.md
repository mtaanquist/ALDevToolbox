# Milestones — Phases 3 and 4

Phases 1 and 2 are done; their milestone log lives in `completed-milestones.md`. This file picks up where that left off.

Phase 3 is a deliberate departure from three constraints the earlier design pinned down. We're crossing those fences on purpose, with eyes open:

- **Multi-tenancy.** Phase 1 declared single-tenancy. Phase 3 introduces organisations as a real boundary — every editable row gains an `organization_id`, queries scope to it, audit scopes to it. The "Out of scope for v1" note about multi-tenancy in `completed-milestones.md` is superseded.
- **Accounts and roles.** Phase 1's `auth-and-audit.md` describes a single shared password. Phase 3 replaces it with email/password accounts, two roles (`User`, `Admin`), and an admin-approved signup flow. That document needs to be rewritten as part of M13.
- **Logo and always-included files as embedded resources.** The "ruleset, `.gitignore`, and logo ship as code" constraint in `CLAUDE.md` is intentionally relaxed for the logo and per-org always-included files (`workspace.json` skeleton, etc.). The ruleset and `.gitignore` template stay as embedded resources for now — they're per-deployment policy, not per-organisation.

When this phase finishes, those design documents (`auth-and-audit.md`, `architecture.md`, `domain-model.md`, `CLAUDE.md`) should reflect the new reality. Update them in the same PRs as the code change, not after.

The order matters: tests land first because M13 introduces a security boundary that can't be validated by clicking around, and we want the patterns established before that work begins.

## Milestone 12 — Test foundation

Goal: stand up a test project, establish the patterns, and backfill tests for the bits of existing logic that will hurt most if they break under future changes. This is foundation work — no behaviour changes — but it's the prerequisite for the rest of Phase 3 doing its job.

### Project

- New `ALDevToolbox.Tests` project alongside the main project. xUnit + FluentAssertions; `Microsoft.EntityFrameworkCore.Sqlite` with an in-memory connection (`Filename=:memory:`) for DB-touching tests, not the EF in-memory provider — we want the same SQL behaviour the app sees in production.
- Test data builders for `RuntimeTemplate`, `Module`, `WellKnownDependency`, plans. Keep them small; resist the urge to build an ORM-on-top-of-an-ORM. The pattern is "construct an entity with sensible defaults, override the fields the test cares about."
- CI: extend `.github/workflows/build.yml` to run `dotnet test` after build. A red test run fails the build the same way a red compile does.

### Backfill targets

Cover the algorithms `completed-milestones.md` flagged as "non-obvious" — things a future contributor could break without realising:

- `GenerationService` ID-range allocation, including the override path.
- Mustache substitution edge cases: missing keys, escaping, nested folders, the "no examples" toggle.
- `AuditInterceptor` snapshots — modified, deleted, and added entities; principal-with-collections snapshotting (templates with folders+files, modules with dependencies).
- `TemplateTomlMapper` round-trip (TOML → entity → TOML) for the seed schema, including `[[folders.files]]` blocks, default modules, and application-version keys.
- `PlanValidationException` field-keyed errors surface correctly through the form posts.

### Bar for new code

After this milestone, every service method added in M13–M15 ships with tests for the happy path and for any validation rule it introduces. This isn't a coverage metric — it's a posture: if the code has a rule, the rule has a test.

### Done when

- `dotnet test` runs green locally and in CI.
- A reviewer breaking a backfilled invariant (e.g. removing the `from <= to` ID-range check) sees a red test before they see a red feature.
- The test patterns are documented in a short `ALDevToolbox.Tests/README.md` so M13 onward can copy them rather than reinvent.

## Milestone 13 — Organisations and accounts

Goal: replace the shared-password gate with real accounts, scoped to organisations. Two roles. Admin-approved signups. Forgot-password by email. Login hardening and account self-service in the same milestone — they're the same surface area and splitting them invites half-finished UX.

### Domain

- New tables: `organizations` (id, name, slug, created_at), `users` (id, organization_id, email, password_hash, display_name, role, status, created_at, last_login_at), `signup_requests` (id, organization_id, email, requested_at, decided_at, decided_by_user_id, decision), `password_reset_tokens` (user_id, token_hash, expires_at, consumed_at), `login_attempts` (id, email, ip, succeeded, timestamp) for rate-limiting and lockout.
- `users.role` is `User` or `Admin`. `users.status` is `Pending`, `Active`, or `Disabled`. The shared-password code path is removed entirely — no compatibility shim.
- Add `organization_id` (NOT NULL, FK) to every editable entity that is currently global: `runtime_templates`, `template_folders`, `template_files`, `modules`, `module_dependencies`, `well_known_dependencies`, `audit_log_entries`, and anything M14 adds. Indexes on `(organization_id, …)` everywhere we currently index on the second column alone. EF query filters enforce the boundary; no service code reads without an org scope.
- Update `domain-model.md` to describe the new tables, the FKs, and the per-org uniqueness constraints (template `key` is unique per org, not globally).

### Migration

- Structural migration — existing single-tenant data has no organisation. The migration creates a default organisation ("Default" / slug `default`), backfills every existing row's `organization_id` to it, and stamps the existing seed data as belonging to that org. Document the steps in the migration's XML doc comment.
- The `ADMIN_PASSWORD` / `ADMIN_PASSWORD_FILE` env variables stop being read. On first run after this migration, if there are zero users, bootstrap a single admin account from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env variables, attached to the default organisation, then never read those variables again. Log a warning if the bootstrap variables are still set on a later boot.

### Auth

- ASP.NET Core Identity is overkill for our shape; roll a thin `AccountService` over the existing EF context. Argon2id password hashing (via `Konscious.Security.Cryptography.Argon2` or `BCrypt.Net-Next` — pick one and document why). No external IdP.
- Cookie auth stays, but the cookie now carries `user_id` and `organization_id` claims (and the role claim derived from `users.role`). `IHttpContextAccessor` consumers read these instead of the bare display name.
- Routes: `/login`, `/signup`, `/forgot-password`, `/reset-password?token=…`, `/account`. The signup form takes email + display name + password + an optional organisation slug; if the slug matches an existing org, the signup attaches to it (pending approval); if blank or unknown, the signup creates a new pending organisation.
- `[Authorize]` everywhere admin pages already have it; new `[Authorize(Roles = "Admin")]` on the admin-only edit/delete actions. End-user pages (the generators) stop being anonymous — every signed-in `User` or `Admin` can use them. Anonymous users are redirected to `/login`.

### Login hardening

- Per-email rate limit on `/login`: max 10 attempts per 15 minutes; after 5 consecutive failures the account locks for 15 minutes (track via `login_attempts`). Successful login clears the streak.
- Per-IP rate limit as a coarser backstop: max 30 login attempts per 15 minutes per IP, regardless of email.
- `/forgot-password` is rate-limited per email *and* per IP — same limits — so the SMTP relay isn't a spam vector. Always show "If that email exists, you'll receive a reset link" regardless of outcome; don't reveal whether the email is registered.
- Reset tokens are single-use, expire after 1 hour, and are stored as `sha256(token)` so a DB read doesn't yield usable tokens.
- Password complexity is intentionally light: minimum 12 characters, no other rules. Length beats classes.

### Approvals

- New admin page `/admin/users` lists pending and active users for the current organisation, with approve / reject / disable / role-change actions. Audit every action.
- Cross-organisation: an admin only ever sees their own organisation's users. There is no superuser. If we need cross-org administration later, that's Phase 4.

### New-organisation provisioning

- When a signup creates a new pending organisation (slug doesn't match an existing one), the org starts empty — no templates, no modules, no catalogue. On first admin login after approval, run the standard `SeedService` against that org's `organization_id`. The seed code already knows how to populate from `Templates.seed/`; this milestone teaches it to do so for a specific org rather than globally.
- The default organisation's content stays put; we don't copy from "default" into new orgs. Once M14's export/import lands, an admin can pull a snapshot from one org and apply it to another if they want to.

### Account self-service

- `/account` page: change password (requires current password), change display name, see active sessions (just current — no multi-session bookkeeping), sign out. All audited.
- "Leave organisation" available to any user who is *not* the last active admin in the org. Prevents an org from becoming unadministerable.
- "Delete account" available to any user. If the user is the last active admin in their org, they must either promote another user to admin first or accept that the organisation will be marked for deletion (which cascades to its content via FK on the entities they own — call this out clearly in the confirmation modal). Audited.

### Email (SMTP)

- New `EmailService` reading SMTP config from env: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD_FILE`, `SMTP_FROM`, `SMTP_USE_STARTTLS`. Use `MailKit` — `System.Net.Mail.SmtpClient` is officially obsolete.
- Three transactional templates, all rendered server-side from Razor partials under `Components/Email/`:
  - **Forgot password** — sent to the user with a single-use reset link valid for 1 hour.
  - **Signup pending** — sent to every active admin in the target organisation when a new signup arrives.
  - **Signup decided** — sent to the requester after an admin approves or rejects.
- `EmailService.SendAsync` is `async` end-to-end and takes a `CancellationToken`. Failures log a warning but do not block the underlying action (a failed approval email shouldn't roll back the approval).
- If SMTP is not configured (`SMTP_HOST` blank), the service throws on send and the calling code surfaces "Email is not configured; ask an admin." rather than silently swallowing. Fail loudly so misconfiguration is visible.

### UI

- Login / signup / forgot-password / reset-password / account pages styled to match the existing shell. Same field-keyed validation pattern as the generator forms.
- Top bar: "Signed in as Bob (Acme) — Admin" with a sign-out menu. Replace the existing single-shared-password indicator.
- Admin sidebar gains "Users" alongside Templates / Modules / Catalogue / Audit.

### Audit

- `audit_log_entries.changed_by` already takes a string; it now stores `"display_name <email>"` of the acting user. Add `changed_by_user_id` and `organization_id` columns alongside it for queryability.
- Every approval / rejection / role change / disable / self-service password change / account deletion writes an audit row.

### Tests

- M12 patterns apply: every new service method has tests for happy path and validation rules. Specific must-haves:
  - Cross-org reads return nothing (EF filter test).
  - Cross-org writes throw or are rejected at the service layer (don't trust the URL).
  - Argon2 hash round-trip; reset token single-use + expiry.
  - Rate-limit and lockout logic against a fake clock.
  - Last-admin-leaves protection.

### Done when

- A fresh deployment with `BOOTSTRAP_ADMIN_*` env vars boots, lets the admin sign in, and shows them an empty "Users" page in their default organisation.
- A second user can sign up against an existing org, the admin gets an email, approves, and the new user can sign in and use the generator (but not the admin pages).
- A signup against a brand-new slug creates a pending org; on approval, that org gets seeded with the standard `Templates.seed/` content.
- Forgot-password produces a working reset link and a clear failure message if SMTP is unconfigured.
- Two organisations cannot see each other's templates, modules, catalogue, or audit log — verified by a test, not just by clicking around.
- A user can change their own password and display name; an admin who is the last admin in their org cannot leave or delete their account without first promoting someone else.

## Milestone 14 — Organisation configuration

Goal: the things an organisation needs to customise — publisher, ID ranges, default briefs, logo, always-included file contents — live in the database and are editable through an admin configuration page. The previously-embedded versions are removed. Export/import grows to cover the per-org config so backups are complete.

### Domain

- New `organization_settings` table (one row per organisation): `default_publisher`, `default_id_range_from`, `default_id_range_to`, `default_brief`, `default_core_description`. Validation matches existing `GenerationService` rules.
- New `organization_assets` table: `id`, `organization_id`, `kind` (`Logo`), `content_type`, `content` (BLOB), `updated_at`. One logo per organisation; uploading replaces.
- New `organization_files` table: `id`, `organization_id`, `path`, `content`, `mustache_enabled`, `ordering`, `updated_at`. Always-included text files written into every generated workspace (and standalone extension where applicable). The `workspace.json` skeleton is the obvious first row; admins can add more (a `.editorconfig`, a per-org `README.md`). Mustache substitution runs when `mustache_enabled` is true, using the same context as per-template files.
- Generation reads org-scoped settings/files/assets from the acting user's organisation. Workspace-level files are written before per-extension folders, so per-template files can override on path collision; document that precedence in `generation-engine.md`.

### Migration

- Drop the embedded `Resources/Logo.svg` and the embedded `workspace.json` template (and any other always-included file currently shipping as a resource). The seed flow on first run for a *new* organisation populates `organization_files` and `organization_assets` from the contents that used to live under `Resources/` — moved into `Templates.seed/organization-defaults/` for the seed step.
- The migration backfills the default organisation's settings/files/logo from the previously-embedded values so existing deployments don't lose anything.
- The ruleset and `.gitignore` template stay as embedded resources for now. They're closer to per-deployment policy than per-org config and don't have a clear customisation story yet. Note that explicitly in the migration so a future maintainer doesn't assume it was an oversight.

### Services

- New `OrganizationConfigService` for reads and writes. Generation gets the org's settings/files/logo via this service; no more direct `Resources/` reads from `GenerationService`.
- Caching: a per-organisation in-memory cache keyed on `organization_id` with invalidation on save. A `ConcurrentDictionary` is fine — the volume is tiny.
- Validate uploaded logos: max 256 KB, content type `image/svg+xml` or `image/png`, basic SVG sanitisation (strip `<script>` and `on*` attributes). Reject anything else with a clear field-keyed error.

### Export / import

- `ExportService` (built in M10) walks templates/modules/catalogue. Extend it to also include `organization_settings`, `organization_files`, and `organization_assets` for the current organisation. Logo bytes go in as base64 in the TOML — small enough at 256 KB cap.
- Import grows the inverse path: an admin can paste an exported TOML into a fresh org (or a wipe-and-replace import on an existing one) and have the full config restored. Import requires explicit confirmation when it overwrites — same modal pattern as delete.
- Round-trip test: export → wipe org → import → diff equals zero (modulo timestamps).

### UI

- New `/admin/configuration` page with three sections:
  1. **Defaults** — publisher, ID range, brief, core description. Inline validation, save button per section.
  2. **Logo** — current logo preview, upload control, "revert to seed default" button.
  3. **Always-included files** — list with path + content editor per row, reorder, delete, add. Reuse `Components/Shared/TemplateFileEditor.razor` from M8.5.
- The end-user generator forms read defaults from the configuration service, so a fresh New Workspace form arrives pre-filled. Users can still edit on the form; the config provides defaults, not locks.

### Audit

- All sections audit through the existing interceptor. Logo audit snapshots the content hash and content type — never the bytes — same pattern as `template_files`.

### Tests

- Generation reads from the right org's configuration (cross-org config doesn't leak).
- Logo upload validates content-type and size; SVG sanitiser strips scripts and event handlers.
- Export → import round-trip preserves settings, files, and logo bytes.

### Done when

- An admin can change the org's default publisher, save, and see the new value pre-filled on a fresh New Workspace form.
- Uploading a logo replaces it on the next generation; the previous logo bytes are gone (no soft-delete on assets, just the audit hash).
- Adding a new always-included file (say `.editorconfig`) makes it appear in every subsequently-generated workspace under the workspace root, with mustache substitution if enabled.
- Two organisations can have different logos, defaults, and always-included files without leaking into each other.
- The codebase no longer reads `Resources/Logo.svg` or any always-included file from disk at generation time — `grep`-verifiable.
- Export → wipe → import preserves everything visible in the admin UI.

## Milestone 15 — Polish pass

Goal: a deliberate sweep across the whole codebase — not feature work, not new milestones disguised as polish. The bar is "a new contributor reading this in six months understands what's going on."

This milestone is read-only on behaviour: nothing here should change what the app does. If a polish change risks behavioural drift (e.g. consolidating two near-identical helpers), call that out in the PR and verify against the relevant flow.

### Code-level pass

- DRY review: walk every `Services/` class and every `Components/` page, list near-duplicate methods (validation helpers, file-walking helpers, mustache contexts). Factor out the second occurrence; leave first occurrences alone. Three similar lines is fine; two callers is rarely worth a shared helper.
- Idiomatic C#: nullable annotations are tight (no `string?` where `string` is the truth), `async` is end-to-end (no `.Result` / `.Wait()`), `AsNoTracking()` is on every read-only EF query, structured logging uses named placeholders. Run with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` enabled to surface anything we've been ignoring.
- Comments review: read every `///` and `//` comment. If it explains *what* the code does and the identifier already says it, delete it. If it explains *why* and the reason has aged out, update or delete. If a tricky helper has no comment and a stranger would stall on it, add one. CLAUDE.md guidance is the bar.
- Magic numbers and strings: anything that appears in two places and means the same thing becomes a named constant in the file that owns the concept.
- Dead code: any `internal` or `private` member with no callers gets deleted. Any `public` API with no callers either gets called or deleted; "in case" violates YAGNI.

### Cross-cutting consistency

- Every list page renders loading / empty / populated states.
- Every form posts a field-keyed `Dictionary<string,string>` on validation failure, never a single concatenated string.
- Every `Task`-returning method in `Services/` accepts and threads a `CancellationToken`. Verify and fix the holdouts.
- Every audited mutation actually goes through the interceptor (no direct SQL, no `ExecuteUpdate` bypass). Spot-check the M13 / M14 services.
- M12 test patterns are applied retroactively where M13/M14 didn't already do so. Aim for the bar "every service has a happy-path test", not a coverage percentage.

### Documentation

- Update `architecture.md`, `auth-and-audit.md`, `domain-model.md`, `templates-and-seeding.md`, `generation-engine.md`, `ui-design.md`, and `CLAUDE.md` to match the post-M14 reality. Anything stale gets rewritten in place — don't leave a "this changed in phase 3" footnote.
- Repo `README.md`: how to run, how to bootstrap an admin, how to configure SMTP, how to back up the SQLite file. Drop anything that's no longer true.
- Migration history: a short note in `.design/` summarising what each migration did.

### Done when

- `dotnet build` is green with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<Nullable>enable</Nullable>`.
- A reviewer can read any service class top-to-bottom without consulting the design docs to follow what it does — the code reads as intended behaviour and the docs answer "why this shape".
- The design docs and code agree on every architectural fence; no document still claims a constraint the code has moved past.
- Manual smoke test of every end-user flow (New Workspace, New Extension, Templates Browser) and every admin flow (Templates, Modules, Catalogue, Audit, Users, Configuration, Export, Import) passes against a fresh database and against an upgraded one.

# Phase 4

Phase 4 sets up the foundation for managed multi-organisation hosting. The "single SQLite file, single container, single volume" fence in `architecture.md` is intentionally relaxed: SQLite is replaced entirely with PostgreSQL 18, the deployment becomes two containers (app + db) plus three named volumes (`pg-data`, `app-keys`, `app-backups`), and a SiteAdmin role gains cross-organisation read access for support and operational management. The constraint that there are no external services holds — backups land on a mounted volume, not in object storage; SMTP routes through whatever relay the operator configures, but the application doesn't depend on cloud services beyond the database it ships with.

SSO / OIDC and a mobile / narrow-viewport layout are explicitly off the table for this phase. The first pulls Phase 4 toward identity work that's better done as its own phase; the second is desktop-first by deliberate scope and would dilute the hosting-readiness theme. Both stay on the Phase 5 candidates list.

When this phase finishes, `architecture.md`, `domain-model.md`, `auth-and-audit.md`, and `CLAUDE.md` reflect the Postgres-default, SiteAdmin-aware, hybrid-SMTP world. Update them in the same PRs as the code, not after.

The order matters: M16 lands the database swap before anything else builds on it. M17 introduces the SiteAdmin role and the system-settings surface that M18 and M19 both extend. M18 layers backup tooling on top of the now-Postgres world. M19 leans on the hybrid SMTP from M17 to push more email-driven flows through it. M20 polishes the audit story (which M16–M19 all write to) and cleans up admin-list ergonomics. M21 closes the phase with cross-cutting cleanup that no individual milestone can see in isolation.

## Milestone 16 — Postgres-only backend

Goal: replace SQLite with PostgreSQL 18 as the only supported database. Single migration history, single provider in the codebase, prod-shaped tests. Sets the foundation for managed hosting.

### Compose

- Add a `db` service running `postgres:18-alpine` with a named volume `pg-data`. The `app` service depends on `db` with a healthcheck. Default credentials via `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`; the application reads `ConnectionStrings__DefaultConnection`.
- The "single SQLite file, single container, single volume" fence in `architecture.md` is intentionally relaxed — same spirit (no external services), different shape (one app container, one db container, named volumes for data, keys, and backups).

### Provider swap

- Drop `Microsoft.EntityFrameworkCore.Sqlite`; add `Npgsql.EntityFrameworkCore.PostgreSQL`. `Program.cs` calls `UseNpgsql` unconditionally — no provider toggle, no dual history.
- `defaults_json` and `app_source_cop_json` move from `TEXT` to `jsonb`. Keep the value-converter approach so the C# side is unchanged. No JSONB indexes yet — add one when a query needs it, not before.
- `AuditInterceptor` verified provider-agnostic. Pin `DateTime` columns to UTC explicitly in `OnModelCreating`; Npgsql's default `timestamp with time zone` behaves differently than SQLite's TEXT.

### Migrations

- Delete the existing `Migrations/` folder and regenerate from a single `InitialCreate` against Postgres. Acceptable because no production deployment depends on schema continuity, and we're explicitly not preserving audit log history across the move.
- The M14 SQLite-specific `__ef_temp_*` table rebuilds disappear — Postgres supports `ALTER TABLE … DROP COLUMN` natively. Net-negative LOC.

### Tests

- Replace `Filename=:memory:` with Testcontainers (`Testcontainers.PostgreSql`). The `TestDb` fixture spins up a container per test collection, applies migrations once, and is reused across tests in the collection.
- CI in GitHub Actions uses `services: postgres:18` directly — faster than Testcontainers when the runner already provides a service container. Both paths apply the same migrations.

### Migration path for existing self-hosted users

- Documented one-way path: export from the previous SQLite tag via `/admin/configuration` → fresh `compose up` on the new tag → import. Audit log history is not preserved; called out in the upgrade notes.

### Documentation

- `architecture.md` updated for the Postgres compose layout. `domain-model.md` notes `jsonb` columns. README quickstart updated. New `migrating-from-sqlite.md` appendix walks the export-then-import path.

### Done when

- `docker compose up` brings up Postgres + app cleanly against an empty volume; seed runs; bootstrap admin works.
- All M12–M15 tests pass against Testcontainers Postgres locally and against the GitHub Actions service container.
- TOML export from the M15 tag imports cleanly into a fresh M16 deployment, with no errors and equivalent admin-UI state.
- `dotnet ef migrations add Foo` produces one Postgres migration; nothing references SQLite. `grep -ri sqlite` in the app project returns nothing.

## Milestone 17 — Site Admin Console v1

Goal: a SiteAdmin role for hosting operators, cross-organisation visibility, and a system-settings surface that includes a hybrid SMTP override.

### Domain

- New `is_site_admin` boolean column on `users`. Distinct from the per-organisation `Admin` role; carried as a separate claim on the auth cookie. Audited on change.
- New `system_settings` singleton table (`id` pinned to `1`). Columns: SMTP overrides (host, port, user, encrypted password, from, starttls), system banner text, default signup approval policy. Audited.
- The bootstrap admin (created from `BOOTSTRAP_ADMIN_*` env vars on a fresh DB) starts with `is_site_admin = true`. Subsequent SiteAdmins are promoted via the SiteAdmin users page.

### Encryption

- SMTP password column encrypted at rest via ASP.NET Core Data Protection. Key ring persisted to `/var/lib/aldevtoolbox/dp-keys` on a named compose volume `app-keys`. The same key ring needs to persist for cookie auth across container restarts anyway — no new posture.
- `AuditInterceptor` redacts the encrypted SMTP password column when snapshotting `system_settings` writes. Capturing the ciphertext history would leak structure; replace with a fixed sentinel.

### Hybrid SMTP

- `EmailService` resolves SMTP from `system_settings` first, falls back to env vars (`SMTP_HOST` etc.). Cached; invalidated on settings write.
- The env-var path stays fully supported — fresh deploys can fire signup-approval emails before any SiteAdmin has logged in to fill the form. Documented as the bootstrap path.

### Pages

- New `/site-admin/users` — search any user by email across organisations, list memberships, promote / demote SiteAdmin.
- New `/site-admin/audit` — cross-organisation audit search by entity type, organisation, actor, date range.
- New `/site-admin/settings` — system settings form (SMTP override, banner, default signup approval policy) with a "Send test email" button that mails the current SiteAdmin.
- Sidebar entry visible only to SiteAdmin. Routes return `404` (not `403`) for non-SiteAdmin users — no information leak.

### Query filter posture

- Existing per-organisation EF query filters stay as-is. The new SiteAdmin services opt out via explicit `IgnoreQueryFilters()` on the specific reads that need cross-org access. No global filter relaxation.
- Mutations on `system_settings` and `users.is_site_admin` are organisation-less; they call `RequireSiteAdmin()` rather than `RequireOrganizationId()`.

### Audit

- Promotions and demotions of SiteAdmin are audited. System settings changes audited via the standard interceptor (with the SMTP password redaction noted above).

### Tests

- M12 patterns: every new service method has happy-path and validation tests.
- Cross-organisation reads: a SiteAdmin search returns rows from multiple orgs; a regular admin search returns only their own.
- Hybrid SMTP precedence: env-only fallback works on a fresh DB; `system_settings` row overrides; settings change takes effect on the next send without restart.
- Audit redaction: `system_settings` write produces an audit row that does not contain the encrypted blob.
- Route protection: every `/site-admin/*` page returns `404` for org admins and anonymous users.

### Done when

- A SiteAdmin can search a user by email and see all organisations they belong to.
- Changing SMTP via the UI takes effect on the next email send, with no restart.
- The "Send test email" button delivers to the SiteAdmin's own address against MailHog (or equivalent) in dev compose.
- Promoting another user to SiteAdmin works, is audited, and grants them access to `/site-admin/*` on next sign-in.
- Org admins navigating to `/site-admin/users` get a `404`.
- The bootstrap env vars still produce a working SiteAdmin on a fresh DB.

## Milestone 18 — Backup tooling and CI migration testing

Goal: built-in scheduled and ad-hoc backups with in-place restore, plus migration testing in CI. Filesystem-only — no S3, no external storage.

### Backups

- New `BackupService` runs `pg_dump` (custom format `-Fc`) using `Npgsql` connection metadata; writes to `/var/lib/aldevtoolbox/backups` on a named compose volume `app-backups`.
- Scheduling: a hosted `IHostedService` runs daily at a configurable time (default 02:00 UTC) — configuration lives in `system_settings` (extends M17). SiteAdmin can also trigger an ad-hoc backup from the UI.
- Retention: keep last `N` (default 14). Oldest pruned automatically. SiteAdmin can pin a backup to exempt it from pruning. Pinned backups are never deleted.
- New page `/site-admin/backups` lists existing backups with size, age, pin / unpin, download (streamed from disk), restore. Audited.

### Restore

- In-place restore: SiteAdmin picks a backup, confirms via a destructive-action modal. The app enters maintenance mode (returns `503` with a static page for non-SiteAdmin requests), drops the schema, restores from the dump via `pg_restore`, and lifts maintenance mode.
- Maintenance mode is process-local — if more than one app container is ever run against the same DB, restore is a SiteAdmin-coordinated downtime. Document that.
- All restore actions audited (which backup, who, when, success or failure).

### CI migration testing

- New GitHub Actions job: spin up `postgres:18`, apply the previous release tag's migrations against a seeded fixture, then check out the current branch and apply its migrations. Assert no exceptions and that a known set of seed rows is still present.
- Runs on every PR. Catches accidental data-loss migrations (column drops without a backfill, type changes that fail on real data) before they merge.

### Tests

- Happy-path backup → restore → app boots cleanly. Use a Testcontainers Postgres for the test, not the dev compose DB.
- Retention pruning: 15 backups produced, oldest auto-deleted; pinned backup at position 1 survives.
- Maintenance-mode middleware returns 503 to non-SiteAdmin during restore; lifts cleanly on success and on failure.

### Done when

- A scheduled backup appears at the configured time without operator intervention.
- A SiteAdmin can take an ad-hoc backup, see it in the list, download it, and restore from it.
- Pinned backups survive automatic pruning; unpinned backups beyond the retention count are removed.
- The CI migration test job catches a deliberately-broken migration on a test PR.
- Restore from a backup taken on the previous tag works against the current tag (validates that backups are forward-compatible across single-version migrations).

## Milestone 19 — Invite-by-email and magic-link login

Goal: lower onboarding and login friction. Admin-issued invites alongside self-signup; magic-link login as an alternative to passwords.

### Invite flow

- Admin action on `/admin/users`: "Invite user" — takes an email address, a role (`User` or `Admin`), and an optional welcome message. Generates a single-use invite token (sha-256 stored, raw sent), valid for 7 days.
- Invitee receives an email with a link to `/accept-invite?token=…`. The page asks for display name and password, then activates the account directly into the inviting admin's organisation. No admin re-approval.
- Existing self-signup-then-approve flow stays. Invite is the second path, not a replacement.
- New `invites` table: `id`, `organization_id`, `email`, `role`, `token_hash`, `expires_at`, `accepted_at`, `invited_by_user_id`. `signup_requests` unchanged.

### Magic-link login

- New page `/login/magic` — user enters email, receives a single-use login link valid for 15 minutes. Clicking the link signs them in directly.
- Per-email and per-IP rate limits same as `/forgot-password` (10 / 15min email, 30 / 15min IP).
- Always show "If that email exists, you'll receive a link." regardless of outcome — don't reveal account existence.
- Tokens are sha-256 in DB. Reuse the `password_reset_tokens` table by adding a `purpose` column (`PasswordReset` or `MagicLogin`); cleaner than parallel tables.
- The standard `/login` page gets a "Sign in with a magic link instead" link. Magic-link is opt-in per attempt; the org admin doesn't decide it.

### Email templates

- Two new transactional bodies added to `EmailTemplates` in `Services/EmailService.cs`: `Invite(...)` and `MagicLink(...)`. Same shape as the existing `ForgotPassword`, `SignupPending`, and `SignupDecided` helpers — static methods returning `(Subject, HtmlBody)` rather than Razor partials. (M13 ships its templates this way too; the milestone doc previously hinted at Razor partials but the codebase never grew them. Worth revisiting only when an email genuinely needs rich layout.)
- All email-driven flows now route through the M17 hybrid SMTP resolver — operators can rotate credentials self-service when invite / magic-link volume changes.

### Audit

- Invite created, invite accepted, invite revoked. Magic-link request and magic-link consumption — same audit shape as password reset.

### Tests

- Invite token: single-use, 7-day expiry, redeems against the right organisation, rejects after acceptance.
- Magic link: 15-minute expiry, single-use, rate-limited per email and per IP.
- Token storage is hashed: a DB read does not yield usable tokens.
- Email enumeration: forgot-password and magic-link responses are indistinguishable for known and unknown emails.

### Done when

- An admin can invite a user by email, the invitee accepts via the link, and signs in immediately into the right organisation with the right role.
- A user can sign in via magic link without entering a password.
- Rate limits on magic-link match the existing forgot-password limits.
- Invite tokens and magic-link tokens are sha-256 in the DB; raw tokens never persisted.

## Milestone 20 — Audit diff viewer and bulk admin actions

Goal: make the audit log usable for support, and add bulk operations to admin lists. Two UX upgrades that share a milestone because they share design surface — the audit table is what gives bulk actions an undo story.

### Audit diff viewer

- New `Components/Shared/AuditDiffViewer.razor` — given two JSON snapshots from `audit_log_entries`, renders a side-by-side or inline diff. Use a small diff library (or hand-rolled if footprint matters) — no full Monaco bring-in.
- The existing `/admin/audit` page gains a "View change" action on each row: opens the viewer with `before` and `after` snapshots from the row.
- Cross-org audit (`/site-admin/audit` from M17) reuses the same viewer.
- Snapshot redactions (e.g. SMTP password from M17, file content hashes from M14) render as `<redacted>` placeholders, not as the redacted bytes.

### Bulk admin actions

- `/admin/templates`, `/admin/modules`, `/admin/catalogue` gain checkbox columns and a bulk action bar that appears when at least one row is selected. Actions: bulk soft-delete, bulk un-delete, bulk deprecate (templates only), bulk un-deprecate.
- `/admin/users` gains bulk disable, bulk enable, bulk role-change.
- Each bulk action goes through one service call that loops the entity ids inside a single transaction and writes one audit row per entity. Atomic per-entity, not atomic across the whole bulk — partial success surfaces a per-row result list.
- Confirmation modal lists the affected rows by name. No accidental "I selected the whole page" disasters.

### Tests

- Audit diff renders sensible HTML for a representative snapshot pair (added field, removed field, changed value, redacted field).
- Bulk soft-delete on three templates produces three audit rows with consistent `changed_by` and `timestamp` ordering.
- Bulk action authorisation: an org admin cannot bulk-act on entities from another organisation (URL tampering).
- Partial failure: if one entity in a bulk action fails validation (e.g. last admin in a bulk-disable), the others still process, the failure is surfaced inline, and the audit log reflects exactly the rows that succeeded.

### Done when

- A reviewer investigating an audit row can click "View change" and see what changed without leaving the page or parsing JSON.
- An admin selects three deprecated templates and un-deprecates them in one click, with a single confirmation and three audit rows.
- Bulk role-change on users respects the "last admin" guard from M13 — the bulk action surfaces the failure inline rather than letting an org become unadministerable.

## Milestone 21 — Hosting-readiness polish

Goal: cross-cutting cleanup that the individual M16–M20 milestones can't see in isolation. The bar is "I'd be happy running this for paying customers."

This is a polish milestone. Per-milestone polish (empty states, error messages, loading spinners on a new page) belongs in the milestone that introduces the page; this milestone is for cross-cutting things that only become visible once several pieces are in place. The categories below are pre-populated based on what we already know P4 will produce; specific items are added during M16–M20 as we notice them.

### Operator surface

- `/healthz` endpoint — returns `200` when the database is reachable and the Data Protection key ring is readable; `503` otherwise. What managed-hosting reverse proxies need for liveness probes.
- `/readyz` distinct from `/healthz` if startup work (migrations, seed) is non-trivial enough to warrant separate readiness gating.
- Structured logging review: every operationally-significant event (failed login, backup success or failure, migration applied, SMTP send failure) emitted at the right level with named placeholders. An operator at 02:00 should be able to grep for what happened.

### Postgres performance pass

- Run the slow-query log against a representative dev dataset. Add indexes where queries warrant — particular attention to the cross-org SiteAdmin reads (M17) and audit search (M17 / M20).
- SQLite hid some N+1s because it was fast on small data; Postgres at managed-hosting scale won't. No speculative indexes — only ones with a measured query.

### UX consistency across new pages

- Site Admin Console (M17), backup UI (M18), invite / magic-link (M19), audit diff (M20) — same empty / loading / error patterns, same primary / secondary button hierarchy, consistent copy tone. Read each page cold and fix what jars.
- Accessibility: focus-trap on the maintenance-mode modal (M18) and test-send dialog (M17). Aria labels on the audit diff (M20). Keyboard navigation through bulk action selection (M20).

### Compose hardening

- Resource limits, restart policies, healthcheck timings on `db` and `app`. The bits that get skipped when first making something work.
- Document the volume layout in one place: `pg-data` for Postgres data, `app-keys` for Data Protection keys, `app-backups` for backup files. Operators backing up the deployment shouldn't have to grep the compose file.

### Documentation

- Operator runbook covering: fresh deploy, scheduled and manual backup, in-place restore, SMTP rotation, promoting and demoting SiteAdmin, recovering from lost Data Protection keys.
- "Migrating from SQLite" appendix from M16 promoted into proper docs.
- README quickstart updated for the Postgres-default world.
- `CLAUDE.md` updated for the new conventions: Testcontainers, system settings posture, Site Admin role, the relaxed single-volume fence.

### Test gap-fill

- Anything that got skipped under time pressure during M16–M20. Specifically the in-place restore flow (M18) — destructive enough to warrant explicit happy-path and failure-path tests.

### Deliberately left out

- Dependency bumps with no security driver. This is a polish milestone, not an upgrade milestone.
- New features dressed as polish.
- Frontend rewrite or component-library swaps.
- Performance work without a concrete query to fix.

### Done when

- `/healthz` returns `200` when DB and DP keys are reachable; `503` otherwise. Verified against a deliberately-broken DP keyring.
- The slow-query log shows nothing over 100ms on the seeded dev dataset for the SiteAdmin pages and audit search.
- The operator runbook walks through every operational flow listed above against a fresh deployment.
- All P4 pages pass an axe-core spot check.
- README quickstart works on a fresh checkout with only `docker compose up`.

## Phase 5 candidates

Not committed; recording so they don't get pulled into Phase 4 by accident. Order is rough — we'll hash out the actual sequencing when we plan the phase.

### Identity (off the table for P4)

- **SSO / OIDC integration alongside email-password.** Per-org IdP config (Azure AD, Google, generic OIDC). Existing accounts coexist with federated ones. Deferred from Phase 4 — out of scope for managed hosting v1.
- **Two-factor authentication.** TOTP first; WebAuthn second. Per-user opt-in initially; admin enforcement per-org later.

### UX (off the table for P4)

- **Mobile / narrow-viewport layout.** The shell is desktop-only today. Deferred from Phase 4.
- **Per-org theming beyond logo** — accent colour, app name in the top bar, favicon. Already partly possible by extending M14's `organization_assets`.
- **Live preview on the New Extension page** mirroring what New Workspace got in M4.

### Generation

- **Workspace upgrade flow.** Given an existing generated workspace, generate a diff against the current template state and let the user apply selected updates. Big — needs its own design pass; provisional Phase 5 anchor.
- **Conditional folders / files.** "Include this folder only when module X is selected." Can be expressed today by splitting templates; a real conditional grammar would compress that.
- **Binary files in template folders.** v1 was text-only; some templates (icons, splash assets) want bytes.

### Out of scope, even for Phase 5

- Per-user accounts on a federated identity model with SCIM provisioning. If we get there, it's a separate product.
- A queue-based generation backend. Generation stays synchronous; if it gets slow, fix the slow part.
