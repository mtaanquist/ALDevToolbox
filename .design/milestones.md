# Milestones — Phase 3

Phases 1 and 2 are done; their milestone log lives in `completed-milestones.md`. This file picks up where that left off.

Phase 3 is a deliberate departure from three constraints the earlier design pinned down. We're crossing those fences on purpose, with eyes open:

- **Multi-tenancy.** Phase 1 declared single-tenancy. Phase 3 introduces organisations as a real boundary — every editable row gains an `organization_id`, queries scope to it, audit scopes to it. The "Out of scope for v1" note about multi-tenancy in `completed-milestones.md` is superseded.
- **Accounts and roles.** Phase 1's `auth-and-audit.md` describes a single shared password. Phase 3 replaces it with email/password accounts, two roles (`User`, `Admin`), and an admin-approved signup flow. That document needs to be rewritten as part of M12.
- **Logo and always-included files as embedded resources.** The "ruleset, `.gitignore`, and logo ship as code" constraint in `CLAUDE.md` is intentionally relaxed for the logo and per-org always-included files (`workspace.json` skeleton, etc.). The ruleset and `.gitignore` template stay as embedded resources for now — they're per-deployment policy, not per-organisation.

When this phase finishes, those design documents (`auth-and-audit.md`, `architecture.md`, `domain-model.md`, `CLAUDE.md`) should reflect the new reality. Update them in the same PRs as the code change, not after.

## Milestone 12 — Organisations and accounts

Goal: replace the shared-password gate with real accounts, scoped to organisations. Two roles. Admin-approved signups. Forgot-password by email.

### Domain

- New tables: `organizations` (id, name, slug, created_at), `users` (id, organization_id, email, password_hash, display_name, role, status, created_at, last_login_at), `user_invitations` or `signup_requests` (id, organization_id, email, requested_at, decided_at, decided_by_user_id, decision), `password_reset_tokens` (user_id, token_hash, expires_at, consumed_at).
- `users.role` is `User` or `Admin`. `users.status` is `Pending`, `Active`, or `Disabled`. The shared-password code path is removed entirely — no compatibility shim.
- Add `organization_id` (NOT NULL, FK) to every editable entity that is currently global: `runtime_templates`, `template_folders`, `template_files`, `modules`, `module_dependencies`, `well_known_dependencies`, `audit_log_entries`, and anything M14 adds for application versions / per-template default modules. Indexes on `(organization_id, …)` everywhere we currently index on the second column alone.
- Update `domain-model.md` to describe the new tables, the FKs, and the per-org uniqueness constraints (template `key` is unique per org, not globally).

### Migration

- This is a structural migration — the existing single-tenant data has no organisation. The migration creates a default organisation ("Default" / slug `default`), backfills every existing row's `organization_id` to it, and stamps the existing seed data as belonging to that org. Document the steps in the migration's XML doc comment so a maintainer reading it later can reconstruct what happened.
- The `ADMIN_PASSWORD` / `ADMIN_PASSWORD_FILE` env variables stop being read. On first run after this migration, if there are zero users, bootstrap a single admin account from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env variables, attached to the default organisation, then never read those variables again. Log a warning if the bootstrap variables are still set on a later boot.

### Auth

- ASP.NET Core Identity is overkill for our shape; roll a thin `AccountService` over the existing EF context. Argon2id password hashing (via the `Konscious.Security.Cryptography.Argon2` package or `BCrypt.Net-Next` if simpler — pick one and document why). No external IdP.
- Cookie auth stays, but the cookie now carries `user_id` and `organization_id` claims (and the role claim derived from `users.role`). `IHttpContextAccessor` consumers read these instead of the bare display name.
- Routes: `/login`, `/signup`, `/forgot-password`, `/reset-password?token=…`. The signup form takes email + display name + password + an optional organisation slug; if the slug matches an existing org, the signup attaches to it (pending approval); if blank or unknown, the signup creates a new pending organisation.
- `[Authorize]` everywhere admin pages already have it; new `[Authorize(Roles = "Admin")]` on the admin-only edit/delete actions. End-user pages (the generators) stop being anonymous — every signed-in `User` or `Admin` can use them. Anonymous users are redirected to `/login`.

### Approvals

- New admin page `/admin/users` lists pending and active users for the current organisation, with approve / reject / disable / role-change actions. Audit every action.
- Cross-organisation: an admin only ever sees their own organisation's users. There is no superuser. If we need cross-org administration later, that's a separate milestone.

### Email (SMTP)

