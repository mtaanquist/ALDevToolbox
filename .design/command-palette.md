# The command palette

Status: **the shell (#880), the search backbone (#881), the first sources (#882-#884) and
the way in, the accessibility pass and the docs (#888) are built; recents and page context
(#887) and the performance work (#889) are next.** This document is the outcome of #879.
Every decision below was made with the maintainer on 2026-09-21.

## Why

The app has grown a navigation tree that serves someone who is looking around and gets in
the way of someone who already knows where they are going. The palette is for the second
person: press one key on any page, type part of a name, press Enter.

### Named users

- **A consultant jumping to a customer's environment.** Knows the customer and the
  environment ("Contoso, production"). Today: Solutions, find the row, open it, the
  Business Central tab, the environment. With the palette: `con prod`, Enter.
- **A support person looking a customer up mid-call.** Knows the name, or the short name
  the team uses for them. Wants the Solution's Customer tab now.
- **A developer hopping between tools** - from the Translator to a release in Object
  Explorer, or to a cookbook recipe they half remember the title of.

None of them wants to learn anything. The palette has one input and a list; nothing in it
needs a caption.

## How it is global

`MainLayout` is a static server-rendered frame, and pages opt into a circuit one by one.
Several have none at all - `/solutions` and the docs pages are plain GET pages by design.
So "works on any page" rules out the obvious Blazor answer: an interactive island in the
layout would make every static page hold a circuit just so the palette can open, would
cost a round trip to open, and would be dead while a circuit reconnects.

Instead the palette is three parts:

1. **Razor renders the markup.** A static, non-interactive component in `MainLayout`
   renders the palette's skeleton, hidden: the dialog, the input, the list, the empty and
   error states, and one `<template>` per kind of result row. Icons come from
   `Icon.razor` like everywhere else, and the "Go to" list of tools is rendered
   server-side from the same role and feature gates the nav menu uses, so the palette
   never offers a page the nav would not.
2. **One script in the shell.** A single dependency-free file beside `shell-drawer.js`.
   It owns the hotkey, open and close, focus, keyboard navigation, the fetch, and cloning
   templates into the list. It writes no HTML and chooses no class names - it fills
   `textContent` and `href` on clones of what Razor rendered. That keeps the design
   system in one place and takes markup injection off the table.
3. **One search endpoint.** `GET /palette/search?q=`, a cookie-authenticated minimal API
   returning data, not HTML. A GET with no side effects, so no antiforgery token; it
   requires an authenticated user and returns 401 otherwise.

Palette styles live in a global sheet, never a scoped `.razor.css`: Blazor's scope
attribute is not on script-cloned nodes, so scoped rules would silently not apply. The
ported sheets stay byte-locked; if the palette needs a component the design system does
not have, it goes through the design project like any other.

### The exception this makes

`CLAUDE.md` says "tiny `.razor.js` companion files only". The palette script is not tiny
and is not a companion. It is a deliberate, recorded exception, for the reason above: a
static layout cannot host an interactive component without taxing every page. It is
**not** a precedent for moving page behaviour into script. The limits that keep it an
exception: one file, no dependencies, no bundler, no build step, no HTML strings.

### Typed JavaScript, not TypeScript

The script is a plain `.js` file with `// @ts-check` at the top and JSDoc `@typedef`s for
the endpoint's response shape. That gets editor-time type checking without a compile
step, without Node in the Dockerfile, and without a second source of truth for what runs
in the browser. CI may run `tsc --noEmit --checkJs` over it as a check; nothing is ever
emitted.

## The hotkey

**Cmd+K on macOS, Ctrl+K everywhere else.** Nothing in the app binds it today:

| Where | Bound today |
| --- | --- |
| Translator | Ctrl/Cmd+S, Alt+letter, arrows on the splitter |
| Piper | Alt+1, Alt+2 |
| Object Explorer release page | Esc, F3, Alt+letter |
| Source viewer | Ctrl/Cmd+F, Ctrl/Cmd+A, Ctrl/Cmd+Up/Down, Shift+F12, Ctrl/Cmd+click |
| Code editor | Ctrl/Cmd+click, CodeMirror's default keymap |

- It works while focus is in an input, a CodeMirror editor or the source viewer. The
  listener is on `document` in the capture phase, so an editor's own keymap never sees
  the keystroke.
- CodeMirror's default keymap binds Ctrl+K on macOS to delete-to-end-of-line. Because
  macOS gets Cmd only, that binding survives. The maintainer's position, should the two
  ever collide: the palette wins.
- Firefox on Windows and Linux uses Ctrl+K for its search bar. The page takes it, as
  GitHub and Slack do.
- It does **not** fire while one of our own dialogs is open. A `ConfirmDialog` naming a
  production environment is not something to be navigated away from by reflex.
- Pressing it while the palette is open closes it. Esc closes it. Focus returns to
  whatever had it.

## The other way in

Nobody finds a hotkey by accident, and on a phone or a tablet there is no hotkey to
find - so the palette also has a visible control in the top bar, and that control is
the only door at phone width.

It is drawn as the search field it stands in for, sitting left in `.app__top`: a
magnifier, "Search or jump to...", and the shortcut as key caps. Clicking it runs the
same `open()` the hotkey does, so the two cannot drift. It narrows with the shell -
the key caps go at rail width, where a tablet has no keyboard to hint at, and below the
drawer breakpoint it is the magnifier alone. The label is *clipped* there rather than
removed, so the icon-only button still announces as "Search or jump to..., Ctrl K".

`shell.css` is byte-locked and has no slot for it, so its styles live in
`MainLayout.razor.css`; scoped is safe here because this is the one part of the palette
Razor renders in place rather than the script cloning. The pattern goes upstream -
`.design/handoff/briefs/2026-09-command-palette.md` is the brief.

Razor renders "Ctrl", because the server cannot know what the person is typing on. The
script rewrites it to "Cmd" on a Mac, from the same `isMac()` that decides which
keystroke opens the palette - one decision, not two that could disagree.

## Sources

Each source of results is one class implementing one contract, registered in DI. The
palette knows none of them by name; adding a source is one class and one registration.
This clears the "no interface until the second implementation" bar on day one - the
first version ships five.

A source provides:

- **An id and a label.** The label is its group heading: Solutions, Environments,
  Releases, Recipes, Go to.
- **A gate.** The role check and feature gate its matching page already has. A caller who
  fails it never has that source asked.
- **A search.** Query terms and a limit in; candidate rows out - kind, title, subtitle,
  link, and the short name when the row has one. Those three text fields *are* what was
  matched against. No icon name: the icon is in the `<template>` the kind selects, so it
  never rides the JSON.
- **Searched-only text**, when a row needs it. A fourth field that is matched against and
  never shown, for the things support types mid-call that a result must not print back: a
  customer's Voice account number, their tenant id. A row found that way says in its
  subtitle *which* field matched, never what was in it, and the field itself is absent
  from the wire contract, so it cannot reach the browser even by accident. It is not a
  place for personal data - a contact's name or company may be matched on and named, a
  phone number or an email address is neither searched nor shown.

Sources return candidates, **not scores**. One shared function ranks everything, so
ranking cannot drift from source to source.

| Source | Searches | Lands on |
| --- | --- | --- |
| Solutions | name, short name; and the customer fields support types mid-call - a contact's name or company, the Voice account number, the tenant id | the Solution (its default tab, Customer) |
| Environments | environment name; the Solution's name as subtitle and its short name as searched-only text | the environment page |
| Releases | release label, BC version, country, and the name of the Solution whose build produced it | the release in Object Explorer |
| Recipes | recipe title and tags | the recipe |
| Go to | tool and page names | the page |

**Go to is the one that is not a DI source.** It has no database behind it and its
whole content is decided by the caller's roles and tool toggles, so Razor renders it
into the page and the script filters it in the browser, under the same every-term-must-match
rule. That is also what makes it survive a failed request: when the endpoint is
unreachable the palette says so and still jumps. The list itself is
`Domain/Navigation/NavDestinations.cs`, shared with the sidebar - `NavMenu.razor` keeps
its own markup (its groups collapse, nest and carry active states) and a test fails if
the two ever disagree about which pages exist. For the same reason Go to is **not capped**:
it is the browsable list of tools, which the empty query already shows whole.

Go to also breaks ties by **sidebar order, not by name**, which is the one place it
departs from the ranking below. Alphabetically, `trans` puts "Translation memory" above
"Translator" - so the three letters a consultant types for the tool in their sidebar land
them on the admin page that curates it. The sidebar's order is editorial (tools first,
then the pages that administer them) and it gets that pair, and the four like it
(Templates, Cookbook, Object Explorer, Audit log), right by construction.

Releases and Recipes search **names, titles and a recipe's tags only**, never objects,
source or recipe bodies - those tables are large and Object Explorer already has a search
built for them. A later version may add a "Search for '...' in Object Explorer" row that
carries the query over; the palette itself never runs that search.

A recipe's summary is left out on purpose, even though the Cookbook's own search reads it:
a palette row is a title and one line, so a row that matched on a sentence the reader
cannot see reads as a bug rather than as a result. Tags are in for the opposite reason -
the row can show the tag it matched on, and does: a recipe's subtitle is its minimum
application version and one tag, and that tag is the one a typed term matched when a term
matched one, otherwise the recipe's first. The Cookbook page's own search is unchanged and
still finds more than the palette does; the palette is for going somewhere, and the
Cookbook is where a wider search belongs.

## The fence

The palette is a new read path across most of the app, so it is a new way to leak.

- **If you cannot open it, it is not returned.** Every source goes through the same
  visibility rules the pages use - the `ProjectAccess` predicates, the role gates, the
  organisation query filter. There is no `IgnoreQueryFilters()` anywhere in the palette.
- **A Private Solution the caller is not on does not appear at all**, and neither do its
  environments. The Solutions list shows such a Solution as a locked name, because a
  list is for seeing what exists; the palette is for going somewhere, and a row that
  cannot be opened is a dead end taking one of five places. The cost is accepted: someone
  searching for a customer they cannot see gets "no results", and the Solutions list is
  where they find out it exists and who to ask.
- Soft-deleted environments and deleted Solutions are not returned.
- **Tests prove it per source**: a user outside a Private Solution searches its exact
  name and gets nothing, for every source that touches Solutions.

## What it never does

**No palette command writes to a customer's tenant.** Every such write in the app sits
behind a confirm that names the environment and says when it is production; a palette
command is exactly how the wrong environment gets updated. A command like "Copy
environment" - if one is ever added - navigates to the page that owns the action and its
confirm. The first version has no commands at all, only places to go; #886 adds
commands under this rule.

## Matching and ranking

The query is split on whitespace into terms. **Every term must match** somewhere in the
row's searched fields, in any order - so `con cof` finds "Contoso Coffee", `cof con` does
too, and `con prod` finds the Production environment of Contoso because an environment's
subtitle is its Solution. Matching ignores case and accents (`moller` finds "Møller").

Accent folding happens in memory, in `PaletteRanking`, because Postgres can only do it
in SQL through the `unaccent` extension and installing one is a migration, not something
a search box decides. A source over a small table of human-typed names (Solutions,
Environments) therefore projects its rows and lets the ranking decide, which folds
correctly. A source over a large table (Releases, Recipes) pre-filters with `ILIKE` per
term first, and that pre-filter is accent-sensitive: `møller` finds a release named for
Møller, `moller` does not. Accepted, and recorded here so the next person does not read
it as a bug.

Results are **grouped by source in a fixed order** - Solutions, Environments, Releases,
Recipes, Go to - so a Solution and its environments do not shuffle against each other as
the user types. Within a group, best tier first, ties broken by name:

1. exact match on a Solution's short name - this row alone is lifted above all groups
2. the query is a prefix of the title or short name
3. every term matches at the start of a word
4. every term matches somewhere (substring)

**Caps:** five rows per group; eight when only one group has any.

**No typo tolerance in the first version.** Fuzzy matching on customer names produces
hits nobody can explain, and it cannot be an indexed query. Initials matching was
considered and dropped as unnecessary once short names are searched.

A row that matched on its short name shows the short name, so the user can see why it is
there.

### An empty query

Opening the palette without typing shows **recent picks**, then the **Go to** list.
Recents are the last few rows the user chose from the palette, kept in that browser's
`localStorage` - no table, nothing server-side, nothing shared (#887). They are re-validated by
being links: a recent the user can no longer open lands on the page's own refusal. If
storage is unavailable the palette simply shows Go to.

## Budget

- **Server:** results in under 100 ms for an organisation with a few hundred Solutions.
  Sources run one after another, not in parallel - they share the request's `DbContext`,
  which allows one operation at a time. Each source is one `AsNoTracking()` query with a
  limit, projecting only the fields a row needs.
- **Browser:** 120 ms debounce after the last keystroke; the in-flight request is
  aborted when a new one starts; a late response for an old query is dropped. A
  one-character query is not sent - Go to filters locally.
- The endpoint is cheap by construction; if it ever shows up in traces, fix the slow
  source rather than adding a cache. It still carries a rate-limit policy beside the
  three in `OperationsRegistration`, because it is the one route a person can hit tens
  of times in a few seconds without meaning to: a token bucket sized well above what a
  120 ms debounce can produce, so typing never trips it and a script is still bounded.
  The partition is the caller's address rather than their user id - `UseRateLimiter`
  runs ahead of `UseAuthentication`, so there is no claim to read at that point.

### What it actually costs (#889)

Measured against a seeded organisation on the Postgres 18 the tests run against,
through `PaletteSearchService` itself rather than through the sources one at a time:
**500 Solutions** (varied names, short names and hosting; one in seven Private, half of
those reachable through a team the caller is on), **1,500 environments**,
**200 releases** (100 of them produced by Solution builds, so the link the Releases
source joins through is real, and 100 Microsoft artifact imports carrying a country in
their dedup key), **300 recipes** with tags, and **1,000 customer contacts**.

Every figure is p50 / p95 in milliseconds, 60 runs after 40 warm-up runs, on a warm
connection. The range in each cell spans seven queries - `cro`, `cronus prod`,
`annette`, `26.0 dk`, `posting`, a two-letter query and one that matches nothing - and
three callers: an org Admin (who bypasses visibility entirely), a member on a team, and
a member on none.

| Per request | p50 | p95 |
| --- | --- | --- |
| The access snapshot, before the first source | 1.0-1.1 | 1.1-1.4 |
| Solutions | 3.8-4.3 | 4.5-5.3 |
| Environments | 3.2-3.6 | 3.8-4.4 |
| Releases | 1.7-2.9 | 1.8-3.4 |
| Recipes | 0.9-1.2 | 1.1-1.8 |
| **The whole fan-out** | **11.4-12.9** | **12.4-14.1** |

The whole fan-out sits at about an eighth of its budget, so **every source keeps the
matching strategy it shipped with**, and no index was added:

- **Solutions - the projection stays, and there is no pre-filter.** 1.2 ms of the 4 ms
  is Postgres: one scan of 500 rows hash-joined to their contacts, all from cache. The
  rest is materialising the ~930 rows that come back and folding them in memory, which
  is what makes `moller` find Møller. An `ILIKE` pre-filter would save about a
  millisecond and cost that.
- **Environments - the same, for the same reason.** 1.5 ms in Postgres over 1,500 rows
  joined to the Solutions the caller may see.
- **Releases - the per-term `ILIKE` stays.** 0.7 ms in Postgres, most of the rest being
  one round trip and the plan. The newest-first `Take` keeps the read bounded whatever
  the term matches.
- **Recipes - the per-term `ILIKE` stays.** 0.3 ms in Postgres and the cheapest source
  in the fan-out.
- **Go to is not a source** and costs the server nothing: it is rendered into the page
  and filtered in the browser.

**`pg_trgm` was not installed and no index was added.** Every one of those queries reads
a few hundred to a few thousand rows out of cache, ordered by a column an index already
covers or sorted in memory in well under a millisecond; an index on a column an `ILIKE
'%term%'` cannot use would be an index on a guess.

### Where it degrades first

At **four times the fixture** (2,000 Solutions, 6,000 environments, 5,000 releases,
2,500 builds, 3,000 recipes) the whole fan-out is still inside budget - 49-62 ms p50,
53-69 ms p95 - but it stops being flat, and the source that moves is **Releases for a
caller who does not bypass visibility**: 2 ms becomes 26-37 ms, while the same query
for an Admin stays at 5 ms.

The cause is worth writing down, because it is not the rows: `VisibleReleasePredicate`
is an anti-join over the locked Solutions, and its row estimate inflates the plan's
*cost* past Postgres's `jit_above_cost` threshold. Postgres then JIT-compiles the
statement on every execution - 23 ms of emission on top of 10 ms of actual work, which
`EXPLAIN (ANALYZE)` reports line by line. The Admin's predicate is `_ => true`, there is
no anti-join, the estimate stays low, and no JIT happens.

Nothing is being done about it now: it is inside budget, the per-source slice below
sheds it if it ever is not, and the fix would be to the shared visibility predicate that
Object Explorer's own release list uses - a change to make on its own evidence, not as a
side effect of a search box.

### A slice per source

A source that cannot answer in **100 ms** is dropped from *that* response and never
waited on. The other sources still return, and the palette shows the groups it has.

- 100 ms is the whole budget handed to one source: spending it alone has already broken
  the promise at the top of this section. It is a fence against a source that is stuck,
  not a deadline that a busy one trips - the slowest source above is 5 ms at p95, and 39
  ms at four times the fixture.
- It is enforced with a `CancellationTokenSource` linked to the request's own token, so
  the SQL command is really cancelled rather than left running against a connection
  nobody is reading. A request the browser aborted is still a cancelled request, not a
  dropped source: it propagates, and the sources behind it are never asked.
- A drop logs one **Warning** naming the source and the elapsed time. Never the query -
  that line lands in the container log.
- Every search logs its per-source timings at **Debug**, on a line that carries no query
  text, so "the palette feels slow" can be answered with which source is slow without
  reading a customer's name over somebody's shoulder. What was typed is on the sibling
  Debug line, as before.

## States

The list pane always shows exactly one of: recents and Go to (empty query), results,
"No search results for '...'" when the search came back with nothing, or "Search is not
available right now" when the request failed. The last two never stack - a failed request
has already explained the silence, so saying nothing matched on top of it would be a
second sentence about the same thing. Either way the closest page is still offered as a
row under Go to, so the palette never ends on a dead end - which is also why the sentence
says "no search results" rather than "nothing matches": something below it plainly did.
While a request is in flight the previous results stay put - no spinner flicker on every
keystroke, and the selected row keeps its place when the new ones arrive.

Keyboard: Up/Down move, Home/End jump to the ends, Enter opens, Ctrl/Cmd+Enter opens in a
new tab, Esc closes and puts focus back where it was.

The foot carries those hints and **at most one thing that is not a hint**: a link to
`/docs/search`, which explains what the palette finds, in the words someone who has never
used one would use. The foot is hidden at phone width, which is why that page is also in
the "Go to" list rather than only here.

### What a screen reader gets

The dialog is a labelled `role="dialog"`, the input is a `role="combobox"` with
`aria-expanded` / `aria-controls` / `aria-activedescendant`, and the list is a
`role="listbox"` of `role="option"` rows. Three things make that actually work rather
than merely validate:

- **Groups are groups.** A heading dropped loose into a listbox is not something a
  listbox may own, and the grouping - which is the difference between "Contoso" the
  Solution and "Contoso" the environment - would be invisible. Each group's rows sit
  inside a `role="group"` carrying the label; the visible heading is `aria-hidden` so it
  is not read twice.
- **The two replacing states are spoken.** Following `aria-activedescendant` a reader
  hears the selected row and nothing else, so "No search results" and "Search is not
  available" - which replace the rows rather than being one - would land in silence. A
  permanent visually-hidden `role="status"` in the dialog carries them, and the result
  count when a query is present. It is in the page before it ever has text, which is what
  makes an announcement fire at all.
- **Focus stays inside, and there are two stops.** The input and the Close button; Tab
  cycles between them. The input suppresses its own ring and the panel draws one instead,
  so the borderless box still shows focus.

Contrast was measured against `tokens.css` rather than judged: the group heading, the
foot and the input's placeholder moved from `--ink-4` to `--ink-3`, because at 11-12px
they are small text and `--ink-4` measures 3.75-4.23:1 in the two themes. The subtitle was
already `--ink-3` (5.85:1 / 6.57:1) and stands. Motion needs nothing: the palette has no
transition of its own, and `tokens.css` already neutralises the app's under
`prefers-reduced-motion`.

The one thing left failing is **not the palette's**: the selected row's `--primary`
keyline is 2.45:1 against `--surface` in light, under the 3:1 WCAG asks of a state
indicator - and it is the system's own selection keyline, shared with the sidebar's active
item, `.data-table` and `.run-row`. Diverging in one sheet would be worse than the defect;
it is recorded in the design brief for a decision upstream.

## Deliberately left out

- Commands that write to a tenant, ever (see "What it never does").
- Searching inside releases, source, or recipe bodies. That includes searching a
  release's *objects* from the palette (`table 18`, `page customer card`), which
  #883 raised as a stretch: the palette would have to pick which release the
  objects came from, and Object Explorer's own search already answers that
  question once a release is open.
- Ordering a group by anything but the shared ranking. #883 asked for a
  Solution's releases newest-first; ranking breaks every tie by title instead,
  for the reason under "Matching and ranking" - one ranking function, or it
  drifts per source.
- Typo tolerance and initials.
- Server-side recents, pinned items, per-user ranking.
- From the first version: pipelines, teams, templates and docs pages as sources. Each is
  one class, and #885 adds them once the first five have proved the contract.
