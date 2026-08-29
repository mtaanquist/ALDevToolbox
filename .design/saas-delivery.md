# SaaS delivery — publishing builds to a BC environment

> **Status: shipped.** The delivery pipeline is built and running. Implementation lives in
> `Services/ObjectExplorer/DeliveryService.cs`, `DeliveryScheduler.cs`, `DeliveryWorker.cs`,
> `DeliveryQueue.cs`, and `ReleasePipelineService.cs`, with the BC automation-API client under
> `Services/ObjectExplorer/Bc/`. The entities are `ProjectDelivery`, `ProjectDeliveryResult`, and
> `ReleasePipeline`; the maintenance-window math is the `UpdateWindow` value object; MCP tools
> expose the surface to agents. It extends `Project` (the customer) and `Pipeline` (the build
> config) so a successful build can be published straight to a Business Central SaaS environment via
> the **automation API**, on a schedule that avoids the customer's working hours.
>
> The sections below are the original design proposal, kept as the record of intent; where a detail
> drifted from what shipped, the code is the source of truth.

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

Each `ProjectEnvironment` also carries a recurring **update window** (see below), so the time-of-day
defaulting is per-environment, not per-release-pipeline.

**As built — the whole environment record is kept, not just name and type.** The environments call
already returns everything the admin center knows about an environment, so discarding it and then
needing a second call later was pure loss. Every field the API reports is persisted on the row,
nullable, and rewritten on each Refresh: `friendly_name`, `application_family`, `status`,
`status_fetched_at`, `country_code`, `aad_tenant_id`, `web_client_login_url`, `location_name`,
`geo_name`, `ring_name`, `app_source_apps_update_cadence`, `version`, `grace_period_start_date`,
`enforced_update_period_start_date`, `soft_deleted_on`, `hard_delete_pending_on`, `delete_reason`.

Three rules that come with them:

- **Verbatim, never normalised.** Microsoft's casing for enum-ish values differs between endpoints
  (`productFamily: "BusinessCentral"` beside `creatorPrincipalType: "app"`), so values are stored
  exactly as returned and every comparison is case-insensitive. `application_family` in particular is
  the family the API reported — it is *not* assumed, because it addresses the environment in later
  admin-center calls.
- **Two fields are deliberately not persisted.** `appInsightsKey` is secret-adjacent (storing it
  would pull the Data Protection key ring into a cache table — a fence conversation, not a detail),
  and `webServiceUrl` is derivable and unused.
- **`soft_deleted_on` and `missing_since` are different signals.** A soft-deleted environment still
  comes back from the API; a hard-deleted one vanishes from it. The first is the customer's state,
  the second is ours.

The refresh upsert still touches only fetched fields, so the user's own settings on the same row (the
picked company, the update window) survive a Refresh unchanged.

#### The status gate (deliveries)

`status` is the field that earns its keep: publishing to an environment mid-upgrade fails in ways
that read as our bug. Classification — `Active` publishes; `Upgrading` / `Preparing` / `NotReady` /
`Recovering` refuse with retryable wording; `Removing` / `SoftDeleting` / `SoftDeleted` refuse with
terminal wording; anything ending `Failed` refuses as a failed state in Business Central. An absent
or unrecognised status does **not** block — rows fetched before the field was captured have none, and
a status Microsoft adds later shouldn't silently stop every release.

It is checked twice, and the second check is the one that matters:

1. **At scheduling**, against the cached status, as a field-keyed validation error so it lands next to
   the environment on the form.
2. **At claim time**, by re-reading the single environment (`GET .../environments/{name}`) after the
   claim and before the first upload. A delivery scheduled at 09:00 for 22:00 was fine when it was
   scheduled; an update that landed at 20:00 is invisible to check 1. The fresh status is written back
   to the row, so the project page doesn't keep showing the status the delivery just contradicted. A
   404 there means the environment is gone; a transport failure is *not* treated as a refusal, since
   an unreachable API is no evidence about the environment's health.

Note the by-name response omits `geo_name`, so a live re-read leaves the cached value alone rather
than erasing it.

