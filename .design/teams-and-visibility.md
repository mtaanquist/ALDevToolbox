# Teams and project visibility

Status: **slices 1 and 2 shipped.** Teams exist and are managed (slice 1), and
project visibility is modelled and fully enforced on every project-id surface
(slice 2) — though nothing sets a project to anything but Public yet, because the
switch that does is the last thing to land. Slice 3 (release-id surfaces, Object
Explorer, MCP, and the project detail Access section that turns the feature on) is
specified here but not implemented. Each section below is labelled with the slice
that lands it.

## Why

Projects and Pipelines are approaching real internal use, which means real customer
data: repository URLs, build logs, compiled `.app` files, and — through Object
Explorer — the customer's source. Today every user in an organisation can read all
of it, and only the project's owner or an org Admin can write. `artifacts.md`
deferred project visibility past v1 on purpose; NDAs and consultants who work with
competing customers are what bring it back.

Two things are needed and they are separable, which is why this doc covers both:

- a way to name a **group of people** — a team — that outlives any one project;
- a way to say **how visible a project is** to people outside that group.

Being on a team confers view and manage rights on the projects that team is
assigned to. No project is assigned to one yet: slice 2 built and enforced the
model, slice 3 ships the UI that uses it.

### Named users

- **An org Admin setting up a customer engagement.** Creates a team, adds the
  consultants on that account, and later marks the customer's project Private.
  Does not want to be the bottleneck for every membership change afterwards.
- **A consultant who manages their engagement's team.** Adds a colleague who just
  joined the account. Is not an admin and should not need to be.
- **A consultant on a different account.** Should not see the first customer's
  project contents at all — but should not be confused by a project that seems to
  vanish, which is why a Private project still shows its name.

## Data model

### Slice 1 — `teams`, `team_members`

| Entity | Table | Notes |
|---|---|---|
| `Domain/Entities/Team.cs` | `teams` | `Id`, `OrganizationId`, `Name`, `CreatedAt`, `UpdatedAt`. **Hard delete.** |
| `Domain/Entities/TeamMember.cs` | `team_members` | Surrogate `Id`, denormalised `OrganizationId`, `TeamId`, `UserId`, `IsManager`, `CreatedAt`. Unique `(team_id, user_id)`, index on `user_id`. Cascade deletes. |

Both tables carry `organization_id` and are scoped by the standard
`ScopeToOrganization<T>()` query filter in `AppDbContext.OnModelCreating`. The
per-entity configurations install **no** filters of their own, per the house rule.

Three choices worth recording:

- **Teams are hard-deleted.** A team is a name plus a membership list — there is
  nothing to recover. A soft-deleted team would have to be excluded from every
  visibility predicate written from slice 2 onward, and a missed exclusion there
  fails *open*, exposing a project. The blast radius of the alternative is worse
  than the loss.
- **`IsManager` is a boolean, not a role enum.** There is exactly one elevated
  capability on a team. An enum would invite a hierarchy nobody has asked for.
- **`team_members.organization_id` is denormalised.** A membership and its team
  always belong to the same org (the service enforces it on insert), but carrying
  the column means the row is scoped by the same one-line query filter as every
  other tenanted table rather than by a join the filter would have to reach through.

Name uniqueness is per-org and **case-insensitive**, enforced by
`ix_teams_org_name_lower` — a functional unique index on
`(organization_id, lower(name))` written by hand in the `20260904000000_AddTeams`
migration, because EF cannot model one. This mirrors `ix_oe_projects_org_name_active`
and `ix_oe_pipelines_project_name_active`. `TeamService` pre-checks the same rule so
the user gets an inline field error rather than a 500; the index is what makes the
rule true under concurrency.

### Slice 2 — `oe_project_teams` and `visibility`

| Entity | Table | Notes |
|---|---|---|
| `Domain/Entities/ObjectExplorer/ProjectTeam.cs` | `oe_project_teams` | Surrogate `Id`, `OrganizationId`, `ProjectId`, `TeamId`, `CreatedAt`. Unique `(project_id, team_id)`, index on `team_id`. Cascade. |

`oe_projects` gains `visibility text NOT NULL DEFAULT 'Public'`, a new
`ProjectVisibility { Public, ReadOnly, Private }` stored via `HasConversion<string>()`.
Visibility is one level for the whole project regardless of how many teams are
assigned. Migration `20260905000000_AddProjectVisibility`.

## The visibility model (slices 2–3)

| Level | Who can view | Who can manage |
|---|---|---|
| **Public** (default) | Everyone in the org | Owner, org Admin, SiteAdmin |
| **Read-only** | Everyone in the org | Owner, org Admin, SiteAdmin, **assigned-team members** |
| **Private** | Owner, org Admin, SiteAdmin, **assigned-team members** | Same set |

- **Delete is carved out.** Assigned-team members get everything management covers
  *except* deleting the project — soft-delete stays with the owner, org Admin, and
  SiteAdmin. A team grant is about doing the work, not about ending it.
