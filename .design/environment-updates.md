# Environment updates — the Upgrades fleet page

> **Status: shipped** ([#657](https://github.com/mtaanquist/ALDevToolbox/issues/657), stages 1–4b).
> The page is `Components/Pages/Upgrades/UpgradesPage.razor`; the services are
> `UpgradeFleetService` (read), `UpgradeActionService` (request/cancel/history),
> `UpgradeActionWorker` (booked slots) and the two write methods on
> `ProjectConnectionService`, all under `Services/ObjectExplorer/Bc/`. The mirror lives in
> `bc_next_update_*` columns on `ProjectEnvironment`; the actions and the history are one
> table, `oe_environment_upgrade_actions` (`EnvironmentUpgradeAction`). The grant is
> `team_members.manages_updates` — see [`teams-and-visibility.md`](./teams-and-visibility.md).
>
> This doc is the record of intent for the tool; where a detail has drifted, the code is the
> source of truth. Everything about *publishing builds* to an environment is a different
> flow and lives in [`saas-delivery.md`](./saas-delivery.md).

## Goal, and the named user

The upgrade team decides *when* around a hundred customers take a Business Central
platform update. Twice a quarter they open each customer's admin center and push the
scheduled update date out to the latest date Microsoft still allows, buying everybody time
before the release lands. Separately, a customer agrees a slot — "tonight at 20:00" — and
that environment is told to update then, whatever its own update window says.

Both were a hundred admin-center visits per sweep. `/upgrades` is one table over every
environment of every customer the viewer can see, with the same two moves as bulk actions
over a checkbox selection.

The named user is **a member of the upgrade team scheduling platform updates for a hundred
customers, who knows nothing about this codebase**. Everything the page says is written for
them: no class names, no column names, no API vocabulary.

## The grant

Acting on an environment requires `team_members.manages_updates` in one of the project's
assigned teams; org Admin and SiteAdmin may act everywhere. The reasoning — why a per-
membership flag rather than a fourth `UserRole`, why it is a different axis from managing
the team, the project, or owning it, and why it deliberately never enters the sign-in
claims — is `teams-and-visibility.md`'s *The environment-update grant* section, and is not
repeated here.

What matters at this end: `ProjectAccess` is the only authority. `CanUseEnvironmentOps`
gates the sidebar entry and the page without naming a project;
`UpdateOpsProjectPredicate` answers per row inside a list query;
`EnsureCanManageEnvironmentUpdatesAsync` re-checks on every write — including a write the
worker fires hours later. Because the flag is not in the cookie, a grant taken away this
afternoon is gone by the next page load rather than the next sign-in.

## The mirror — what the page lists from

A fleet page that asked Business Central per row would make a hundred round trips to draw
one table. So the *next platform update* for each environment is mirrored onto its row:
seven nullable `bc_next_update_*` columns holding the version, the type and status verbatim
as the API spells them, the scheduled date, the latest date the update can still be pushed
to, whether it ignores Microsoft's update window, and when the mirror last succeeded.
Opening `/upgrades` makes no call to Business Central at all.

**Selection rule.** The *selected* update when the customer has picked a slot — that is the
answer even when a newer version is on offer. Otherwise the newest `Available` one,
compared numerically per segment (a string compare puts `10.1` before `9.2` and would
mirror last year's update as the next). Otherwise the six value columns are cleared: an
environment with nothing on offer shows nothing rather than a stale version. An unreleased
version is never a candidate — it carries no date to schedule.

**Freshness.** The mirror rides the same per-environment loop as the update-window mirror,
one updates call per environment, with the same failure isolation: one environment's
refusal costs neither the environment list nor the other environments' answers, and leaves
the previous mirror and its age intact rather than blanking it.
`bc_next_update_fetched_at` is stamped on every *successful* read, **including one that
found nothing** — "nothing is scheduled" and "we never asked" are different facts and the
page says which it has.

Three things fill it. A consultant's Refresh on the project's Business Central tab; a
nightly sweep (`EnvironmentRefreshScheduler`, a fixed quiet UTC hour, `DeliveryScheduler`'s
shape) that offers every BC-connected project to the in-process
`EnvironmentRefreshQueue`/`Worker` pair so the fleet is fresh each morning without anyone
opening a project; and the page's own **Refresh from Business Central** action, which feeds
the same queue so a sweep and a hand-triggered refresh coalesce. Rather than telling the
reader to reload after that, the page polls itself every 20 seconds for up to three
minutes, on the renderer's synchronisation context so a tick cannot collide with a click on
the circuit's one `AppDbContext`; a **Reload now** button in the same notice is there for
anyone who doesn't want to wait. The sweep takes a
non-user-gated refresh path (the `AcquireDeliveryContextAsync` precedent) and never stamps
`bc_connection_verified_at` — a refresh nobody asked for must not present itself as the
consultant's own connection test.

The **full** updates list is still fetched for the environment panel rather than read from
the mirror: the mirror is one row for listing many environments, not a replacement for the
detail a consultant opens on purpose. That fetch is cached briefly once made — see "The
environment panel" in `saas-delivery.md`.

## The two writes

Both act on the update the selection rule picks, both re-read the environment's updates
live first (so the page and the write can never disagree about which update is meant), both
are gated on the environment-updates grant rather than on managing the project, and both
re-mirror the row from a fresh read afterwards so the table shows the new date without
waiting for the nightly sweep. A failed re-read costs the freshness, never the write.

- **Push the date to the latest** sets the date to the update's latest selectable date.
  Refuses when there is no update on offer, when Business Central gave the update no latest
  date, and when the date is already there.
- **Update now** sets the date to the current moment and is the *only* operation that ever
  ignores the environment's update window — a customer who has agreed a slot is asking for
  the upgrade regardless of their window, and nothing else has the right to take that
  protection away. Refuses only when there is nothing on offer.

Each refusal is a `PlanValidationException` the fleet page shows against that one row, not
a failure of the batch.

### The wire shape

Both go through the same `PATCH .../environments/{family}/{name}/updates/{targetVersion}`
the environment panel's version pick uses, with `selectedDateTime` and
`ignoreUpdateWindow` added alongside the `selected` / `targetVersionType` it already sent:

| Field | Shape | Sent when |
|---|---|---|
| `selected` | JSON boolean, always `true` | always — a date set on an update the customer had not picked selects it in the same request |
| `targetVersionType` | string, verbatim from the updates read | when the read gave one |
| `selectedDateTime` | ISO-8601 in **UTC** (`yyyy-MM-ddTHH:mm:ssZ`) | only when the caller is moving the date; omitting it leaves the customer's existing slot alone |
| `ignoreUpdateWindow` | **a real JSON boolean** | only by "update now" |

`ignoreUpdateWindow` is a boolean and not the string `"true"` the Microsoft 365 licence
endpoint documents, because this body already carries `selected` as a boolean and the same
endpoint reads both flags back as booleans. **If Business Central ever refuses it, the
string form is the first thing to try** — that is the documented fallback, and this API
family has drifted on exactly this before, which is why every flag is read back
case-insensitively and no logic keys on localized text.

**Don't write a concrete Admin Center API version into this doc.** It lives in exactly one
place, `BcConstants.AdminApiVersion`, watched by `.github/workflows/bc-api-version.yml`;
naming a version in prose is how the last one rotted eight releases behind. Same rule as
`saas-delivery.md`.

## Actions and history — one table

Every move is one row in `oe_environment_upgrade_actions`, and those rows **are** the
per-environment activity feed; there is no second log behind it. A row carries the customer,
the environment, the kind (push-to-latest / run-now), a status, who asked and when (as a
denormalised `"name <email>"` string, so the history still names them after the account is
gone), the fire time, when it was sent, the outcome in plain words, and who cancelled it.
The table is deliberately **not** in `AuditInterceptor`'s audited map: it is itself a log,
and auditing a log records every event twice.

Status is `Pending → Sent | Failed | Cancelled`.

**Immediate is a direct send.** "As soon as possible" calls Business Central on the request
thread and the row is written in its finished state, `Sent` or `Failed`, in the same
operation. There is no worker hop and nothing to cancel, because by the time the row exists
the change has already landed or been refused. A refusal writes a `Failed` row *and*
rethrows, so the page shows its usual per-row message while the feed keeps the attempts that
came to nothing.

**A booked slot is a `Pending` row and nothing else.** "At a time we agreed" writes the row
with its fire time and calls nobody. Nothing is enqueued: `UpgradeActionWorker` finds due
rows by polling the table every 30 seconds, so a slot booked for tonight survives this
afternoon's deploy, which an in-memory channel would not. Only "update now" offers a slot;
push-to-latest is housekeeping ahead of a release and is always immediate, though it records
its rows the same way so one feed reads uniformly. The worker's per-org enumeration is the
one cross-org read, and it needs no bypass — the organisations table carries no tenant
filter; per-org work stays inside the filter.

**The race rule.** Cancel works until the worker sends. The worker claims a row by stamping
`sent_at` while it is still `Pending`; a cancel is an `UPDATE ... WHERE status = 'Pending'
AND sent_at IS NULL`. Both sides are the same compare-and-set
`DeliveryService.RunDeliveryAsync` uses, so exactly one wins and the loser is told in words
— a cancel that arrives too late says the action has already run rather than appearing to
work, and a send that arrives after a cancel never touches the tenant. There is deliberately
no version column: with a token the loser would have to re-read and work out what the new
state meant, which is the question the `WHERE` clause already answers. A row left
claimed-but-unfinished by a restart is failed on the worker's first sweep after it, never retried — we
know the send started and not whether it landed.

**Each booked row fires as the person who booked it.** The worker enters the ambient org
scope with the requester's user id (the `DeliveryWorker` precedent), which buys two things
at once: the audit row names them rather than "unknown", and the grant is re-checked as
theirs at fire time, so somebody taken off the upgrade team during the afternoon does not
get their evening slot fired anyway. The writes re-read the environment live, so an update
applied or withdrawn in the meantime, a blocked environment, or rotated credentials land the
row as `Failed` with the reason in the feed rather than guessing. One row's failure never
stops the sweep.

**An entry's headline says which thing was asked for.** Booking an update and starting one
are opposite claims, so the feed compares the fire time to the request time and titles the
entry accordingly: a booking stays "Booked the update" whether it is still waiting, has since
run, or was called off; an immediate send says "Started the update", and one that was refused
says it tried. Failure outcomes are composed in the past tense at the moment they are stored,
with any trailing "try again" advice dropped — the refusals are worded for somebody standing
at the form, and a history entry read a week later must not claim an environment is still busy.

**Reading the feed is a visibility question**, not an ops one: anyone who can see the customer
can read it (`EnsureCanViewAsync`), and only Cancel needs the grant. It shows the newest 50
entries for one environment, and one component (`Components/Shared/EnvironmentActivityFeed`)
renders it in both places — the Upgrades page's per-row Activity panel and the environment
panel on a project's Business Central tab — so the history cannot read differently depending
on which page somebody opened. Its empty state says nothing has been done to this environment
yet.

## The page

One table, one row per non-missing environment of every project the viewer can see: the
customer, the environment and its type, its status, the version it is on, the mirrored next
update (version, when, and a marker when it ignores Microsoft's window), the latest date that
update can still be pushed to, and how old the mirror is. Filters are text search,
environment type, and "update available"; loading, empty and populated states as usual.

**The join is the guard.** `ProjectEnvironment` has no visibility rule of its own — it
inherits its project's. `UpgradeFleetService.ListFleetAsync` therefore reaches the
environments table *through* `VisibleProjectPredicate`, and any future query that lists
environments must do the same rather than reading the DbSet directly. "May act" is computed
in the same query from `UpdateOpsProjectPredicate`, so a fleet of a hundred costs one round
trip; a row the viewer may see but not act on shows a lock instead of a checkbox. The org
fence sits underneath both.

**Two actions, two voices.** Each runs over the checkbox selection behind a confirm that
lists every selected environment with what will happen to it and — grouped at the bottom
under its own heading — the ones that will be passed over and why.

- **Move dates to the latest** previews each date and the date it moves to.
- **Start the update now** is the sterner one. It carries the danger button variant in the
  toolbar (the one control here that acts at once and cannot be taken back — a variant, not a
  second primary button), says plainly that Microsoft will start the updates whatever the
  environment's update window says, counts the production environments in the selection just
  above the gate, and holds its confirm button disabled until the person types "update".

That dialog re-voices itself on the choice inside it, because immediate and booked carry
opposite promises. Immediately keeps the danger button, the typed word, and "once it starts
you cannot stop it". A booking gets the normal button, no typed word, a confirm label carrying
the time, and the sentence that makes it safe — cancellable from the Upgrades page until it
runs. Saying "you cannot stop it" over an action with a Cancel button would be a plain
contradiction, so `ConfirmDialog` learned to keep tracking its parameters while open (only for
callers that opened it on its own parameters) and to accept a caller-owned `ConfirmDisabled`,
which is what holds the button while the picked time has already passed for some customer.

**Times belong to the customer.** A slot is picked and displayed in the project's own
Business Central time zone, named explicitly beside the picker, and stored UTC. Across a
selection spanning zones the same wall clock is read *per customer* — "20:00 in each
customer's own time zone" — which is what "tonight at eight" means to the person who agreed
it, and the dialog says so rather than quietly picking one zone for everybody. Because a
`datetime-local` field renders in the browser's locale, the booking is echoed under the field
in the page's own 24-hour format, naming the zones, and *that* sentence settles what was
picked. A time already past is refused per customer **by name**, since the reason only some
are past is that they are in another country.

**Running a batch.** The run is sequential in the page's own circuit, reports per row as it
goes, never lets one environment's refusal end the batch, and can be stopped between
environments (never mid-write). Afterwards the page re-reads the fleet so the rows show the
re-mirrored truth. The summary sits in a sticky bar above the table — findable after a long
batch has scrolled — and takes a warning tone whenever anything was skipped or failed, because
that is not a neutral outcome. Two kinds of per-row refusal are told apart by the
`PlanValidationException` field key rather than by reading the sentence: an `Environment` key
means the customer's connection needs attention somewhere else, so the row links to the project
and the summary says so; an `Update` key means this particular update can't move, which its own
message already explains. A genuine failure never shows raw exception text — the row says
Business Central didn't accept the change and links to the project, and the detail goes to the
log.

**A booking is visible on the fleet row itself**, not only in the batch result that made it
(which a reload discards). One booking shows the whole fact — when, in whose time, who booked
it — with a Cancel beside it; several show the nearest and a count. Either way that marker *is*
the disclosure that opens the history, so "Update history" is a second door and never the only
one. Confirming an update-now over an environment that already has a booking waiting groups it
under "Already booked" in the preview, with what it is booked for: the run still acts on it, and
adding a second booking is a thing to notice before the click.

**Audit.** Each of the two writes records an audit row, and this is the one place in the
application that writes to `audit_log` outside `AuditInterceptor`. It has to be: the writes land
on the customer's tenant and touch no row of ours that the interceptor watches — and the
re-mirror afterwards is deliberately outside `AuditInterceptor.EnvironmentSettingColumns`,
because the nightly sweep writes those same columns and would otherwise fill the log with rows
nobody made. The entry is a `ProjectEnvironment` row keyed by the environment id, and its
snapshot keeps the log's "state before the change" contract — the update as we read it, plus a
plain-words `Action` naming which of the two writes it was, since the audit model records rows
changing and these are events. The actor is resolved from the database rather than from claims,
because a Blazor circuit has no `HttpContext` for the interceptor's own lookup to read. A refused
row writes nothing: nothing changed. For a booked action the audit row is written at send time,
by the worker, so the log records what actually reached Microsoft while the activity feed records
the whole request-and-cancel story.

## Deliberately out of scope

- **No MCP tools for the fleet actions.** These writes land on customers' production tenants
  behind a typed confirmation; that is not a surface to hand an agent. The environment reads stay
  web-only too.
- **No per-batch job table.** `oe_environment_upgrade_actions` plus the on-page results are the
  whole record of a sweep. Revisit only if losing a batch to a disconnect mid-run turns out to
  bite.
- **No "move the date back" cancel.** Cancel stops one of *our* pending actions before it is
  sent. Once Business Central has the new date, changing it again is another action, not an undo —
  and once an update has actually started, Microsoft owns it.
- **No change to the delivery flow.** Publishing builds to an environment is a separate tool with
  a separate schedule; the two-windows separation — our delivery slot versus Microsoft's update
  window — stands exactly as [`saas-delivery.md`](./saas-delivery.md) sets it out, and the update
  window this tool can override is Microsoft's.
- **Claims and the cookie pipeline are untouched**, by the grant's design.
