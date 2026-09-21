# Environment updates — the Upgrades fleet page

> **Status: shipped** ([#657](https://github.com/mtaanquist/ALDevToolbox/issues/657), stages 1–4b).
> The page is `Components/Pages/Upgrades/UpgradesPage.razor`; the services are
> `UpgradeFleetService` (read), `UpgradeActionService` (request/cancel/history),
> `UpgradeActionWorker` (booked slots) and the two write methods on
> `ProjectConnectionService`, all under `Services/ObjectExplorer/Bc/`. The mirror lives in
> `bc_next_update_*` columns on `OeProjectEnvironment`; the actions and the history are one
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
opening a project; and the page's own **Refresh** command, which feeds
the same queue so a sweep and a hand-triggered refresh coalesce. Rather than telling the
reader to reload after that, the page polls itself every 20 seconds for up to three
minutes, on the renderer's synchronisation context so a tick cannot collide with a click on
the circuit's one `AppDbContext`; a **Reload now** button in the same notice is there for
anyone who doesn't want to wait. The sweep takes a
non-user-gated refresh path (the `AcquireDeliveryContextAsync` precedent) and never stamps
`bc_connection_verified_at` — a refresh nobody asked for must not present itself as the
consultant's own connection test.

**The sweep is a guest on somebody else's API, and behaves like one.** Microsoft documents
no limits for the admin center API, so there is nothing to pace against in advance; what we
can do is be unhurried and do as we are told.

- *One request at a time.* One worker drains the queue, and a solution's calls - a token,
  the environment list, then the update window, the next update and the installed apps for
  each environment, and the tenant's storage (four plus three per environment) - go out one
  after another. A customer
  is its own Microsoft tenant, so each sees a handful of requests a night.
- *A breath between customers.* The worker waits a second before the next solution. Nobody
  is waiting on the sweep; a hundred customers cost under two minutes.
- *Not on the hour.* The sweep starts a random few minutes into its hour (up to forty,
  drawn once per process), because everything else in the world fires at 03:00 sharp.
- *Told to slow down, it slows down.* `BcThrottleHandler`, on the shared Business Central
  HTTP client, retries a **read** answered with 429 (or a 503 that names a wait) once,
  after the `Retry-After` it was given, capped at a minute. A write is never re-sent on
  our own initiative. Still throttled after that is an ordinary failed read: the customer
  keeps last night's mirror.
- *Parallelism was considered and left out.* Several customers at once would be safe for
  the same per-tenant reason and would shorten the run, but it means a degree-of-parallelism
  knob on `QueueDrainWorker`, which every worker inherits. The worker now logs each run -
  solutions, requests, elapsed - so that decision can be made on a measurement.

**Storage.** The same refresh reads the tenant's storage in two calls per customer
(`/environments/usedstorage` and `/environments/quotas`), not per environment: every
environment's database size, and the one allowance they share. That shape decides the
display. The Environments list shows each environment's own size, and under it a bar for
the *customer's whole tenant* against its allowance - repeated on each of the customer's
rows - because the tenant is what runs out. The bar is amber from 80% and red at or over
100%; Business Central lets a tenant go over, so red is a state rather than a ceiling, the
bar stops at full and the words carry the rest ("Customer at 115% of 80 GB - over its
allowance"). A customer at or over their allowance counts under **Needs attention**; one
that is merely filling up does not. The environment page shows the size in its meta row and
the same sentence as an alert, which also says that deleting a sandbox frees room. A size
Business Central could not work out (it reports -1) is left blank, and an allowance of zero
or none is "not read", never "full". Stored as `oe_project_environments.bc_database_kb`
and `oe_projects.bc_storage_quota_kb` / `bc_storage_fetched_at`; a failed read keeps the
last figures. That makes the sweep four requests per solution plus three per environment.

The queue holds 256 solutions and **waits** when full rather than dropping, so nothing is
lost past that size; the scheduler restarts its heartbeat's active clock on every job it
gets in, so waiting for a slot does not read as a stall on `/healthz/workers`, while a
worker that has really stopped still does.

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

- **Push the date to the latest** sets the date as late as Business Central will take it.
  The `latestSelectableDateTime` the updates read gives back is an *exclusive* bound — a
  value of midnight UTC means "before that day", which is why the admin center's own picker
  stops the day before — so a midnight bound is turned into the previous day and only a
  bound carrying a time of day is sent as it stands. The write is then verified rather than
  trusted: the toolbox recorded the move as done while the date stayed exactly where it was,
  so the re-read that re-mirrors the row is also what proves the date changed, and an
  unchanged date fails the action. The test is whether the date *moved*, not whether it
  landed on the day we asked for — Business Central stores it at the start of the customer's
  update window, which for a window opening after midnight UTC is the following day. That is
  also why the refusals compare calendar days: it refuses when there is no update on offer,
  when Business Central gave the update no latest date, and when the date already sits on or
  past that day. The same reading decides what the fleet page shows as the latest allowed,
  so the page and the admin center agree.
- **Update now** sets the date to the current moment and is the *only* operation that ever
  ignores the environment's update window — a customer who has agreed a slot is asking for
  the upgrade regardless of their window, and nothing else has the right to take that
  protection away. Refuses only when there is nothing on offer.

Each refusal is a `PlanValidationException` the fleet page shows against that one row, not
a failure of the batch.

### The wire shape

Both go through the same `PATCH .../environments/{family}/{name}/updates/{targetVersion}`
the environment panel's version pick uses, with a `scheduleDetails` object added alongside
the `selected` / `targetVersionType` it already sent. The two scheduling fields go **inside**
that object, where the updates read also returns them. Sent at the top level they are
ignored with a 200, which is how the first version of this failed to move any date:

| Field | Shape | Sent when |
|---|---|---|
| `selected` | JSON boolean, always `true` | always — a date set on an update the customer had not picked selects it in the same request |
| `targetVersionType` | string, verbatim from the updates read | when the read gave one |
| `scheduleDetails.selectedDateTime` | ISO-8601 in **UTC** (`yyyy-MM-ddTHH:mm:ssZ`) | only when the caller is moving the date; omitting it leaves the customer's existing slot alone |
| `scheduleDetails.ignoreUpdateWindow` | **a real JSON boolean** | only by "update now" |

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
customer, its state as a glyph, the environment over its type, the version it is on, the
mirrored next update (version, when, and a marker when it ignores Microsoft's window), the
latest date that update can still be pushed to, how old the mirror is, and a row menu. Above
it sits one sticky command bar: a view select (all, update waiting, and each environment type
with and without an update waiting), a search that filters as you type, and the commands. The
filters live in the address (`q`, `type`, `waiting`); loading, empty and populated states as
usual. The layout is the design's archetype 15 - see "The Upgrades page, against its designed
sheet" below.

**The join is the guard.** `OeProjectEnvironment` has no visibility rule of its own — it
inherits its project's. `UpgradeFleetService.ListFleetAsync` therefore reaches the
environments table *through* `VisibleProjectPredicate`, and any future query that lists
environments must do the same rather than reading the DbSet directly. "May act" is computed
in the same query from `UpdateOpsProjectPredicate`, so a fleet of a hundred costs one round
trip; a row the viewer may see but not act on shows a lock instead of a checkbox. The org
fence sits underneath both.

**Two actions, two voices.** Each runs over the checkbox selection - or, from a row's own
menu, over that one row, leaving the ticked rows as they were - behind a confirm that
lists every selected environment with what will happen to it and — grouped at the bottom
under its own heading — the ones that will be passed over and why.

- **Move dates** previews each date and the date it moves to. It is the page's one primary
  button: it is what the team comes here to do, a hundred at a time.
- **Start update...** is the sterner one, and its dialog is where that is said; in the bar it
  is a plain button, as the sheet has it. The sheet calls it "Update now", but the dialog also
  books an update for a later slot, and nobody wanting tonight at 20:00 presses a button called
  "now"; the dots say a dialog follows. The dialog says plainly that Microsoft will start the
  updates whatever the
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
the disclosure that opens the history, so "Update history" in the row menu is a second door
and never the only one. Confirming an update-now over an environment that already has a booking waiting groups it
under "Already booked" in the preview, with what it is booked for: the run still acts on it, and
adding a second booking is a thing to notice before the click.

**Audit.** Each of the two writes records an audit row, and this is the one place in the
application that writes to `audit_log` outside `AuditInterceptor`. It has to be: the writes land
on the customer's tenant and touch no row of ours that the interceptor watches — and the
re-mirror afterwards is deliberately outside `AuditInterceptor.EnvironmentSettingColumns`,
because the nightly sweep writes those same columns and would otherwise fill the log with rows
nobody made. The entry is an `OeProjectEnvironment` row keyed by the environment id, and its
snapshot keeps the log's "state before the change" contract — the update as we read it, plus a
plain-words `Action` naming which of the two writes it was, since the audit model records rows
changing and these are events. The actor is resolved from the database rather than from claims,
because a Blazor circuit has no `HttpContext` for the interceptor's own lookup to read. A refused
row writes nothing: nothing changed. For a booked action the audit row is written at send time,
by the worker, so the log records what actually reached Microsoft while the activity feed records
the whole request-and-cancel story.

## Deleted environments, and bringing one back

A customer who deletes a Business Central environment does not lose it at once. Microsoft
soft-deletes it: the environment still answers from the admin center API, carrying the day
it was deleted, the day it stops being recoverable, and the customer's stated reason, and
until that second day it can be brought back with everything it held. Business Central also
renames it on the way out, which is what the fold in `UpsertEnvironmentsAsync` is for — see
"soft_deleted_on and missing_since" in [`saas-delivery.md`](./saas-delivery.md).

**It is not one more row with a red state.** Nothing can be published to it, updated on it
or rescheduled for it, so listing it beside the live environments makes a fleet look both
bigger and sicker than it is; and "needs attention" is a list of things somebody has to go
and do, which a deletion somebody already decided on is not. So:

- The **Environments list** hides deleted environments from All, Update scheduled and Needs
  attention, and gives them a **Deleted** view of their own with its count. That view is
  offered only when there is something in it — a view that is always empty is one people
  learn to ignore, which is exactly the view that has to be noticed on the fortnight it
  isn't. In it, the Next update column becomes **Gone for good** and carries the deadline in
  the line the version would have had ("Gone for good on 4 Oct 2026", or plainly that
  Business Central hasn't given a date), with how long is left under it ("3 days left to
  bring it back") — a date alone makes somebody scanning a hundred rows do the arithmetic per
  row to find the customer who needs a call today, which is the question the view exists to
  answer. Recover leads the row menu, in place of Upload an app, and a line above the table
  says these can be brought back: the pill's tooltip says the same, but a tooltip does not
  exist on touch and never appears on a keyboard, so it can never be the only place the
  meaning lives.
- The **Upgrades page** doesn't list them at all. They are dropped in
  `UpgradeFleetService.ListFleetAsync`, not in the page, so the counts, the checkbox
  selection and both bulk actions agree without each having to remember. The Environments
  list asks for them back with `includeSoftDeleted`.
- The **environment's own page** keeps working — the links from the Deleted view have to
  land somewhere — and leads with a danger alert saying when it was deleted, when it is gone
  for good *in the list's exact words* (the two are read in the same minute, and on a
  fortnight's window a day either way is a customer's data), how long is left, and that
  Business Central refuses an install, an update or a settings change until it is back.
  Someone who may act gets Recover; someone who may not is told who to ask, as every other
  locked part of that page does. The alert is first because it changes what everything under
  it means: the version, the two windows and the app lists are all the state the environment
  was in on the day it was deleted.
- The **Modules card** on a solution's Customer tab reads the installed apps from the
  customer's production environment, and never picks a deleted one — its app list is what
  was installed the day it was deleted, which is not what the customer runs now. The
  Solutions list's module filter reads the same environments, so the two cannot disagree
  about who has a module.

**The mirror.** `soft_deleted_on`, `hard_delete_pending_on` and `delete_reason` sit on
`oe_project_environments` beside the rest of the fetched detail, written by the same refresh
and **cleared by it** when the environment is live again: "no longer deleted" is a fact the
mirror has to be able to state, or a recovered environment would sit in the Deleted view for
ever. Microsoft returns those three PascalCase beside camelCase neighbours, so the admin
client reads every property case-insensitively — a case-sensitive lookup read each deleted
environment as one with no dates at all, silently. `BcEnvironmentStatus.IsSoftDeleted` is the
one place the status is compared, and `EnvironmentQueries.NotSoftDeleted` the one place the
same question is asked in SQL.

**Recover** is `POST .../environments/{family}/{name}/recover` with no body. It is a write to
the customer's tenant and carries the four things every such write does: it is gated on
managing the solution (`ResolveEnvironmentAsync`), it sits behind a confirm that names the
environment and its customer and says out loud when it is a production one, it records
"Recovered the environment" in that environment's Toolbox history, and a `BcApiException`
reaches the page as a sentence — the two codes Microsoft documents here, an environment
already being recovered and one whose state forbids it, are told apart rather than both
arriving as "the API refused it". An environment that was never deleted is refused before
anything is sent.

One verb, everywhere: the menu item, the dialog's title and its button all say **recover**,
which is what the admin centre calls it. Never *restore* — in Business Central that is the
point-in-time restore of a live environment, and somebody would reasonably ask which point
in time.

Business Central *schedules* the recovery rather than doing it there and then, so the write
is followed by a re-read of the customer's environments: the row moves to `Recovering` and
then out of the Deleted view on its own. A failed re-read costs the freshness, never the
write.

**Deliberately not built.** Deleting an environment, renaming one and restoring one to a
point in time all stay in the admin centre. Recover is here because it is the one of them
with a deadline — a fortnight, after which nobody can do it at all — and because the toolbox
is where a deleted environment is noticed. Copying one is here for the opposite reason: it
has no deadline and is simply the thing an ops engineer does most often, which is the next
section.

## Copying an environment

The most common errand this page's reader would otherwise open the admin centre for: make a
copy of an environment, almost always a customer's production into a fresh sandbox, to try
an update or reproduce a problem on their real data. Named user: the same consultant or ops
engineer who manages the customer's solution. It is one `POST
.../environments/{family}/{source}/copy` carrying the new environment's name and its type,
and it is the one write here that *adds* something to the customer's tenant — it counts
against their storage allowance, and a production copy against their licences.

It carries the four things every tenant write does: it is gated on managing the solution
(`ResolveEnvironmentAsync`), it sits behind a confirm that names the environment and its
customer, it records "Copied the environment" in the **source** environment's Toolbox
history — the one that existed when it was asked for, and the one somebody later asks where
the sandbox came from — and a `BcApiException` reaches the page as a sentence. Every code
Microsoft documents for this endpoint is told apart, because they are different situations
with different next steps: a name already taken, a name against the rules, a tenant out of
environments, out of storage, or already making one, a source that has gone, and a source
whose uploaded extensions clash with developer extensions in the copy.

**Two things are refused before anything is sent.** A source the customer has deleted, which
has nothing to copy until it is back; and a source Business Central is not reporting as
ready, because an environment part-way through an update is a moving target and a copy of
one is a copy of a moment nobody can name. That second reading is `BcEnvironmentStatus`'s,
the same one the delivery gate makes — ready, or a status we have no opinion about.

**The name rules are Microsoft's and live in one place.** `BcEnvironmentName` holds them:
start with a letter, then letters, digits, dashes and underscores, fewer than thirty
characters. The service is the source of truth and the dialog mirrors them in `pattern` and
`maxlength`, so the browser answers first and the rule is on screen as the person types —
Business Central's own refusal arrives minutes later as `environmentNameNotValid`, which is
too late to be help. A name the solution already has is refused from our own mirror,
case-insensitively, before a round trip.

**The dialog says what the copy is, not how the copy works.** It defaults the name to
`<source>-Copy` and the type to Sandbox, which is the rarer-is-dearer way round: a
production copy turns the confirm red and says it costs the customer a licence and one of
the production environments they are allowed. Copying production into a sandbox says plainly
that the sandbox will hold the customer's real data, because a sandbox is not a blank
environment and everyone let into it can read all of it. A customer at or over their storage
allowance is **warned and not blocked** — Microsoft decides whether there is room, and by
the time somebody reads the warning capacity may have been added.

**Nothing waits for it.** Business Central schedules the copy and takes its time: the new
environment appears in the environments list as `Preparing` and turns `Active` when it is
ready, which can be an hour later. The write is followed by the same re-read Recover does,
so the new environment shows up as soon as Microsoft lists it — and **its absence from that
read is not an error**, which is the whole reason the dialog and the success notice both say
where to go and watch instead: the source environment's Operations tab, which is Business
Central's own record of what it is doing. No polling beyond that, and no job row of ours.

**Copy is the one row-menu entry offered only to a manager.** Its neighbours on the
Environments list are one click and a refusal; this one asks for a name and two decisions
first, and taking all of that back with "you may not" is a worse answer than never having
asked. `UpgradeFleetRow.CanManage` carries that answer, computed as a subquery over
`ProjectAccess.ManageProjectPredicate` in the same round trip as `CanAct` — never a
substitute for the service-side check, which is made on every write regardless.

## Sessions

The classic support call: "posting has been running for an hour and everything is locked."
Until now the fix meant opening the customer's admin centre, finding the environment,
opening its Sessions page and cancelling the one that is stuck. Named user: a support
consultant or an ops engineer who manages the customer's solution, on the phone to the
customer while they do it. The **Sessions** tab on the environment's own page is that
errand, and ending a session is the write behind it.

It is one read and one write on Microsoft's documented session endpoints:
`GET .../environments/{family}/{name}/sessions` and `DELETE .../sessions/{sessionId}`.
Session ids are integers. The read is gated on managing the solution, like every other read
that spends the customer's credentials; the write carries the four things every tenant write
carries — the same gate, a confirm that names the environment and says when it is a
production one, a line in the environment's Toolbox history (`UpgradeActionKind.CancelSession`,
a text column, no migration), and a `BcApiException` that reaches the page as a sentence.
Microsoft documents no error codes of its own for the DELETE, so the one worth telling apart
is the status: a **404 is a session that ended between the list and the click**, which is the
likeliest failure of all.

**Nothing about a session is stored.** A user id and what that person is doing in their
employer's system is personal data with no reason to outlive the screen it is on, so it is
read live, shown, and forgotten — no cache, no mirror column, no table. Leaving the tab
drops the list. The one thing that lasts is the history line, and it keeps to the same
rule: *"Ended session 47 on Production."* - the session's number and where, with who asked
for it beside it as on every history line, and nothing about whose session it was or what
it was running (maintainer's decision, 2026-09-21; the first version named both). The
service still re-reads the live list before it deletes, because that turns "already gone"
into a sentence rather than a wire 404.

**This tab is live where Operations is not.** Both are read live rather than cached, but an
operations list that is two minutes old is still *true* — the entries in it happened. A
sessions list that is two minutes old is *wrong*: the person it names may have signed out,
and the one holding the lock may have signed in since. So while the tab is open it keeps
itself current, and the page says so rather than leaving rows to move unexplained.

- **It reads the moment it is opened**, by a click or by landing on `/environments/{id}/sessions`
  directly. There is no first-run state with a button on it: a Sessions tab waiting to be
  told to read is a tab showing a wrong answer. Coming back to it later in the same visit
  re-reads rather than restoring what was there.
- **Every 30 seconds, for 10 minutes.** Thirty seconds is short enough that the list matches
  what the customer is describing and long enough that nobody watches rows flicker. Ten
  minutes is the length of the phone call it was built for; past that, a browser tab
  somebody forgot must not read a customer's tenant all night. One request every thirty
  seconds against one tenant is a fine guest (see "The sweep is a guest on somebody else's
  API"); an unbounded one is not. When it stops it says so in the card, and **Refresh**
  starts it again.
- **The card has its own Refresh**, beside a line saying how old the list is
  (`RelativeTime`), because the freshness of *this* list is part of the answer. The page's
  contextual Refresh in the freshness strip works on this tab too; the card's is the obvious
  one.
- **A tick never overlaps anything.** It is skipped while a read or a write is in flight and
  while the confirm dialog is open — the row somebody is about to end must not move or vanish
  between reading it and pressing the button. It runs through `InvokeAsync`, on the
  renderer's synchronisation context, so a tick cannot collide with a click on the circuit's
  one `AppDbContext`; that is the mechanism the Upgrades page already polls with and
  deliberately not a second one. It stops on leaving the tab and on dispose.
- **A failed automatic re-read keeps the list**, with a quiet line saying it could not be
  updated. Replacing a list somebody is reading out to a customer with an error card is the
  worse answer. A failed *first* read is still the unreadable state.

**"Long-running" is ours, and it is a cue rather than a verdict.** Business Central marks
nothing, so the row the caller is looking for has to be made findable here: the list is
ordered by `currentOperationDuration` descending (then by who has been signed in longest),
and a session that has been in the same operation for **five minutes** is marked. A person
clicking through the web client finishes an operation in well under a second, so a minute
already means work rather than somebody thinking; five is where a consultant would start
looking, and it is a round number to hold in the head. It marks a row and orders the list —
it never hides one and never decides anything, because a job queue task legitimately runs
for hours. The rule is written under the table so nobody has to guess what the colour means.

**One verb, everywhere.** The row button, the question and the answer all say *end*, never
*cancel*: a row labelled "Cancel session" whose dialog then says "End the session" makes
somebody stop and wonder whether they are the same act, and the dialog cannot say "cancel"
because its other button already does. Business Central's own admin centre says cancel; the
confirm's sentence carries the meaning either way.

**Two things about the payload are worth knowing.** Microsoft types `currentOperationDuration`
as a `long` and names no unit, so the parser reads a number as milliseconds and a string as a
time span, and that is the one field here not checked against a live tenant. And the API
marks nothing as belonging to an app registration or to the system, so **no row is hidden
from Cancel** — guessing which sessions are "ours" would be inventing a rule Microsoft has
not written. Our own admin-centre calls are not Business Central sessions and never appear.
Client types are worded in `BcSessionDisplay`, which gives every value Microsoft has today a
phrase a consultant would say out loud ("Web client", "Web service (OData)", "Job queue") and
spaces out one they add tomorrow rather than showing the wire token — the treatment
`BcEnvironmentOperationDisplay` gives an operation.

**Deliberately not built.** No history of who was signed in (that is the personal data the
tab exists not to keep), no ending several sessions at once, and no telemetry.

## The Environments list, against its designed sheet

`/environments` is the read-only view of the same fleet rows, designed as archetype 2a in
`.design/handoff/PageEnvironmentsList.dc.html`. It follows the sheet: a glyph-only state cell
with the word on `aria-label` / `title`, no status column, "Now on", a semibold next version
over its date, skeleton rows under the real header while loading, and the count in `.pager`.
With nothing scheduled, the line under Next update names any state that is not plainly
running, as the sheet does - that keeps the word on screen, since four states share two
glyphs and a title does not exist on touch.

Where it still differs, and why:

| The sheet has | We have | Why |
| --- | --- | --- |
| "Export the list" and a primary "Refresh from Business Central" in the page head | Refresh in the freshness strip only | There is no export. Refresh sits beside the age it fixes, and a second copy in the head would be the same button twice. Recorded upstream in the design project's `briefs/2026-09-port-corrections.md`, with the freshness copy, the "Solution" column name and the unread-row glyph. |
| A row menu: Open environment, Open in Business Central, Refresh this environment | The first two, plus Upload an app and Copy, or - on a deleted environment - Recover | A refresh is per solution, not per environment, and the freshness strip already does it. The other three are writes the sheet does not draw; see "Deleted environments" and "Copying an environment". Copy is the only one shown to managers alone, for the reason given there. **Needs a design pass upstream.** |
| Three views | A fourth, **Deleted**, when there is one | The sheet has no notion of an environment that is deleted but recoverable. See "Deleted environments" above. |
| Sortable Customer and Next update headers | Fixed order | Not built. Follow-up. |
| Previous / Next | Count only | The whole set is rendered; buttons that can never be enabled are noise. |

## The Upgrades page, against its designed sheet

`/upgrades` is archetype 15, the actionable list, in `.design/handoff/PageUpgrades.dc.html`. It
follows the sheet: crumbs, the time-zone rule as a clause of the subtitle, one `.cmdbar` whose
selection commands are plainly disabled until rows are ticked (no counter and no instruction,
as in Business Central's own lists), `.check` boxes in a `data-table__col-check` column with
`is-indeterminate` on the header and `is-selected` on the row, the same glyph-only state cell
and state wording as the Environments list (`FleetRowState` serves both), `.cell-stack` cells,
"Now on" and "Latest possible date", bare dates, and the row's commands in one `.ra` menu with
Update history first.

Where it still differs, and why:

| The sheet has | We have | Why |
| --- | --- | --- |
| "Customer" | "Solution" | The house name for the record; see CLAUDE.md. |
| "Update now" | "Start update..." (and "Start this update..." in the row menu) | The dialog behind it also books a later slot, which "now" hides. Maintainer's decision, 2026-09-19; to be recorded upstream in `briefs/2026-09-port-corrections.md`. |
| A fixed view list: Production / Sandbox, each with "update waiting" | The same list built from the environment types actually present | Business Central reports the type as text, and a fleet with no sandboxes should not offer one. |
| An overflow menu: delivery window, two exports, fleet-wide history, cancel the scheduled update | No overflow menu | None of the five exists yet. A booking is cancelled from its own marker or from the history. An empty kebab is worse than none; add it with the first entry. |
| Every row has a checkbox | A padlock instead, on rows of a team the viewer is not on, with a legend under the table | The sheet has no notion of a row you may see but not change. Their row menu holds history only. |
| Nothing under the next update's date | The out-of-window warning, a booking marker with its Cancel, and the live per-row result of a run | Behaviour the sheet does not draw. They sit under the date because a booked slot and Business Central's date are two answers to one question. |
| Sortable Customer and Next update headers; Previous / Next | Fixed order; count only | As on the Environments list. |
| The table directly in the page | The table in a box that scrolls sideways, with the checkbox, state and Solution columns pinned | The page container clips rather than scrolls (#574), and nine columns do not fit a narrow window. |

Row menus anywhere in the app now open upwards when there is no room under them
(`row-actions-menu.js` sets the sheet's `.ra--up`), which this table needed for its last rows.

## The environment's own page, against its designed sheet

`/environments/{id}` is `.design/handoff/PageEnvironmentDetail.dc.html` on the `DetailPage`
frame (#809). Named user: an ops engineer with a fleet of customer environments, who would
otherwise open each customer's admin centre. It follows the sheet top to bottom: crumbs
through the solution, a `detail-head` with the state pill and the type as a `.tag`, the
freshness strip, the six-item `.meta-row`, the Updates card's `.kv-grid` with the info alert
when the two windows overlap, then Apps (Scheduled installs, Installed apps, AppSource
updates waiting - ready ones first, "Waits for N" with the prerequisites as `.tag`s) and
Environment settings as a `.setting-list` ending in the `setting--danger` row.

**Two readings share the page.** The head, the meta row and the Updates card come from our
own mirror, reached through `UpgradeFleetService.GetEnvironmentAsync` - the same
visible-projects join as the fleet, so an id from a solution the viewer cannot see answers
exactly like an id that does not exist. Apps and Environment settings are the existing
cached panel read (`ProjectConnectionService.GetEnvironmentPanelAsync`; no second fetch
path) and keep the gate they had on the solution's Business Central tab: people who manage
the solution - its owner, an org admin, or anyone on a team assigned to it. That already
covers the ops team: the environment-updates grant is only ever held through an assigned
team, so whoever holds it manages the solution too. Everyone else gets the mirror and a
quiet card saying who can open the rest.
History (its own tab) sits outside both, because it is our record and must survive a tenant
that will not answer.

The inline "Environment details" panel on the solution's Business Central tab is retired;
its button goes here. The tab keeps the connection and the delivery window, and
`/solutions/{id}?tab=bc` opens on it so this page can send people there.

Where it differs from the sheet, and why:

| The sheet has | We have | Why |
| --- | --- | --- |
| "Nothing on this page is stored by the toolbox." | "Apps and settings read from Business Central {age}. Version and update dates last checked {age}." | The sheet's sentence is not true for us: the environment row and its next update are mirrored, and the app lists are cached for fifteen minutes. The strip says which half is which. |
| An overflow menu: Copy environment ID, Open the admin centre, Export the app list, Remove from this solution | "Open the admin centre" as a second button; no menu | We mirror no Business Central environment id, there is no export, and environments are mirrored from Business Central rather than attached by hand, so nothing can be removed. One entry is not a menu. |
| Refresh for everyone | Refresh for people who manage the solution | It reads the customer's tenant with their credentials. |
| A region in the subtitle and a country in the meta row | Both, when Business Central reported them | `location_name` and `country_code` are mirrored; an environment read before they were captured shows a dash. |
| Overlap alert naming the overlapping hours | The alert without the hours, linking to where the delivery window is set | The two windows can be in different time zones and the overlap moves with daylight saving; `BcUpdateWindow.Overlaps` answers yes or no. |
| "Reschedule the update" as a button in the Updates card | "Change the next version", with a down arrow, linking to the "Next Business Central update" setting further down the Overview tab | One control writes the version, and it carries the warning and the lock line; a second one in the card would skip both. The label says it goes down the page, because a button that scrolls reads as a button that did nothing. |
| "Blocked" in the filter, "Waits for N" on the pill | "Waiting" and "Waits for N" | One word for one state on one card. |
| "Scheduled for" in UTC | The customer's local time first, UTC second | The two windows beside it are local, and whether they collide is what the card is for. |
| An empty Scheduled installs card that only warns about Extension Management | It says what puts a row there and links to Releases; the warning moves under the populated table | The first-run state has to name the next step. |
| Next-update options as version and date pairs | Versions, with the one already queued marked | Business Central offers versions; the date comes from the customer's update window once a version is chosen. Moving the date is what Upgrades is for. |
| A sortable App header on the waiting updates | Fixed order, ready first | The order is the point of the table. |
| Cadence saves from the select; Microsoft 365 is a switch | The same, each behind a confirm | They write to the customer's tenant. Declining puts the control back. |
| Nothing after Environment settings | Update history | Who moved this environment's dates and what is still booked; the same feed the Upgrades page shows. |
| A read-only list of waiting AppSource updates | An **Update** button on the rows that are ready | What #809's report asked for, and the maintainer's decision on #841 to build it without a sheet. Ready rows only: a waiting row names its prerequisites instead, which is the next step. The confirm names the app, both versions, the environment and whether it is production, and asks when - the environment's next update window by default, or now. `ProjectConnectionService.UpdateAppAsync` re-reads the waiting list before it writes and refuses an app that is not on it, a version Business Central is not offering, or an app that still waits for another; dependencies are never pulled along. Manage-gated; logged, not audited, as it touches no row of ours. **Not yet tried against a live tenant** - the request shape is from Microsoft's documentation of `POST .../apps/{appId}/update`. Needs a design pass upstream. |
| The result of a write beside its control | One result line under the head | The writes are spread down a long page and each re-reads everything; the top is where the eye is afterwards. |
| Nothing about copying the environment | **Copy this environment...** as a third outline button in the head, and **Copy...** in the Environments list's row menu; both open the same dialog | The sheet draws a page that only reads and adjusts. Copying is the errand this page's reader would otherwise open the admin centre for, and it is the one write here that adds an environment to the customer's tenant; see "Copying an environment" above. Not shown on a deleted environment, which has nothing to copy. **Not yet tried against a live tenant** - the request shape is from Microsoft's documentation of `POST .../copy`. **There is no sheet for this; it needs a design pass upstream.** |
| Nothing about a deleted environment | A danger alert above the meta row, with **Recover this environment** | The sheet draws a live environment. A deleted one changes what every number under it means, and it has a deadline; see "Deleted environments" above. |
| One long page: Updates, Apps, Environment settings | Five tabs under the meta row - **Overview** (the Updates card, both windows, the three settings), **Apps** (scheduled installs, installed apps, AppSource updates waiting, Upload an app), **Operations**, **Sessions**, **Toolbox history** | The page had grown past what the sheet drew (uploads, app updates, the delivery window, history) and the thing looked for was a long scroll away. The head, the freshness strip, the result line and the meta row stay above the tabs because they are true on every one. The tabs are real links (`/environments/{id}/apps`), so one can be bookmarked and Back works; the page reads the environment once per id, and a change of tab reads only what that tab shows - Overview and Apps share the one cached panel, Operations has its own read, History asks Business Central nothing. Refresh re-reads the open tab - and Sessions, the one live tab, keeps itself current besides; see "Sessions" above. Maintainer's decision, 2026-09-20; needs a design pass upstream. |
| No operations list | **Operations**: Business Central's own record for the environment (`GET .../environments/{name}/operations`) - app installs, updates and uninstalls, platform updates, restarts, renames, setting changes - newest first, each as a sentence with a status, who started it, when (the solution's time zone) and how long it took; a failure carries Business Central's message | Toolbox history is what *we* did from here (named so at the tab strip, where the choice between the two is made); an update started in the admin centre, or one Microsoft ran overnight, is only in Business Central's record. Two tabs rather than one merged timeline until there is real data to judge a merge by. Read live on every visit, never cached: the point is to watch something finish. Manage-gated like the panel, and read-only. Operation types and statuses are worded in `BcEnvironmentOperationDisplay`; one Microsoft adds later is spaced out into words rather than shown as the wire token. **Not yet tried against a live tenant** - the shape is from Microsoft's documentation. |
| No notion of who is signed in | **Sessions**: who has a session open on the environment right now (`GET .../environments/{name}/sessions`) - who, how they got in, since when, what they are running and for how long - with **End session** per row behind a confirm | The errand this page's reader would otherwise open the admin centre for while a customer is on the phone with everything locked. The one live tab: it reads on arrival, re-reads every 30 seconds for 10 minutes, and says so. Nothing about a session is stored. Manage-gated; the history line names whose session it was and what it was running. **Not yet tried against a live tenant** - the shape is from Microsoft's documentation of the session endpoints, and `currentOperationDuration` is documented as a bare `long` with no unit. See "Sessions" above. **There is no sheet for this; it needs a design pass upstream.** |
| Scheduled installs drawn only as an empty state | A table with a Cancel install action when there are any | The write exists and a booked install has to be reachable from somewhere. |

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