- **Org Admins and SiteAdmins bypass visibility entirely.** This cannot live in an
  EF query filter (the membership set is not a scalar and the bypass is not a model
  fact), which is why enforcement is explicit predicates rather than a second global
  filter.
- **A Private project still shows its name** in `/projects`, as a greyed, locked,
  non-clickable row with the caption "Private — visible to its team". Everything
  else about the row — owner, status, counts — leaks activity and is omitted. MCP's
  `list_projects` omits Private projects entirely; a locked name is no use to an agent.

### The invariant

> `Visibility != Public` **⇔** at least one team is assigned.

Enforced atomically by a single `ProjectService.SetAccessAsync(projectId, visibility,
teamIds)` — never by two independent writes that could interleave into a Private
project nobody can see.

Its consequence for teams: **deleting a team is refused while it is the last team on
any non-Public project**, listing the projects that block it. The refusal is
deliberate rather than auto-resetting those projects to Public — silently making a
Private project world-readable is exactly the failure this feature exists to prevent.
That refusal lives in `TeamService.DeleteTeamAsync`.

## Authorisation

### Slice 1 — `TeamService`

Two levels, deliberately different:

| Capability | Who |
|---|---|
| Create a team, delete a team | Org Admin, SiteAdmin |
| Rename, add / remove members, promote or demote a manager | The above, plus a member of that team with `IsManager` |

- `CanManageTeamAsync(teamId)` / `EnsureCanManageTeamAsync(teamId)` resolve in that
  order: SiteAdmin → org Admin → `IsManager` member of *that* team. Modelled on
  `Services/ObjectExplorer/ProjectAccess.cs`.
- A caller with no signed-in user (a background worker under an ambient org scope)
  gets `false`, never an exception — the check is safe to call from a render path.
- **Zero managers on a team is a valid state.** Org Admins can always manage any
  team, so there is nothing to recover from, and a last-manager guard would strand
  the final manager on a team they wanted to leave. `/teams/{id}` shows a quiet hint
  in that state rather than an error.
- Every mutation re-checks authorisation inside the service. Hiding a button is a
  courtesy to the user, not the gate.

### Slice 2 — `ProjectAccess` grows a second axis (shipped)

`ProjectAccess` stays the single authority for project authorisation and gains:

- `AccessSnapshot(UserId, IsSiteAdmin, IsOrgAdmin, TeamIds)`, loaded lazily once per
  DI scope, with `BypassesVisibility => IsSiteAdmin || IsOrgAdmin`. A null user means
  "no grants", never a throw. Snapshot staleness across a long-lived Blazor circuit
  is accepted — it matches the circuit-scoped `AppDbContext` consistency model, and a
  page reload picks up membership changes.
- **Manage axis**: `CanManageAsync(projectId, createdByUserId)` — SiteAdmin → owner →
  org Admin → member of any assigned team.
- **Delete axis**: `EnsureCanDeleteAsync` keeps today's stricter rule.
- **View axis**: `CanViewAsync(projectId)` / `EnsureCanViewAsync(projectId)`, throwing
  `ProjectAccessDeniedException`.
- `VisibleProjectPredicate(snapshot)` for list queries, skipped when the snapshot
  bypasses.
- `LockedProjectPredicate(snapshot)` — the complement of the above, written out
  longhand rather than negated at the call site, so `/projects` can pull the
  name-only rows in one query and a reader can check the two halves against each
  other.
- **Slice 3:** `IsReleaseVisibleAsync(releaseId)` / `VisibleReleasePredicate(snapshot)` — a
  `Release` has no `ProjectId`, so a release is hidden iff it is linked (via
  `OeProjectBuilds.ReleaseId` or `ImportJob.ProjectId`) to a Private project the
  caller cannot view. Unlinked releases stay governed by the org filter alone.

## Gated-surface inventory (slices 2–3)

Every read surface that takes a project id or a release id must opt in. A new
surface added later is **not** covered by default — add it here and gate it.

All of the project-id surfaces below are gated as of slice 2; the release-id and
MCP sections are slice 3.

Two list surfaces answer the same question differently, on purpose.
`ArtifactService.ListProjectsAsync` is what `/projects` renders, so it returns a
**locked name-only row** for a project the caller cannot open.
`ProjectService.ListProjectsAsync` feeds the project *pickers* (new pipeline, new
release pipeline) and simply **omits** those projects — a name you cannot act on is
only in the way of choosing one you can.

**Project-id surfaces** (`EnsureCanViewAsync` at the top, or `VisibleProjectPredicate`
in the list query): `ArtifactService` (`ListProjectsAsync`, `GetProjectHeaderAsync`,
`ListPipelinesAsync`, `GetPipelineHeaderAsync`, `ListBuildsAsync`,
`ListBuildsForProjectAsync`, `GetBuildDetailAsync`, artifact bytes and log getters,
`GetProjectIdForReleaseAsync`, `ListComparableBuildsAsync`); `ProjectService`
(`ListProjectsAsync`, `GetProjectAsync`, `ListProjectReleasesAsync`,
`ListSupplementalSymbolsAsync`); `PipelineService` and `ReleasePipelineService` list
and get reads; `DeliveryService` (`ListDeliveriesAsync`, `GetDeliveryAsync`,
`ListDeliveryHistoryAsync`); `Bc/ProjectConnectionService` (`GetConnectionAsync`,
`ListEnvironmentsAsync`); `ProjectDiscoveryService.GetDiscoveryAsync`;
`Endpoints/ArtifactEndpoints.cs` (gate before streaming bytes — slice 3; the
`ArtifactService` byte getters those endpoints call are already gated).