#### Update window (per environment)

Every BC SaaS environment already *has* an update window in the admin center — a recurring daily
time range during which Microsoft applies platform/app updates — so BC admins reach for exactly this
model. We mirror it: two nullable columns on `ProjectEnvironment`, interpreted in the project's
`bc_time_zone`:

| Column | Type | Why |
|---|---|---|
| `update_window_start` | `time?` | Start of the daily window (e.g. `22:00`), in `bc_time_zone`. |
| `update_window_end` | `time?` | End of the daily window (e.g. `06:00`); may wrap past midnight. |

Both null ⇒ **no window** (deliver any time) — the normal Sandbox case. Set ⇒ a recurring default a
Production environment is happy to receive updates in. v1 is a single daily range, matching BC's own
admin-center field (no weekday mask — add one only if a real case needs it). The window is in
`bc_time_zone` for now; BC environments carry their *own* tz, which we could fetch from the admin API
later, but one project-level tz is the v1 simplification consistent with the rest of this doc.

**It's a default, not a lock.** This is the one place we deliberately differ from BC's own window
(which Microsoft enforces): ours only computes the **prefilled `scheduled_for`** when a user schedules
a delivery — "next time this environment's window opens." The user can override it to run now, or at
any other time; the consultant is the one in control, not the platform. Overriding the window (or
delivering to an environment that has one set, outside it) is **audited** — recorded on the
`ProjectDelivery` and surfaced in history — so the safe default protects you and the opt-out is a
deliberate, traceable act. Production targets, which already get an extra confirm, are the case this
most matters for.

This **supersedes `ReleasePipeline.default_publish_time`** as the source of the schedule prefill: the
window lives on the environment (where it's reused across every release pipeline targeting it and
matches the BC mental model), rather than being re-entered per release pipeline. Keep
`default_publish_time` only if a pipeline ever needs to differ from its environment's window;
otherwise drop it (see the amended row in §3).

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
| `deployment_schedule` | `text` | App Management `deploymentSchedule` — **when** BC installs the upload: `Immediate` (default) / `UpdateWindow` / `NextMinorUpdate` / `NextMajorUpdate`. **Renamed from `version_mode`** when publishing moved off the retired upload API: the old column held a *version target* (`Current version` / `Next minor version` / `Next major version`) and the new field genuinely means a time, so the values were migrated as well as the name. Only three are offered in the picker — see *Deployment schedules* below. |
| `schema_sync_mode` | `text` | App Management `syncMode`: `Add` (default, safe) or `ForceSync` (can drop columns — gate behind a confirm). Note the missing space: the retired API spelled it `Force Sync`, so stored values were migrated too. |
| `default_publish_time` | `time?` | **Superseded by the target environment's update window** (§1 → *Update window*) as the schedule prefill, and likely droppable. Keep only as a per-pipeline override when one release pipeline must default to a different time than its environment's window. The execution model is unchanged: the real schedule is always a concrete date+time per delivery (`ProjectDelivery.scheduled_for`, §4) — the window/`default_publish_time` only seed the picker. **As built (CRUD slice):** the column was *not* added — there is no scheduling in the CRUD slice to prefill, and the per-environment update window (phase 3) is the intended source. Add it back only if a per-pipeline override turns out to be needed. |

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
  install `operation_id`, the operation's result, message.
- `failure_message`, and a log section for the raw API responses (secret-free).

