# SaaS delivery — publishing builds to a BC environment (PROPOSAL)

> **Status: proposal / plan only. Nothing here is built.** This is the "delivery target"
> that `artifacts.md` and the `pipelines-domain-model` memory have flagged as *future*. It
> extends `Project` (the customer) and `Pipeline` (the build config) so a successful build can be
> published straight to a Business Central SaaS environment via the **automation API**, on a
> schedule that avoids the customer's working hours.

## Goal & scope

When a pipeline's build succeeds, upload and install the compiled `.app`s into a chosen BC SaaS
environment + company, automatically, inside a maintenance window — no manual "download the zip and
upload it in the admin center" step.

**In scope (v1):** per-tenant extension upload + install + deployment-status polling, for the apps a
pipeline already compiles, using S2S (client-credentials) auth.

**Out of scope (the automation API does these too, but we don't need them):** company *creation*,
RapidStart packages, user/permission/security-group management, feature management. Mention only so
the namespace isn't mistaken for our surface.

## End-to-end flow (the user's journey)

1. **One-time per customer (Project):** enter the BC connection — tenant id, the customer's Entra app
   (client id + secret + expiry), timezone — and **Test connection**, which fetches the environments
   (flagging a missing GDAP). Owner/Admin only.
2. **One-time per target (Release pipeline):** create a release pipeline — name it (e.g.
   `Contoso → Production`), choose the **source Build pipeline**, the **target environment**, version
   mode, sync mode, and a default publish time. (Can be created inline the first time you release to a
   new environment.)
3. **Build:** from the Build pipeline, trigger a build — it clones latest `HEAD`, so "from a new
   commit" just means running it again. Clone → compile → ingest, tracked live as today.
4. **Release:** once a build is **successful**, "Release" (on the release pipeline) or "Release to…"
   (on the successful build row) → the dialog resolves the **target** (= a release pipeline, carrying
   the environment + modes), defaults to the **latest successful build** (older ones selectable), and
   you pick the **date+time**. → enqueues a scheduled `ProjectDelivery`.
5. **Run:** at the scheduled time the background worker **claims** the delivery (after which it's no
   longer cancellable) and runs upload → install → poll; status flows
   `scheduled → claimed → uploading → installing → deployed | failed`.
6. **Track:** build progress on the build/pipeline page; delivery progress on the release pipeline's
   delivery history; both summarised on the pipelines landing. Cancel is available while `scheduled`.

The key point: **"target" is a release pipeline, not an ad-hoc environment pick** — so the *same*
successful build can be released through `Contoso → Production` and `Contoso → Sandbox` independently,
each with its own schedule and history (build-once-deploy-many).

## Fences this crosses — the explicit asks

Per `CLAUDE.md`, three things here need your sign-off before any code:

1. **A new per-tenant secret.** We'd store each customer's S2S **client secret**. This must follow
   the SMTP-password precedent exactly: encrypted with the Data Protection key ring (the `app-keys`
   ring; losing it means re-entering secrets), written only through a service, **never** returned to
   the UI or logged, org-scoped, and access-gated to the project owner / org Admin. Secrets are a
   named fence — this doc is the ask.
2. **Outbound HTTP to Microsoft.** New calls to `login.microsoftonline.com` (token) and
   `api.businesscentral.dynamics.com` (automation + admin APIs). This is the *same kind* of outbound
   dependency we already have (`BcArtifactService` → Microsoft CDN, `AlCompilerProvisioner` → NuGet),
   **not** a new piece of infra (no broker/cache/datastore). Framing it that way so it's clearly
   inside the existing fence, but calling it out.
3. **Scheduled background work.** Delivery must run *later* (the maintenance window), not in-request.
   Reuse the sanctioned in-process pattern — a `BackgroundService` scheduler + bounded `Channel`
   queue + worker, persisted rows for restart-resume — mirroring `ReleaseAutoImportScheduler` +
   `ReleaseImportQueue`/`ReleaseImportWorker` + `PersistedImportJobs`, and the newer
   `ProjectDiscoveryQueue`/`Worker`. **No external queue/broker.**

Migration discipline (future-dated timestamps) and tenant isolation (`IgnoreQueryFilters()` stays
untouched) apply as always.

## Data model

**Decision — separate Build and Release, rather than one pipeline that does both.** A pipeline name
like *"Release Contoso App on Production"* is really a release concern, and a partner usually wants to
**build once and deploy that same build to several environments** (test in Sandbox, then promote the
identical artifact to Production). Fusing build + delivery onto one entity can't express that without
rebuilding. So instead of a `kind` discriminator on `Pipeline` (which would mean many
nullable-by-kind columns, since build and release fields barely overlap), model them as two entities:

```
Project (customer)
├─ Build pipeline  (Pipeline — unchanged)      subset of extensions → Build(s) → artifacts
└─ Release pipeline (ReleasePipeline — new)     draws a Build's artifacts → an environment
   └─ Delivery (ProjectDelivery)                one scheduled run of a release pipeline
```

A Release pipeline references **one** Build pipeline as its artifact source and **one** environment
as its target; a Build pipeline can feed several Release pipelines. The existing `Pipeline` (shipped
in 7.1.0) keeps its meaning untouched — we add `ReleasePipeline` alongside it. (Alternative if you'd
rather not add an entity: a `kind` column on `Pipeline` — noted in open questions.)

### 1. Project = the customer connection (the tenant + credentials)

A customer has one Entra tenant and one set of S2S credentials shared across all their environments,
so these live on `Project` (new columns on `oe_projects`, snake_case):

| Column | Type | Why |
|---|---|---|
| `bc_tenant_id` | `uuid?` | The customer's Entra (AAD) tenant GUID. Used for the **OAuth token endpoint** and to scope the admin API — *not* the automation URL itself (that uses the environment name; see "Auth" below). |
| `bc_client_id` | `text?` | The S2S app registration's client id (one app **per project/customer** — see below). |
| `bc_client_secret_encrypted` | `text?` | Client secret, DP-key-ring encrypted. Write-only in the UI ("secret is set ✓"); never read back. |
| `bc_client_secret_expires_at` | `timestamptz?` | When the client secret expires. Entra secrets have a **max 2-year lifetime**; we surface a warning as it approaches so a delivery doesn't fail on an expired secret. Entered alongside the secret (Entra shows the expiry at creation). |
| `bc_credentials_updated_at` | `timestamptz?` | For the "last updated" caption + key-ring-loss diagnostics. |
| `bc_time_zone` | `text?` | IANA tz (e.g. `Europe/Copenhagen`) — the customer's local time, so scheduling defaults and "working hours" mean *their* hours. Defaults to the org default if unset. |
| `bc_connection_verified_at` | `timestamptz?` | Set by a "Test connection" action (token + list-environments round-trip). |

**Decision — one Entra app per project/customer.** Microsoft is deprecating cross-tenant Entra app
registrations, so each customer gets its own app: the per-project columns above are the right model
(not a shared system-level secret). Because each secret is short-lived, `bc_client_secret_expires_at`
is first-class — the Project connection card warns when a secret is within ~N weeks of expiry, and a
delivery scheduled past the expiry is flagged at scheduling time.

**Environments (fetched, persisted).** Because company id is per-environment and Release pipelines
reference an environment, persist the customer's environments as a child `ProjectEnvironment`
(`oe_project_environments`: `name`, `type` Production/Sandbox, `company_id`, `fetched_at`), populated
by Test connection / a Refresh — the same fetch-and-cache shape as the discovery cache. Release
pipelines then point at a `ProjectEnvironment` rather than re-typing a name. (Lean fallback: inline
`environment_name` + `company_id` on the release pipeline and skip this table.)

### 2. Build pipeline (`Pipeline`) — unchanged

The 7.1.0 entity stays exactly as is: a named subset of the project's extensions that compiles to
`Build`s (`ProjectBuild`) and artifacts. No new columns. It's now explicitly the *build* half of the
split; releases draw from its builds.

### 3. Release pipeline (`ReleasePipeline`) — new

The reusable "where + how" of a deploy: a named, listable config (`oe_release_pipelines`) that draws
from one Build pipeline and targets one environment. This is the *"Release Contoso App on Production"*
the naming suggested.

| Column | Type | Why |
|---|---|---|
| `id` / `organization_id` / `project_id` / `created_by_user_id` / `deleted_at` | | Standard, org-scoped, soft-deletable, owner-managed (same as `Pipeline`). |
| `name` | `text` | e.g. `Contoso App → Production`. |
| `build_pipeline_id` | FK → `oe_pipelines` | The artifact source — releases publish *this* build pipeline's builds. |
| `project_environment_id` | FK → `oe_project_environments` | The target environment (carries `company_id` + type). |
| `version_mode` | `text` | API `extensionUpload.schedule`, all three user-selectable: `Current version` (default) / `Next minor version` / `Next major version`. **Named `version_mode`** — the API's "schedule" is a version target, not a time. |
| `schema_sync_mode` | `text` | API `schemaSyncMode`: `Add` (default, safe) or `Force Sync` (can drop columns — gate behind a confirm). |
| `default_publish_time` | `time?` | Prefills the date/time picker when scheduling a delivery (e.g. 02:00), in `bc_time_zone`. **Not** a recurring window — the real schedule is a concrete date+time per delivery (below). |

### 4. Delivery = one run of a release pipeline (the analogue of `ProjectBuild`)

New entity `ProjectDelivery` (`oe_project_deliveries`), created when the user schedules a release of a
specific build. Mirrors how `ProjectBuild` records a build run:

- FKs: `release_pipeline_id`, `project_build_id` (the chosen build's `.app` blobs — already persisted
  as `ProjectBuildArtifact`), `organization_id`, `triggered_by_user_id`.
- **Snapshot** at creation (so later edits to the release pipeline don't rewrite history):
  `environment_name`, `company_id`, `version_mode`, `schema_sync_mode`.
- Schedule: `scheduled_for` (the UTC instant the user picked), `claimed_at`, `started_at`, `finished_at`.
- **Status lifecycle + the cancel/run race:**
  `scheduled → claimed → uploading → installing → deployed | failed`, plus `scheduled → cancelled`.
  - While `scheduled`, the delivery is **cancellable**. Cancel is an atomic compare-and-set
    (`UPDATE ... SET status='cancelled' WHERE id=? AND status='scheduled'`) — it only succeeds if the
    worker hasn't taken the row yet.
  - The scheduler/worker **claims** the same way (`SET status='claimed', claimed_at=now() WHERE
    id=? AND status='scheduled'`). Whoever wins the compare-and-set decides the outcome: a claim that
    finds the row already `cancelled` does nothing; a cancel that finds it already `claimed` is
    refused with "already started". This is the "cancellable until a worker picks it up" guarantee,
    enforced in the DB rather than with a lock.
- Per-app rows (`oe_project_delivery_results`, like `ProjectBuildResult`): app name/id, the BC
  `extensionUpload` id, the `extensionDeploymentStatus` result, message.
- `failure_message`, and a log section for the raw API responses (secret-free).

## Authentication (client credentials / S2S)

- **Token:** `POST https://login.microsoftonline.com/{bc_tenant_id}/oauth2/v2.0/token`,
  `grant_type=client_credentials`, `scope=https://api.businesscentral.dynamics.com/.default`,
  client id + secret. Tokens are ~1 h — **cache in memory** keyed by project (a singleton, like the
  compiler gate), **never persisted**. Refresh on expiry/401.
- **Customer-side prerequisites (document for onboarding, we can't do it for them):** the Entra app
  must be registered in the customer's BC as an **application (S2S) user** with a permission set that
  allows extension management (e.g. `D365 EXTENSION MGT` / `D365 AUTOMATION`, or `SUPER`), and admin
  consent granted. Note from the docs: S2S can't use `getNewUsersFromOffice365` and can't hold SUPER
  in the user-sync sense — irrelevant to publishing, but the app-user + permission step is mandatory.
- **URL nuance to fix in our mental model:** the *automation* base is
  `https://api.businesscentral.dynamics.com/v2.0/{environment_name}/api/microsoft/automation/v2.0/`
  — it keys on **environment name**, not tenant id. The tenant id is what the *token* is for. So
  "tenant id builds the URL" → really "tenant id gets the token; environment name builds the URL."

## Environment & company discovery

Two **different** API surfaces — worth flagging because authorization differs:

- **Environments** come from the **Admin Center API**:
  `GET https://api.businesscentral.dynamics.com/admin/v2.x/applications/businesscentral/environments`
  (tenant scoped by the token). This is the **primary** path — the maintainer always sets up the
  **GDAP** relationship that authorizes it. **If GDAP is missing/insufficient**, the call fails (401/
  403); Test connection must detect that and surface a clear "GDAP doesn't appear to be set up for
  this customer — grant it, then retry" message rather than a raw error. Manual environment-name
  entry stays as a fallback, but fetching is the expected flow.
- **Companies** come from the **automation API** for the chosen environment:
  `GET {automationBase}/companies` → pick one → store `company_id`.

UI flow: enter credentials → Test connection (token + list environments, flagging missing GDAP) →
pick environment → fetch companies → pick company.

## Publish flow (maps 1:1 to the automation docs)

For each app in the build, in **dependency order** (reuse `ProjectBuildService.TopologicalOrder`):

1. *(optional)* `GET extensions` — see the currently installed version, for diff/skip logic.
2. `POST companies({companyId})/extensionUpload` with `{ "schedule": version_mode, "schemaSyncMode": schema_sync_mode }`.
3. `PATCH extensionUpload({id})/extensionContent` — the `.app` bytes, `application/octet-stream`, `If-Match: *`.
4. `POST extensionUpload({id})/Microsoft.NAV.upload`.
5. Poll `GET extensionDeploymentStatus` until the app reports completed/failed.

Details to settle during build: whether apps upload one-at-a-time (docs show single file per upload)
vs a dependency bundle; how `version_mode` interacts with the app's `app.json` version (e.g. "Current
version" hot-swap vs "Next minor"); idempotency when the same version is already installed; and
partial-failure semantics (one app installs, a dependent fails) — same shape as the build report.

## Services & seams

- **`IBcAutomationClient` / `IBcAdminClient`** — HTTP seams (interfaces) over the two API surfaces, so
  the orchestration is unit-testable without hitting Microsoft. This is the *same* sanctioned reason
  we introduced `IProcessRunner` for git/alc (a real test seam, two-impl-or-test rule satisfied).
- **`BcTokenService`** — singleton, in-memory token cache + client-credentials flow.
- **`ProjectConnectionService`** — writes/reads the connection config; owns the secret (encrypt on
  write, never return it), the Test-connection action, environment/company fetch. Access-gated.
- **`ReleasePipelineService`** — CRUD over `ReleasePipeline` (name, source build pipeline, target
  environment, version/sync modes, default time). Access-gated like `PipelineService`.
- **`DeliveryService`** — creates a `ProjectDelivery` when the user schedules a release of a chosen
  build (no auto-on-build in v1); converts the picked local date+time to a UTC `scheduled_for` using
  the project's timezone; owns the atomic cancel/claim transitions.
- **`DeliveryScheduler`** (`BackgroundService`) — polls for due `scheduled` rows, enqueues to
  **`DeliveryQueue`** (bounded `Channel`); **`DeliveryWorker`** drains and runs the publish under the
  triggering user's captured `AmbientOrganizationScope` identity. Persisted rows = restart-resume.

## UI surfaces

- **Project detail:** a "Business Central connection" section — tenant id, client id, secret
  (write-only) + secret-expiry, Test connection (flags missing GDAP), timezone, and the fetched
  environment list with Refresh. The single sensitive screen; owner/Admin only.
- **Release pipelines:** a listable surface alongside Build pipelines (own icon — e.g. `rocket` for
  build stays, a `send`/`upload-cloud` for release), with a create/edit dialog: name, source build
  pipeline, target environment (picker), version mode, schema sync mode (Force Sync behind a confirm),
  default publish time.
- **Schedule a release:** lives on the **Release pipeline** — a "Release" action that's enabled once
  the source Build pipeline has a *successful* build. It defaults to the **latest successful build**
  (with the option to pick an older one), then "pick the date+time" (prefilled from
  `default_publish_time`) → creates a scheduled `ProjectDelivery`. Failed/in-progress builds aren't
  releasable. Production targets get an extra confirm; scheduling past secret expiry warns but allows.
  A "Release to…" shortcut on a successful build row in the Build pipeline's history can open this same
  dialog as a convenience, but the canonical action is on the release pipeline.
- **Delivery history:** per release pipeline, the `ProjectDelivery` runs with status,
  scheduled/started times, per-app results, and **Cancel** (only while `scheduled`) / **Reschedule**.

## Security & tenant isolation

- Every new row carries `organization_id`; reads ride the EF query filter. No new
  `IgnoreQueryFilters()` — deliveries run under the triggering user's captured identity in the worker
  (the blessed deferred-work analogue), exactly like the build worker.
- The secret never leaves the server: encrypted column, write-only field, redacted from logs and from
  the delivery's stored API-response log.
- "Test connection" and "Publish" are owner/Admin-gated via `ProjectAccess`.
- Production deploys want a deliberate confirm; consider an audit-log entry per delivery.

## MCP parity

A future `publish_build` / `list_deliveries` MCP tool would let agents drive delivery the way humans
do. Not v1, but design the `DeliveryService` API so a tool can sit on it without reaching past it.

## Suggested phasing

1. **Connection + auth + Test** (Project columns incl. secret-expiry, secret handling,
   `BcTokenService`, `IBcAdminClient` list-environments with GDAP-missing detection, Test-connection,
   expiry warning). No publishing yet — just prove the creds.
2. **Release pipelines + manual publish** (`ProjectEnvironment` fetch, `ReleasePipeline` CRUD,
   "Release this build now" running the full upload→install→poll in-worker, no scheduling yet).
3. **Scheduling** (pick a concrete date+time per delivery, `DeliveryScheduler`/`Queue`/`Worker`, the
   atomic claim/cancel transition, cancellable-until-claimed, restart-resume, delivery history UI).
4. **Polish** (partial-failure reporting, Production confirms, secret-expiry-vs-scheduled-time
   guard, audit-log entries, MCP tool). *Auto-deliver on build success is explicitly **not** v1.*

## Decisions (resolved)

- **Build vs Release:** two distinct concepts, as a **separate `ReleasePipeline` entity** (not a
  `kind` column on `Pipeline`). `Pipeline` (build) stays as shipped; `ReleasePipeline` draws from one
  build pipeline and targets one environment. Build-once-deploy-many falls out for free (one build
  pipeline → several release pipelines).
- **One environment per release pipeline** (1:1). Naming reads *"Release Contoso App on Production."*
- **Environments are persisted** as a `ProjectEnvironment` child (fetched + refreshable), so the
  picker and release pipelines share one `company_id` — not inlined per release pipeline.
- **Release trigger:** the "Release" action is on the **Release pipeline**, enabled once its source
  Build pipeline has a successful build; defaults to the latest successful build, with the option to
  pick an older one. (A build-history "Release to…" shortcut opens the same dialog.)
- **Credential model:** one Entra app **per project/customer** (cross-tenant app registrations are
  being deprecated). Track the secret's expiry (max 2-year lifetime) and warn before it lapses.
- **Environment listing:** fetch via the Admin Center API as the primary path; GDAP is always set up,
  so Test connection should **flag a missing/insufficient GDAP** clearly. Manual entry is a fallback.
- **Version mode:** all three offered; default **`Current version`**.
- **Trigger model:** no auto-publish in v1. The user explicitly schedules a delivery for a concrete
  date+time; it then runs automatically at that time, and is **cancellable until a worker claims it**.
- **Expired-secret behaviour:** warn-but-allow at scheduling; the run hard-fails with a clear "secret
  expired — rotate it" message if it's actually lapsed when the worker fires.

## Open questions

The shape is settled (see Decisions). What's left is **implementation detail to settle when building**,
not architecture:

- **Upload granularity:** one `extensionUpload` per app (docs show single-file uploads) vs a
  dependency bundle — and confirm dependency-order publishing with polling between apps.
- **Version interplay:** how `version_mode` interacts with the app's `app.json` version (e.g. "Current
  version" hot-swap vs "Next minor"), and idempotency when the same version is already installed.
- **Partial-failure semantics:** one app installs, a dependent fails — surface like the build report.
- **Secret-expiry warning lead time** (the "~N weeks" before expiry to start nagging).