A pipeline, a release pipeline, a build, and a delivery have no visibility of their
own: each gate resolves the owning project and defers to the one authority. A row
that does not exist passes the gate — the read below returns nothing on its own, and
refusing would confirm an id that is not in this org.

**Release-id surfaces** (via `IsReleaseVisibleAsync` / `VisibleReleasePredicate`):
`ObjectSearchService` and `ReferenceQueryService` `ReleaseVisibleAsync` hooks — which
covers search, find-references, and reference sessions;
`ObjectExplorerService.ListLatestPipelineBuildReleasesAsync`; `SourceViewerService`;
`ReleaseComparisonService` (**both** sides of a comparison);
`Endpoints/ObjectExplorerViewerEndpoints.cs`.

**MCP** (`Services/Mcp/Tools/`, per the parity guide in `PROJECT.md`):
`ArtifactsTools` — apply the predicate inside `ResolveProjectAsync` /
`ResolveReadyBuildAsync`, which query `_db.OeProjects` directly and so are where all
six tools inherit the gate; `list_projects` filters Private out entirely.
`DeliveryTools` — same, on project resolution. `ObjectExplorerTools` — release-keyed
tools inherit the delegated `ReleaseVisibleAsync` hooks; verify each.

**Not gated**: background worker internals (queues and workers must not be starved
by a view gate), `/site-admin/*`, and Public / Read-only reads.

## Audit

`AuditEntityType.Team` and `AuditEntityType.TeamMember` (slice 1), plus
`AuditEntityType.ProjectTeam` and a `Visibility` entry in the `OeProject` column
gate (slice 2). A `ProjectTeam` row stays unnamed like every other join row; the
snapshot carries the project and team ids.

Team and membership changes are audited in full rather than behind a column gate:
who could see what, and when that changed, is the point of the feature, and there is
little to gate anyway — a team has only a name, a membership row only its manager
flag. `Team.Name` resolves through the existing `AuditEntityName` candidate list, so
the log reads "changed the team Nordics". A `TeamMember` row stays unnamed, like
every other join row; its snapshot carries the team and user ids.

## UI

### Slice 1

- **Administration → Teams** (`/admin/administration/teams`, Admin only). Team list
  with member and manager counts, "Create team" as the page's one primary action,
  delete behind `ConfirmDialog`. Empty state: "No teams yet. Teams let you limit who
  can see and change a customer's project." The team name links to the shared detail
  page.
- **`/teams`** (any signed-in user). The teams *you* are on. Admins wanting the full
  list have the Administration tab; showing everyone every team here would bury the
  one thing the page is for. Empty state: "You're not on any team yet. Ask an admin
  to add you."
- **`/teams/{id}`** (any signed-in user). Roster visible to everyone in the org —
  membership is not a secret inside an organisation, and knowing who to ask is the
  point. Rename, an explicit "+ Add member" button opening a picker of org users not
  yet on the team, Remove, and a Manager toggle render only when
  `CanManageTeamAsync`. Not a ghost-row grid — see the `AdminProjectDetail`
  cautionary tale in `CLAUDE.md`. With no manager, a quiet hint: "No manager yet —
  admins manage this team's members."
- No sidebar entry for `/teams` in slice 1; it is reachable from Administration and
  from links on team names.

### Slices 2–3

- **Project detail gains an Access section** (visible when the viewer can manage the
  project): the three visibility levels with plain-language captions, a team
  multi-select, and one outline "Save access" button. Inline errors keyed
  `Visibility` / `Teams`. This is the switch that turns the feature on, which is why
  it ships **last** — after OE and MCP enforcement. Shipping the Private toggle
  before those would let a user believe a project is hidden while `search_objects`
  still reads it.
- **`/projects` renders locked rows** for Private projects the viewer cannot see:
  name only, greyed, a `lock` icon, a plain `<span>` rather than a link. Direct
  navigation to a locked project maps `ProjectAccessDeniedException` onto
  `ProjectDetail`'s not-found state — don't confirm more than the list already shows.

## Out of scope

- **Per-user grants on a project.** Teams are the unit. A one-person team is the
  answer to the case that wants this, and it stays greppable.
- **Nested teams / team hierarchies.** No evidence anyone wants them, and they make
  every visibility predicate recursive.
- **Cross-organisation teams.** Both tables are org-scoped; a person belongs to one
  org, so a team cannot span two.
- **Per-pipeline or per-build visibility.** Visibility is a property of the project;
  everything beneath it inherits.