**As built:** `oe_project_deliveries` also carries a denormalised `project_id` (so the worker
resolves the BC credentials without a join) and a `diagnostics_log` text column (the secret-free
per-step run log). The per-app `app_id` is now **populated**: BC reads it out of the uploaded package
and returns it on the install operation, which is what lets the poll ask about one specific app
rather than matching on a name. `company_id` is nullable and no longer written — extensions install
per environment — and the automation API's `extension_upload_id` survives on historical rows only.

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
- **BC-side prerequisites are two separate registrations, and neither is visible from Entra.**
  This is the part that looks finished when it isn't: granting the API permissions in Entra only
  gets a *token*, and Business Central keeps its own allow-lists.
  1. **Admin Center API** — the app's client id must be on the **Authorized Microsoft Entra apps**
     page in the BC admin center (tenant-wide). Missing → **401** on the environments call.
  2. **Automation API** — the app must be added on the **Microsoft Entra applications** page
     *inside each environment*, `State = Enabled`, with permission sets (`D365 AUTOMATION` +
     `EXTEN. MGT. - ADMIN`; `SUPER` can't be assigned to an application). Missing → **401** on the
     companies call, even though Test connection just passed.
- **URL nuance (corrected).** The automation base is
  `https://api.businesscentral.dynamics.com/v2.0/{tenant_id}/{environment_name}/api/microsoft/automation/v2.0/`
  — Microsoft's *direct tenant* endpoint form. An earlier revision of this doc said the base keys on
  environment name and **not** tenant id, citing the *common endpoint* form that omits the tenant
  segment; that form resolves the tenant from the token, which fails for an S2S application token
  (bare 401, no body) and can't express the partner case at all. Both segments are required: the
  tenant id gets the token *and* addresses the URL.

## Environment & company discovery

Two **different** API surfaces — worth flagging because authorization differs:

- **Environments** come from the **Admin Center API**:
  `GET https://api.businesscentral.dynamics.com/admin/{version}/applications/businesscentral/environments`
  (tenant scoped by the token). This is the **primary** path. Manual environment-name entry stays
  as a fallback, but fetching is the expected flow.
  **On the version:** this line used to read `admin/v2.x`, and that placeholder is what caused the
  drift — the implementer substituted whatever was current that week (`v2.21`), and it then sat
  eight versions behind for months, below the `v2.24` that `authorizedAadApps/manageableTenants`
  needs. The version now lives in exactly one place, `BcConstants.AdminApiVersion`, and
  `.github/workflows/bc-api-version.yml` probes Microsoft monthly and opens an issue when a newer
  one ships. **Don't write a concrete version into this doc** — it will rot the same way; name the
  constant instead. Old versions keep serving for years (v2.15 still answered in Aug 2026), so
  falling behind never fails loudly, which is precisely why it needs watching rather than trusting.
  **Denials must be told apart, because they are fixed in different portals:**
  **401** = BC won't accept the app at all (it's missing from *Authorized Microsoft Entra apps*);
  **403** = the app is known but not permitted (missing/unconsented `AdminCenter.ReadWrite.All`, or
  — for a customer's tenant managed as a partner — a missing GDAP relationship). An earlier revision
  assumed "GDAP is always set up" and so reported *both* as missing GDAP; that message sent a
  maintainer connecting their **own** tenant hunting a delegated-admin relationship their setup
  never needed. GDAP is one possible cause of one of the two, not the diagnosis for either.
- **Companies** come from the **automation API** for the chosen environment:
  `GET {automationBase}/companies` → pick one → store `company_id`. A 401/403 here is the
  per-environment registration above, *not* the admin-center one — passing Test connection says
  nothing about it.
- **Ordering.** Environments render production-first, then sandboxes, name-ordered within each
  group: production is what a consultant looks for when something is wrong, and a customer often
  has enough sandboxes to bury it.

UI flow: enter credentials → Test connection (token + list environments) → pick environment →
fetch companies → pick company. The connection card carries the three-step setup checklist in its
rail, because two of the three steps happen outside Entra and are invisible from the app.

## Publish flow

Publishing goes through the **Admin Center API's App Management surface** (`pteInstall`), not the
automation API's `extensionUpload`. Microsoft is removing `extensionUpload` as an upload surface, and
the replacement needs only the tenant-wide *Authorized Microsoft Entra apps* registration — the
per-environment one the automation path required is not needed to publish.

There is no company anywhere in this flow. Extensions install per **environment** and are then
available to every company in it; the company was only ever an artifact of the automation API being
an OData surface bound to `companies({id})`.

Once per delivery:

1. `GET .../apps` — what the environment already has. Read before anything is uploaded, because the
   API only accepts a deferred schedule for an app it already knows.

Then, for each app in the build in **dependency order** (the order the build stamped; deliveries
preserve it by ordering on artifact id rather than re-sorting):

2. `POST .../apps/pteInstall` — a multipart upload carrying the `.app` file itself, the deployment
   schedule, the sync mode, and `acceptIsvEula`. BC reads the app id and version out of the package
   and returns an **operation** to track; both ids are recorded on the per-app result row. The run
   checks the version BC read against the version the build promised, and fails the app if they differ.
3. If the schedule is `Immediate`: poll `GET .../apps/{appId}/operations/{operationId}` until the
   operation reports a terminal state. The poll is keyed on **ids**, which is what makes it safe when
   two extensions share a display name — the retired flow matched on name and could confuse them.
4. Otherwise the operation comes back `scheduled` and never goes terminal while we watch, because BC
   runs it in its own window. The delivery ends in `handed_off` (see below).

`installOrUpdateNeededDependencies` is always sent true (the API defaults it to false). It only
resolves dependencies BC can already see — it cannot conjure a sibling extension that hasn't been
uploaded yet — so it supplements our dependency ordering rather than replacing it.

**No language is sent.** `languageId` sets the extension's install locale, and the toolbox has no
concept of a language; defaulting to `en-US` would be wrong for, say, a Danish customer. BC applies
its own default until a release pipeline can say what the language should be. Open question, below.

### `acceptIsvEula` is sent true, unattended, on the customer's behalf

The API refuses an install without it. There is no interactive surface on which to show the
Marketplace terms, so sending it agrees to those terms for someone else's tenant — the same thing
the admin center's own UI does behind a checkbox, but without a human at the checkbox. That is a
deliberate decision rather than an incidental constant: it is stated here, and it belongs in the
onboarding copy so nobody discovers it by reading the code.

### Deployment schedules, and the two rules around them

`Immediate` installs as soon as BC accepts the upload. The other schedules hand the app to Business
Central to install later, which changes what a delivery can promise:

- **`handed_off`** is a terminal delivery state meaning *BC accepted this and will install it on its
  own schedule*. It is not "succeeded" — we never saw the install happen — and it is not "still
  running" either, because nothing on our side is driving it any more. Cancelling one means
  cancelling it in Business Central (`removeScheduledPteVersion`, keyed on app id + version +
  schedule). There is deliberately **no background reconciler** polling scheduled operations; the
  on-demand read on the environment panel is enough.
- **Several apps on a deferred schedule are refused.** BC decides the order it installs a window's
  queue in; our dependency order only decides the order things were *uploaded*. With one app that's
  harmless, with several it can install a dependent before its dependency, so the delivery is refused
  at scheduling time with a message saying to install right away or release one app at a time.
- **A first install can't be deferred to a version bump.** `NextMinorUpdate` / `NextMajorUpdate` are
  instructions to bump an app BC already has; it rejects them for an app it has never seen. The run
  catches this against the installed-apps read and fails with a message naming the app, rather than
  letting BC answer with a 400 that doesn't say which rule was broken.

Because the stored values go to the API verbatim, a release pipeline saved under the retired API
holds wording this one rejects. Those values were migrated with the columns, and both the edit screen
and the scheduling path refuse an unmigrated value rather than guessing at it — that refusal is what
makes the data migration required rather than optional.

Per-app result statuses are `pending → uploading → installing → completed | failed | skipped`, plus
`scheduled` for an app handed to BC's own window. A `skipped` row is one an earlier app's failure
short-circuited.

**Failure detail comes from the codes, never the message.** A failed operation carries `errorMessage`
localized to the *environment's* language (a real failure came back in Danish) with the structured
`code` / `innerError.code` embedded in it as JSON. The run keys everything on those codes and carries
the message through only as display text.

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
  the project's timezone; owns the atomic cancel/claim transitions. **As built:** the engine slice
  ships `ReleaseBuildNowAsync` (immediate run, `scheduled_for = now`) + `RunDeliveryAsync` (claim →
  upload → install → poll); it takes the access token through a narrow **`IDeliveryTokenSource`**
  seam (implemented by `ProjectConnectionService`) so the orchestration is unit-testable without the
  OAuth round-trip or the key ring — mirroring the `IBcAutomationClient` seam. The future-time
  scheduler and cancel surface land in the scheduling slice.
- **`DeliveryScheduler`** (`BackgroundService`) — polls for due `scheduled` rows, enqueues to
  **`DeliveryQueue`** (bounded `Channel`); **`DeliveryWorker`** drains and runs the publish under the
  triggering user's captured `AmbientOrganizationScope` identity. Persisted rows = restart-resume.

## UI surfaces

- **Project detail:** a "Business Central connection" section — tenant id, client id, secret
  (write-only) + secret-expiry, Test connection (flags missing GDAP), timezone, and the fetched
  environment list with Refresh. The single sensitive screen; owner/Admin only. Each environment row
  carries per-environment settings (its company, and its **update window** — start/end time, or
  "Any time"); these hang off the row's settings affordance so the table stays calm. Setting/clearing
  a window must survive a Refresh (it's user config on a fetched row — the upsert touches only the
  discovered fields, keyed on `(project_id, name)`), and a vanished environment keeps its window
  read-only. Each row also shows the environment's **status** (a badge, toned by the same
  classification the delivery gate uses, so the badge and the refusal never disagree) with its
  version underneath, and an **Open in Business Central** link built from `web_client_login_url` —
  the question the row has to answer is "is this environment safe to deploy to right now".