- New `EmailService` reading SMTP config from env: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD_FILE`, `SMTP_FROM`, `SMTP_USE_STARTTLS`. Use `MailKit` — `System.Net.Mail.SmtpClient` is officially obsolete.
- Three transactional templates, all rendered server-side from Razor partials under `Components/Email/`:
  - **Forgot password** — sent to the user with a single-use reset link valid for 1 hour.
  - **Signup pending** — sent to every active admin in the target organisation when a new signup arrives.
  - **Signup decided** — sent to the requester after an admin approves or rejects.
- `EmailService.SendAsync` is `async` end-to-end and takes a `CancellationToken`. Failures log a warning but do not block the underlying action (a failed approval email shouldn't roll back the approval).
- If SMTP is not configured (`SMTP_HOST` blank), the service throws on send and the calling code surfaces "Email is not configured; ask an admin." rather than silently swallowing. Don't add a "skip if unconfigured" mode — fail loudly so misconfiguration is visible.

### UI

- Login / signup / forgot-password pages styled to match the existing shell. Same field-keyed validation pattern as the generator forms (server is the source of truth, HTML attributes mirror the rules).
- Top bar: "Signed in as Bob (Acme) — Admin" with a sign-out menu. Replace the existing single-shared-password indicator.
- Admin sidebar gains "Users" alongside Templates / Modules / Catalogue / Audit.

### Audit

- `audit_log_entries.changed_by` already takes a string; it now stores `"display_name <email>"` of the acting user. Add `changed_by_user_id` and `organization_id` columns alongside it for queryability.
- Every approval / rejection / role change / disable writes an audit row.

### Done when

- A fresh deployment with `BOOTSTRAP_ADMIN_*` env vars boots, lets the admin sign in, and shows them an empty "Users" page in their default organisation.
- A second user can sign up, the admin gets an email, approves, and the new user can sign in and use the generator (but not the admin pages).
- Forgot-password produces a working reset link and a clear failure message if SMTP is unconfigured.
- Two organisations cannot see each other's templates, modules, catalogue, or audit log. Enforced with EF query filters scoped to `organization_id`, not just by URL.

## Milestone 13 — Organisation configuration

Goal: the things an organisation needs to customise — publisher, ID ranges, default briefs, logo, always-included file contents — live in the database and are editable through an admin configuration page. The previously-embedded versions are removed.

### Domain

- New `organization_settings` table (one row per organisation): `default_publisher`, `default_id_range_from`, `default_id_range_to`, `default_brief`, `default_core_description`. Validation matches the existing `GenerationService` rules (publisher non-empty, ID range numeric and `from <= to`).
- New `organization_assets` table: `id`, `organization_id`, `kind` (`Logo`), `content_type`, `content` (BLOB), `updated_at`. One logo per organisation; uploading replaces.
- New `organization_files` table: `id`, `organization_id`, `path`, `content`, `mustache_enabled`, `ordering`, `updated_at`. These are the always-included text files that get written into every generated workspace (and standalone extension where they apply) — the v1 `workspace.json` skeleton is the obvious first row, but admins can add more (e.g. a `.editorconfig`, a per-org `README.md` template). Mustache substitution runs when `mustache_enabled` is true, using the same context as the existing per-template files.
- Generation reads `organization_files` and `organization_assets` from the acting user's organisation; the workspace-level files are written before per-extension folders, so per-template files can override if paths collide (document the precedence in `generation-engine.md`).

### Migration

- Drop the embedded `Resources/Logo.svg` and the embedded `workspace.json` template (and any other always-included file currently shipping as a resource). The seed flow on first run for a *new* organisation populates `organization_files` and `organization_assets` from the contents that used to live under `Resources/` — that's the one and only time those bytes appear in the codebase, and they're moved into `Templates.seed/organization-defaults/` for the seed step.
- The migration backfills the default organisation's settings/files/logo from the previously-embedded values so existing deployments don't lose anything.
- The ruleset and `.gitignore` template stay as embedded resources for now. They're closer to per-deployment policy than per-org config and don't have a clear customisation story yet. Note that explicitly in the migration so a future maintainer doesn't assume it was an oversight.

### Services

- New `OrganizationConfigService` for reads and writes. Generation gets the org's settings/files/logo via this service; no more direct `Resources/` reads from `GenerationService`.
- Caching: a per-organisation in-memory cache keyed on `organization_id` with invalidation on save. Don't reach for `IMemoryCache` if a `ConcurrentDictionary` will do — the volume is tiny.
- Validate uploaded logos: max 256 KB, content type `image/svg+xml` or `image/png`, basic SVG sanitisation (strip `<script>` and `on*` attributes). Reject anything else with a clear field-keyed error.

### UI

- New `/admin/configuration` page with three sections:
  1. **Defaults** — publisher, ID range, brief, core description. Inline validation, save button per section so a typo in one doesn't lose progress on another.
  2. **Logo** — current logo preview, upload control, "revert to default" button (re-runs the seed for this row only).
  3. **Always-included files** — list with path + content editor per row, reorder, delete, add. Reuse `Components/Shared/TemplateFileEditor.razor` from M8.5 — that's exactly the surface this needs.
- The end-user generator forms read defaults from the configuration service, so a fresh New Workspace form arrives pre-filled with the org's publisher and ID range. Users can still edit on the form; the config provides defaults, not locks.

### Audit

- All three sections audit through the existing interceptor. Logo audit snapshots the content hash and content type — never the bytes — to keep the log compact (same pattern as `template_files`).

### Done when

- An admin can change the org's default publisher, save, and see the new value pre-filled on a fresh New Workspace form.
- Uploading a logo replaces it on the next generation; the previous logo bytes are gone (no soft-delete on assets, just the audit hash).
- Adding a new always-included file (say `.editorconfig`) makes it appear in every subsequently-generated workspace under the workspace root, with mustache substitution if enabled.
- Two organisations can have different logos, different defaults, and different always-included files without leaking into each other.
- The codebase no longer reads `Resources/Logo.svg` or any always-included file from disk at generation time. `grep`-verifiable.

## Milestone 14 — Polish pass

Goal: a deliberate sweep across the whole codebase — not feature work, not new milestones disguised as polish. The bar is "a new contributor reading this in six months understands what's going on."

This milestone is read-only on behaviour: nothing here should change what the app does. If a polish change risks behavioural drift (e.g. consolidating two near-identical helpers), call that out in the PR and verify by hand against the relevant flow.

### Code-level pass

- DRY review: walk every `Services/` class and every `Components/` page, list near-duplicate methods (validation helpers, file-walking helpers, mustache contexts). Factor out the second occurrence; leave first occurrences alone if they have no twin. The same rule from `CLAUDE.md` applies — three similar lines is fine; two callers is rarely worth a shared helper.
- Idiomatic C#: confirm nullable annotations are tight (no `string?` where `string` is the truth), `async` is end-to-end (no `.Result` / `.Wait()`), `AsNoTracking()` is on every read-only EF query, structured logging uses named placeholders. Run with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` enabled to surface anything we've been ignoring.
- Comments review: read every `///` and `//` comment in the codebase. If it explains *what* the code does and the identifier already says it, delete it. If it explains *why* and the reason has aged out, update or delete. If a tricky helper has no comment and a stranger would stall on it, add one. The CLAUDE.md guidance is the bar.
- Magic numbers and strings: anything that appears in two places and means the same thing (regex patterns, default page sizes, file extensions) becomes a named constant in the file that owns the concept.
- Dead code: any `internal` or `private` member with no callers gets deleted. Any `public` API with no callers either gets called or gets deleted; keeping it "in case" violates the YAGNI rule we've been holding.

