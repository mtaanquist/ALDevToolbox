# Milestones — Phase 3

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

## Phase 4 candidates

Not committed; recording so they don't get pulled into Phase 3 by accident. Order is rough — we'll hash out the actual sequencing when we plan the phase.

### Operations and hosting

- **Cross-organisation superuser role.** A `SiteAdmin` flag on `users` (or a separate `site_admins` table) that bypasses the `organization_id` filter for support / debugging. Audit aggressively. Pre-requisite for managed hosting.
- **Postgres backend option.** Keep SQLite as the single-container default; add Postgres as a configurable provider for hosted multi-org deployments where the SQLite file becomes a backup pain. Mostly an EF provider swap plus a connection-string story.
- **Backup and restore tooling.** Built-in scheduled SQLite snapshots to a configured path (or S3-compatible bucket). Right now the backup story is "copy the file"; that doesn't scale once orgs care about their data.
- **Automatic migration testing in CI.** Spin up the previous release's DB, apply the new migration, assert no data loss. Currently manual.

### Identity

- **SSO / OIDC integration alongside email-password.** Per-org IdP config (Azure AD, Google, generic OIDC). Existing accounts coexist with federated ones.
- **Two-factor authentication.** TOTP first; WebAuthn second. Per-user opt-in initially; admin enforcement per-org later.
- **Magic-link login** as an alternative to passwords. Lower friction for occasional users; same SMTP plumbing as password reset.
- **Invite-by-email flow** (admin → user) alongside the existing self-signup-then-approve flow.

### UX

- **Mobile / narrow-viewport layout.** The shell is desktop-only today.
- **Per-org theming beyond logo** — accent colour, app name in the top bar, favicon. Already partly possible if we extend M14's `organization_assets`.
- **In-app diff viewer for audit snapshots.** Already noted in `completed-milestones.md` "out of scope for v1"; M14's complete-config audit makes this more useful.
- **Live preview on the New Extension page** mirroring what New Workspace got in M4.
- **Bulk actions in admin lists** — soft-delete or change-role across multiple rows.

### Generation

- **Binary files in template folders.** v1 was text-only; some templates (icons, splash assets) want bytes.
- **Conditional folders / files.** "Include this folder only when module X is selected." Can be expressed today by splitting templates; a real conditional grammar would compress that.
- **Workspace upgrade flow.** Given an existing generated workspace, generate a diff against the current template state and let the user apply selected updates. Big.

### Out of scope, even for Phase 4

- Per-user accounts on a federated identity model with SCIM provisioning. If we get there, it's a separate product.
- A queue-based generation backend. Generation stays synchronous; if it gets slow, fix the slow part.