- **Release pipelines:** a listable surface alongside Build pipelines (own icon — e.g. `rocket` for
  build stays, a `send`/`upload-cloud` for release), with a create/edit dialog: name, source build
  pipeline, target environment (picker), version mode, schema sync mode (Force Sync behind a confirm),
  default publish time.
- **Schedule a release:** lives on the **Release pipeline** — a "Release" action that's enabled once
  the source Build pipeline has a *successful* build. It defaults to the **latest successful build**
  (with the option to pick an older one), then "pick the date+time" (prefilled to the **next opening
  of the target environment's update window**, or now if it has none) → creates a scheduled
  `ProjectDelivery`. The user can override the prefill to run now or any other time; doing so outside a
  set window is recorded on the delivery. Failed/in-progress builds aren't
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

**As built (phase 4b):** shipped as `DeliveryTools` (`Services/Mcp/Tools/DeliveryTools.cs`) — a
trio so the flow is usable end-to-end: `list_release_pipelines` (discover the id), `publish_build`
(release a `ready` build *now*, delegating to `DeliveryService.ReleaseBuildNowAsync`), and
`list_deliveries` (poll history with per-app outcomes). Publishing runs in the same in-process
worker as the web "Release now", so `publish_build` returns the new delivery id to poll rather than
blocking. Access-gating + validation come from `DeliveryService`/`ProjectAccess` unchanged; the tool
only maps `ProjectAccessDeniedException`/`PlanValidationException` to `McpException`. Scheduling a
*future* delivery and the Production extra-confirm stay web-only — the agent path is release-now.

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
   **As built:** partial-failure reporting + Production/Force-Sync confirms shipped in phases 2–3.
   **Phase 4a** adds the secret-expiry-vs-schedule guard (a warn-but-allow note in the release dialog
   and reschedule modal when the picked time is past the secret's expiry — the run's hard-fail stays
   the backstop) and audit-log entries: `ReleasePipeline` (create/edit/delete) and `Project` are now
   audited, the latter **column-scoped** to BC connection/secret changes so the background discovery
   worker's cache writes and name edits don't flood the log. Deliveries keep their richer
   self-history rather than the entity-granularity interceptor (which would miss the `ExecuteUpdate`
   cancel/reschedule transitions and flood on every worker save). **Phase 4b** is the MCP trio above.

**As built (phases 1–3 are in `main`):**
- Phase 1 = #462; phase 2 = #465 (CRUD) + #468 (publish engine) + #469 (UI).
- **Phase 3 (scheduling):** the per-environment update window (`update_window_start`/`update_window_end`
  on `ProjectEnvironment`, edited on the project's BC page), the schedule picker (prefilled to the
  next window opening in the project tz), `DeliveryService.ScheduleDeliveryAsync` / `CancelDeliveryAsync`
  / `RescheduleDeliveryAsync`, a `DeliveryScheduler` poller, and Cancel/Reschedule + an "outside window"
  badge in delivery history. Overriding the window is audited via
  `ProjectDelivery.ScheduledOutsideWindow`.
  - **Scheduler tenant scope — deliberate divergence from `ReleaseAutoImportScheduler`:** the delivery
    scheduler enumerates **all non-pending orgs *including the system org*** (the one new
    `IgnoreQueryFilters()`, on org enumeration only — per-org work stays filtered). It must *not* skip
    the system org the way the release auto-importer does, because in single-tenant (and fresh
    bootstrap-admin) deployments the working org **is** the system org, so its deliveries have to run.
  - **Restart-resume:** scheduled rows survive a restart (re-picked on the next due sweep); a delivery
    orphaned mid-publish is failed on the scheduler's first per-org sweep (nothing runs yet at startup,
    so an active delivery is never tripped) — folded into the scheduler to avoid a second
    `IgnoreQueryFilters()` startup site.
  - Times are entered/displayed in the project's `bc_time_zone` (customer's local time). The window
    may wrap past midnight.

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
- **Environment listing:** fetch via the Admin Center API as the primary path; Test connection
  **names the step that failed** — 401 (app not on BC's authorized-apps list) and 403 (permission or
  GDAP) are separate outcomes with separate remedies. GDAP is *not* assumed: the same connection
  serves the maintainer's own tenant, where no delegated-admin relationship exists at all. Manual
  entry is a fallback.