### Cross-cutting consistency

- Every list page renders loading / empty / populated states. M11 covered the v1 set; this pass covers anything M12–M13 added (`/admin/users`, `/admin/configuration` sub-lists).
- Every form posts a field-keyed `Dictionary<string,string>` on validation failure, never a single concatenated string.
- Every `Task`-returning method in `Services/` accepts and threads a `CancellationToken`. The earlier code mostly does this; verify and fix the holdouts.
- Every audited mutation actually goes through the interceptor (no direct SQL, no `ExecuteUpdate` bypass). Spot-check the new M12 / M13 services.

### Documentation

- Update `architecture.md`, `auth-and-audit.md`, `domain-model.md`, `templates-and-seeding.md`, `generation-engine.md`, `ui-design.md`, and `CLAUDE.md` to match the post-M13 reality. Anything stale (e.g. "single shared password", "logo ships as code") gets rewritten in place — don't leave a "this changed in phase 3" footnote.
- Repo `README.md`: how to run, how to bootstrap an admin, how to configure SMTP, how to back up the SQLite file. Drop anything that's no longer true.
- Migration history: a short note in `.design/` summarising what each migration did, so an admin running `EF Core` updates against a long-lived database can reason about what's happening.

### Done when

- `dotnet build` is green with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<Nullable>enable</Nullable>`.
- A reviewer can read any service class top-to-bottom without needing to consult the design docs to follow what it does — the code reads as intended behaviour and the docs answer "why this shape".
- The design docs and code agree on every architectural fence; no document still claims a constraint the code has moved past.
- Manual smoke test of every end-user flow (New Workspace, New Extension, Templates Browser) and every admin flow (Templates, Modules, Catalogue, Audit, Users, Configuration, Export) passes against a fresh database and against an upgraded one.

## Phase 4 candidates

Not committed; recording so they don't get pulled into Phase 3 by accident.

- Cross-organisation superuser role for hosted deployments.
- SSO / OIDC integration alongside email-password.
- A real diff viewer for audit snapshots.
- Per-organisation Docker volumes / Postgres backend for hosted deployments.
- Mobile layout.
