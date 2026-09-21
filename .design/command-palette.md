# The command palette

Status: **designed, not built.** This document is the outcome of #879; the build is the
rest of milestone "Command palette" (#880-#889). Every decision below was made with the
maintainer on 2026-09-21.

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

The second way in, for people without the habit or the keyboard, is a visible "Search or
jump to..." control in the top bar showing the shortcut (#888).

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
| Releases | release name and version | the release in Object Explorer |
| Recipes | recipe title | the recipe |
| Go to | tool and page names | the page |

Releases and Recipes search **names and titles only**, never objects, source or recipe
bodies - those tables are large and Object Explorer already has a search built for them.
A later version may add a "Search for '...' in Object Explorer" row that carries the
query over; the palette itself never runs that search.

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

## States

The list pane always shows exactly one of: recents and Go to (empty query), results,
"No results for '...'" with a line pointing at the Solutions list, or "Search is not
available right now" when the request fails. While a request is in flight the previous
results stay put - no spinner flicker on every keystroke.

Keyboard: Up/Down move, Enter opens, Ctrl/Cmd+Enter opens in a new tab, Esc closes. The
dialog is a labelled `role="dialog"` with a combobox/listbox pattern, focus is trapped
while it is open, and the selected row is announced (#888).

## Deliberately left out

- Commands that write to a tenant, ever (see "What it never does").
- Searching inside releases, source, or recipe bodies.
- Typo tolerance and initials.
- Server-side recents, pinned items, per-user ranking.
- From the first version: pipelines, teams, templates and docs pages as sources. Each is
  one class, and #885 adds them once the first five have proved the contract.