- **Version mode:** all three offered; default **`Current version`**.
- **Trigger model:** no auto-publish in v1. The user explicitly schedules a delivery for a concrete
  date+time; it then runs automatically at that time, and is **cancellable until a worker claims it**.
- **Per-environment update window (revised):** each `ProjectEnvironment` carries a recurring daily
  update window (start/end time in `bc_time_zone`, nullable = any time), mirroring BC's admin-center
  environment update window — the model BC admins already know. It is a **default, not a lock**:
  scheduling prefills `scheduled_for` to the next window opening, and the user can override to run now
  or any time, with overrides recorded on the delivery. This **revises** the earlier framing that the
  schedule was "not a recurring window"; the *execution* model is unchanged (a concrete per-delivery
  `scheduled_for`), but the **default** that seeds it is now a per-environment recurring window rather
  than `ReleasePipeline.default_publish_time` (which this supersedes and likely retires).
- **Expired-secret behaviour:** warn-but-allow at scheduling; the run hard-fails with a clear "secret
  expired — rotate it" message if it's actually lapsed when the worker fires.

## Open questions

The shape is settled (see Decisions). What's left is **implementation detail to settle when building**,
not architecture:

- **Install language.** `pteInstall` takes a `languageId` that sets the extension's install locale.
  We send none, because the toolbox has no language concept and `en-US` would be wrong for a Danish
  customer. It probably belongs on the release pipeline, beside the other per-target settings.
- **Whether to offer "install in Business Central's update window".** The API's `UpdateWindow`
  schedule is supported by the engine and deliberately absent from the picker. It means *whenever
  Microsoft next patches this environment*, which is a different promise from the delivery slot the
  toolbox already schedules; offering both without distinguishing them would mislead.
- **Re-releasing a version that's already scheduled.** BC won't hold two versions of one app for the
  same schedule, so re-releasing the same version probably 400s. Decide between pre-checking,
  cancel-then-install, and mapping the error to a clear message.
- **Mixed-tool invisibility.** A version scheduled through the web client's Extension Management page
  isn't visible in the admin center until it installs, and vice versa. If a customer's own consultant
  uploads that way while we schedule through the admin center, neither surface shows the other's
  work. That needs UI copy before it generates support calls.
- **Partial-failure semantics:** one app installs, a dependent fails — surface like the build report.
- **Secret-expiry warning lead time** (the "~N weeks" before expiry to start nagging).
