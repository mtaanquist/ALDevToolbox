# Migrating onto the Business Central design system

Working doc for the `design/bc-system` integration branch. The brief that
produced the design is `design-system-brief.md`; the handoff itself is in
`handoff/` (see `handoff/README.md` for what is imported and how to pull more).

This doc records **what we found when we measured the port**, **the decisions
that need a human**, and **the PR order**. Update it as each PR lands.

## Where the work happens

`design/bc-system` is a long-lived integration branch off `main`. Each step
below is its own PR **targeting `design/bc-system`**, not `main`; the branch
merges to `main` once the app is fully migrated and green. Branch names follow
the repo convention with a `design/` type prefix — `design/tokens`,
`design/shell`, `design/tool-translator`.

Migrating tool-by-tool is the point: each PR moves one surface onto the shared
system **and deletes that surface's bespoke CSS in the same PR**, so the old and
new never coexist long enough to drift.

**Health metric: `wwwroot/tools.css` line count.** 5,472 at the start. It only
goes down. When it hits zero the migration is done.

## Where the branch stands

*Updated after the PR 8 audit. Keep this block current — it is the first thing to
read when picking the work back up. Re-measure with
`python3 .design/progress.py` rather than trusting the numbers below
once a few PRs have landed.*

**33 commits on `design/bc-system`, all pushed.**

### The decision in force

**Finish PR 8 and PR 9 before anything else.** Taken 2026-08-15 by the
maintainer. **PR 8 is done, and has been audited** — the audit found seven live
class collisions, three "moved" blocks where only the comment moved, and
[#544](https://github.com/mtaanquist/ALDevToolbox/issues/544) (the type scale
rendering at 87.5%, app-wide, which is a decision rather than a fix). All but
#544 are closed out. What is left of the decision:

- **9** — settings / site-admin (16 files, 107 refs) **and the Account family**
  (5 files, 101 — of which `Account.razor` alone is 71, the best
  value-per-file left in the queue).

Everything else waits, including the gap below. Do not start 10–14 first
because a page looks easy.

### Honest progress

| | |
|---|---|
| CSS still on the legacy sheets | **75%** (5,185 of 6,933 lines) |
| Components fully on the design layer | **52** |
| Components still referencing a legacy class | **82** |
| Total stale class references | **865** |

`tools.css` 5,472 → 3,698 (−32%), `base.css` 1,095 → 842, `admin.css` 660 → 401.
(`base.css` went *up* from 794: the design-layer bridge grew to cover seven
classes. It is the one file here that shrinks last.)

**Landed:** token layer (1–2), component layer (3), **shell (4)**, Piper +
Compare (5), the five list-archetype browsers (6a–6c), recipe detail + the
Cookbook's loose ends (6d), both generators (7a–7b), audit history + diff (8a),
the four global audit pages (8b), **the whole admin edit-form family (8c)**,
Object Explorer **landing** (14a), the toast component, the tools home and the
admin dashboard.

The fair framing: the *foundation* is finished — tokens, components, shell and
the four archetypes everything plugs into — as is every high-traffic surface a
normal user touches, and now all of admin authoring. What is left is settings,
the two power tools, and the gap below.

### What PR 8c actually needed, versus what the plan said

The plan said "the admin edit forms onto `.sub-rows`". That was written without
reading the files, and only a third of it survived contact:

- **`.sub-rows` fits a flat sortable list** — the module dependency editor, the
  claimed email domains. It needed one variant, `.sub-rows--reorder`, because
  the handoff reorders by dragging a grip and we have never shipped
  drag-and-drop.
- **It does not fit a spreadsheet.** The application-version and dependency
  catalogues are six columns of inline inputs with Excel paste and F8
  copy-above; a six-child row parked in the five-track grid puts the first cell
  under the 26px grip. They went onto `.data-table`, which was already
  underneath them.
- **Two components had no handoff counterpart at all** and were ported onto the
  tokens under their own names: `.folder-editor` (the recursive folder/file tree)
  and `.hint-details` (a `<details>` reference panel — the handoff's screens are
  one level deep and have no disclosure).

**Read the files before trusting a bucket in this plan.** The same caution
applies to PR 9 and to the gap below.

### What is left, by weight

From `.design/progress.py` at `e7fcea8`:

```
GAP    Pipelines / Projects        17 files   360 refs
PR 14  Object Explorer             14 files   133 refs
PR 9   settings / site-admin       16 files   107 refs
PR 9   Account                      5 files   101 refs
shared components + odds           14 files    79 refs
PR 11  auth                         8 files    62 refs
PR 12  docs / MCP / 404             5 files    18 refs
PR 13  Translator                   1 file      3 refs   <- see blind spot below
```

### The plan has a hole: Pipelines / Projects

**363 stale refs across 17 files — the single biggest remaining chunk — and it is
not in the PR 1–14 order at all.** PR 6 migrated the Pipelines *browser*;
`PipelineBuilds` (77), `ProjectDetail` (73), `ReleasePipelineDetail` (45) and the
editor dialogs all landed *after* this plan was written. It needs a PR number and
a scoping pass against `.design/saas-delivery.md`, which nobody has read against
the archetypes yet. **Parked until 8 and 9 are done** — do not start it
opportunistically.

### The progress metric has a blind spot

It counts references to the *shared* legacy sheets. A page with its own
`.razor.css` scores zero regardless of what is in there. **`Translator.razor`
reads as 3 stale refs and carries 395 lines of private CSS** — it is barely
started, not nearly done. 2,192 lines of scoped CSS overall; some is legitimate
(a migrated page keeps its own layout — `RecipeDetail` 305, `CookbookBrowser` 94)
and some is unmigrated styling hiding from the count. Never quote a single stale
count as "this page is done".

**Upstream sync is current.** `tokens.css` and `components.css` have both been
pushed back to the design project, so divergences 1–6 are the design system's
own text. Only divergences 7 (`--sticky-head`) and 8 (always-open nav groups)
are ours to keep — both are app-vs-handoff differences, not errors.

### The bug classes this migration keeps producing

Every defect found after a merge so far has been one of these. Check for them
before calling a page done:

1. **Class collisions.** `tools.css` loads *after* `components.css`, so any
   still-ungated legacy rule with the same class name wins on every property it
   names. Hit us three times: `.ra__menu` (kebabs 35px off, app-wide),
   `.pill-tabs` (`margin-bottom: 16px` misaligning migrated tab bars, plus
   `button.pill-tab` beating the design system's sizing), and `.nav-link-btn`
   beating `.brand__toggle`'s `display: none`. The fix is always to **gate or
   rename the legacy rule**, never to patch an override on top.

   This one is now **enforced by a test** rather than tracked by hand:
   `ALDevToolbox.Tests/Assets/ComponentCollisionTests.cs` parses the four
   stylesheets, diffs the bare `.class` rules that appear in both layers, and
   fails on any layout property the legacy rule does not override — with an
   allow-list of reviewed exceptions that must shrink as families migrate. A
   screenshot only catches the collisions on pages someone thought to look at,
   and the whole failure mode is that nobody looks: the `.ra__menu` kebab bug
   survived months for exactly that reason.
   [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537) tracks the
   remaining three exceptions, all of which retire with the `.ra*` migration
   (#529) and the generator's module picker.

   **The test has a blind spot, and it shipped a bug.** It asks whether the
   legacy rule *fails to override* a design property. It never asks whether the
   legacy rule *overrides one with an incompatible value* — which is the failure
   mode once a MIGRATED page uses the class. `.form-grid` is the worked example:
   the legacy rule names `display` and `gap`, so nothing leaks and the test stays
   green, but it turns the design system's two-column grid into a one-column flex
   stack on every page that has moved. Same for `.field`'s `margin-bottom: 16px`
   and `.field__label`'s uppercasing. PR 7 patched around two of them per-page
   and nothing caught that a third page needed the same patch; they are now the
   **form-scaffolding bridge** in `base.css`, keyed on the `.page` root every
   migrated page carries and no legacy root does. Widening the test is
   [#542](https://github.com/mtaanquist/ALDevToolbox/issues/542).

   **The PR 8 audit found four more of exactly this shape**, so the bridge is
   now "the design-layer bridge" and covers seven: `.card` (legacy `--r-lg` and
   the heavy `--shadow`, so every migrated card sat on a drop shadow instead of
   the 1px hairline), `.data-table` (collapsed borders, 13px body, 8px cell
   padding — the design system sizes cells by `--row-h`, so every migrated table
   was denser than the handoff), `.audit` (a *different* component of the same
   name, the key/value trail on Project detail and Account) and `.section-label`
   (tighter tracking). None of these was visible in a screenshot: legacy values
   are plausible, just wrong. Measure with `scratch/bc-design/collisions.py`
   (parses the sheets and reports which declarations a later one wins) and
   confirm with `scratch/bc-design/cascade-probe.mjs`, which asks a real browser
   instead of guessing at specificity. **Wrap the probe's markup in `.page`** —
   the bridge is gated on it, and measuring outside it answers the wrong
   question.

   The same audit found the mirror-image miss: rules whose comment says they
   *moved* to the design layer, where only the comment moved. `.extension-editor*`,
   `.dep-editor__fields` and `.logo-preview` each had a "moved in 8c-3" note
   sitting directly above the original rules, which — loading last — still won.
   After deleting a block, re-run `scratch/bc-design/dead-css.py`; a rule that
   is still defined and no longer applied is the tell.
2. **Sizing chains broken by the new shell.** `.app__content-inner` is an
   auto-height grid where `.content` used to be a definite-height flex item.
   That silently collapsed Compare's editors to 0px, and the `min-height` fix
   then stretched *every* page because grids stretch auto rows by default. Full
   height is now opt-in via `.u-fill`.

   Third instance, same rule: `.app__content-inner > *` pins `width: 100%` so
   auto margins do not shrink a page root to its content — but a page can also
   mount a `position: fixed` overlay at its root, where `100%` resolves against
   the **viewport**. Both toasts rendered as a full-width bar across the bottom
   of the window. Out-of-flow roots are now excluded by name (CSS cannot select
   on `position`); add to that list when a page mounts a new one.
3. **Grid tracks sized to min-content.** A grid's implicit `auto` track is sized
   to its content's *min*-content, which for `white-space: pre` code is the full
   unwrapped line. Use `minmax(0, 1fr)` on any track that can hold code or a
   long token.
4. **Razor swallows the space before an `@if`.** `@row.DepPublisher <span
   class="tag">` renders as `MicrosoftNot in catalogue`. It has shipped twice —
   `#42in Default` on the SiteAdmin audit list (8b) and the module editor's
   dependency publisher (8c-3). The markup looks correct both times, and a text
   pattern like `/[a-z][A-Z]/` drowns in CamelCase brand names (GitHub, DevOps,
   DeepL). **The durable fix is structural:** a label-plus-tag pair is a flex row
   with a gap, and a gap cannot be swallowed. The detector is
   `scratch/bc-design/run-together.mjs`, which measures whether a text node's
   last glyph touches the next element's box.

### Verification that actually catches things

Screenshots alone have missed real bugs repeatedly — a page can look perfect and
still be broken. What works:

- **Drive the app, don't just shoot it.** Search boxes that never filtered,
  kebabs 35px out of place and a nav that navigated to the site root were all
  invisible in both markup and screenshots.
- **`scratch/bc-design/sweep-stretch.mjs`** walks all 32 routes and asserts on
  `.page-head` height, `.u-fill` fill height, and horizontal overflow. The
  earlier sweep only checked overflow and console errors, and sailed straight
  past a page stretched to 4× its height. **Extend the assertions whenever a new
  bug class appears** — that is what turns the sweep from decoration into a net.
- **Seed the awkward data first.** Local seeds were too tidy to reproduce three
  reported bugs: a short code line hid the overflow, absent min-versions hid the
  chip escape, four keywords hid the table blow-out.
- **A synthetic `mouseover` event does not set CSS `:hover`.** One fix was
  verified green while the page was still visibly broken. Use real pointer hover
  (`locator.hover()`).
- **Self-test any detector before trusting a clean run.** The first
  run-together detector returned clean on a page that definitely had the bug: it
  read the text node's *parent*, and in a grid or flex parent each run of text is
  its own item, so adjacency means nothing there. `run-together.mjs` now exports
  a `selfTest` that plants the bug in the live page and asserts it is reported.
  A detector nobody has watched fire is not evidence.
- **Park the pointer and blur before shooting.** The pointer keeps its viewport
  position across a navigation, so whatever sits under it on the *next* page is
  already `:hover` before anything is measured — that read a resting table cell
  as bordered. A screenshot taken straight after driving a page catches the
  last-touched control mid-hover and mid-focus, which looks exactly like a
  styling bug.
- **Delete CSS by rule, never by range.** "Everything between rule A and rule B"
  removed 896 unrelated lines from `tools.css` in 8c-4, because the two markers
  were nowhere near each other. CSS has no compiler, so nothing failed — the line
  count caught it. Use `scratch/bc-design/retire-css.py`, which walks the sheet
  brace by brace and drops a rule only when every class in its selector is dead
  across `.razor`, `.razor.css`, `.cs` **and** `.js` (a lot of `tools.css` is
  applied by CodeMirror, not by markup). After a deletion pass, re-sweep the
  pages you did *not* change and assert nothing collapsed: a rule that turns out
  to still be load-bearing shows up as a flattened page, not as an error.

## What we measured

### The token layer is a safe drop-in

Every custom property the app reads is either defined in the new `tokens.css` or
was **already a dead reference** before this work. Checked mechanically:

```
grep -rhoE 'var\(--[a-zA-Z0-9-]+' --include=*.css --include=*.razor \
  ALDevToolbox/wwwroot ALDevToolbox/Components | sed 's/var(//' | sort -u
```

18 names came back "not in the new token layer". Four (`--st-todo`,
`--st-todo-bg`, `--st-review`, `--st-review-bg`) are declared locally in
`Translator.razor.css` and are self-contained. One — `--source-viewer-outline-width`
— **is not dead at all**: `source-viewer.js` sets it at runtime for the
resizable Object Explorer pane, and its `340px` fallback is the correct default.
Leave it alone. The remaining **13 were never declared anywhere in the app** and
had been silently resolving to their hardcoded `var(--x, #fallback)` second
argument:

`--accent` `--border-color` `--border-muted` `--line` `--r-md` `--radius-md`
`--radius-sm` `--surface-1` `--surface-hover` `--text-1` `--text-2` `--text-3`
`--text-muted`

Two of these were live bugs rather than cosmetic drift:

- `base.css` used bare `var(--line)` with **no fallback**. An invalid `var()`
  makes the whole `border` shorthand invalid at computed-value time, so that
  row rendered with **no border at all**.
- Several fallbacks baked *dark-theme* greys in unconditionally, e.g.
  `var(--text-1, #d8d8e0)` and `var(--surface-1, #1a1c22)` in `tools.css` —
  dark-mode colours painting on the light theme.

This is exactly the drift the design system exists to stop, and it is a good
argument for the "no `#` in a component layer" non-negotiable.

### What the token swap reaches, and what it doesn't

Colour and type re-point globally the moment `tokens.css` loads. Shape only
re-points where the radius was already tokenised. Counts per stylesheet:

| File | hardcoded hex | `rgba()` | `border-radius: Npx` | `border-radius: var(--r*)` |
| --- | --- | --- | --- | --- |
| `base.css` | 3 | 2 | 12 | 12 |
| `tools.css` | **88** | **60** | **87** | 79 |
| `admin.css` | 13 | 14 | 7 | 13 |
| `auth.css` | 3 | 0 | 0 | 2 |
| scoped `*.razor.css` | 19 | - | 37 | - |

`base.css` and `auth.css` are nearly clean and mostly came along for free.
`tools.css` is where the drift lives, and it is why the migration is
tool-by-tool rather than one sweep.

## Decisions that need a call

The design agent worked greenfield. These are the places where it made a choice
we have to accept, adapt, or reject — worth settling before the component PR,
because each one ripples.

1. **Square corners.** `--r` drops 8px → **2px**, `--r-control` 2px; `--r-pill`
   is reserved for status pills, avatars and progress bars. This is deliberately
   BC-native and it is the single biggest visual change. Because the tokens flip
   globally, the *tokenised* radii went square immediately — but ~136 hardcoded
   `Npx` radii did not, so the app is currently **mixed** until each tool
   migrates. Accepting this means committing to finish.

2. **The primary button is not the brand teal.** `#00B7C3` measures 2.5:1
   against white, so `.btn--primary` fills with `--primary-strong` `#008089`
   and the bright teal is reserved for accents, keylines, focus rings and active
   bars. That is correct for contrast, but it means "BC teal" reads as a deep
   teal in the loudest place. Look at the screenshots before signing off.

3. ~~**The font stack has no non-Windows fallback.**~~ **Settled — see below.**

4. **Status changes shape and meaning.** `.status-pill` becomes a squared tag
   with a 3px left keyline instead of a rounded lozenge, and the system adds a
   rule: **pills are for cards, detail headers and inline text — never for table
   rows.** Rows carry status as a 4px right edge bar (`.data-table--edge` +
   `tr.is-succeeded`). That is an information-design change, not a restyle: it
   touches `BuildStatusPill`, `DeliveryStatusPill`, and every status column in
   Pipelines, Releases and Projects. Decide whether we take the row rule.

5. **`.is-*` for runtime state, `--modifier` for variants.** ~~We currently use
   `.theme-toggle__btn--active`.~~ **Adopted.** The design uses `.is-active` /
   `.is-selected` / `.is-open` / `.is-checked`; the split is the better convention
   and one of its stated rules. The theme toggle was renamed with the shell (PR 4),
   `theme.js` included — it toggles the class at runtime, so a rename that misses
   the JS leaves the highlight dead. Rename the rest as each component moves.
   Note the handoff also ships `.is-hover` / `.is-focus` as spec-sheet display
   aids; those are the ones safe to drop, the state ones are not.

6. **New components with no current equivalent.** `.cue` (BC activity tiles),
   `.switch`, `.skeleton`, `.toast`, `.alert`, `.header-tabs`, `.steps`.
   `.skeleton` and `.toast` fill real gaps (we render bare "Loading..." text and
   have no confirmation feedback). `.cue` is a genuine BC idiom for the admin
   dashboard. Take them as their page needs them, per CLAUDE.md — not up front.

7. **Modern CSS.** `shell.css` uses container queries against a `.shell-root`
   wrapper, and `color-mix()` appears in the focus halo, modal backdrop and
   reconnect overlay. Both are fine for our targets, but they are new to this
   codebase and worth knowing before someone "fixes" them. Both are now live —
   PR 4 shipped `.shell-root` and the three container breakpoints, verified
   firing at 1280 / 1080 / 700. Note `.page` is *also* an inline-size container,
   so a query inside a page body resolves against `.page`, not `.shell-root`.

Density tokens (`--control-h`, `--row-h`, `.u-compact`) are declared but nothing
consumes them yet — they only start mattering as components migrate.

## Settled: the font stack (decision 3)

Segoe UI **cannot be self-hosted.** Microsoft's licence covers using the font to
build software that runs on a Microsoft platform; it does not grant
redistribution or web embedding, and Segoe UI Variable is explicitly excluded
from licensing outside Microsoft products. There is a commercial route, but it
is a procurement conversation, not a download.

Microsoft's own answer is **[Selawik](https://github.com/microsoft/Selawik)** —
an open-source, *metric-compatible* Segoe UI replacement under OFL-1.1, built so
non-Windows targets can match Segoe's metrics. We take a hybrid:

```
--font-sans: "Segoe UI", "Segoe WP", Segoe, device-segoe,
             Selawik, system-ui, -apple-system,
             Tahoma, Helvetica, Arial, sans-serif;
```

Segoe stays first, so **Windows users render genuine Segoe UI and download
nothing** — web-font fetches are lazy and only fire when every earlier family
misses. Everyone else gets Selawik at 32 KB (400 + 600 as `woff2`, in
`wwwroot/fonts/`, declared in `wwwroot/fonts.css` with the OFL text alongside).
`--fw-medium` (500) has no Selawik weight; CSS font-matching resolves it down to
400 rather than synthesising a faux bold, which is the behaviour we want.

**Known gap.** Selawik maps 348 codepoints — Latin only. Verified present:
Danish `æøå`, German, French, Polish, Czech, Turkish, smart punctuation.
Verified absent: **Cyrillic, Greek, Vietnamese, CJK.** Those fall through
per-glyph to `system-ui`, so they render correctly but in a different face from
the surrounding UI. The place this shows is the **Translator**, where a target
string can be any BC locale. If that reads badly once the Translator migrates,
the fix is to scope the grid's target column to a broader stack rather than to
widen the app-wide font.

This *was* the one deliberate deviation between `.design/handoff/tokens.css`
(pristine upstream) and `ALDevToolbox/wwwroot/tokens.css`. It isn't any more —
the correction went upstream, and as of the PR 8 audit the two files are
byte-identical. See `handoff/README.md` for how to keep them that way.

Which matters for [#544](https://github.com/mtaanquist/ALDevToolbox/issues/544):
the type scale is not something we mistranscribed. Both files carry the same
`rem` values and the same "rem against a 16px root" note. The handoff renders
them at 22px / 14px / 12px because its prototype never sets a root font-size
and inherits the browser's 16px; we render them at 19.25px / 12.25px / 10.5px
because `base.css` has set `html, body { font-size: 14px }` since `1b70480`
(2026-05-20) — three months before this migration started. A `rem` scale was
imported onto a root it was not calibrated for.

Measured side by side by `scratch/bc-design/compare-to-handoff.mjs` (same
markup, handoff sheets vs our full stack):

| | handoff | ours |
| --- | --- | --- |
| page title | 22px | 19.25px |
| table head | 12px | 10.5px |
| table cell | 14px | 12.25px |
| button | 14px | 12.25px |
| section label | 11px | 9.625px |

Uniformly 88%. **Type only — the boxes are already right**: `--space-*`,
`--control-h` and `--row-h` are px, so button height matches exactly and rows
land within 1px. That makes #544 a smaller change than the element counts in
the issue suggest.

## Tracked issues

Judgment calls and check-later items are GitHub issues labelled **`redesign`**,
so they survive the branch and this document. Add to that label rather than
growing a TODO list here — there will be many.

Open decisions (these need a human answer):

- [#544](https://github.com/mtaanquist/ALDevToolbox/issues/544) — **the type scale renders at 87.5%.** `tokens.css` says "rem against a 16px root"; `base.css` sets the root to 14px, so every type token, app-wide, is 12.5% small (a page title is 19.25px where the handoff says 22px). One-line fix (`html` 16px, `body` 14px — `rem` resolves against the root, so the inherited default is untouched), but it moves the type on every screen already signed off, so it wants a look rather than a quiet landing. Measured by `scratch/bc-design/verify-root-font.mjs`; blast radius by `probe-root-fix.mjs`.
- [#523](https://github.com/mtaanquist/ALDevToolbox/issues/523) — confirm the `.btn--loading` divergence
- [#524](https://github.com/mtaanquist/ALDevToolbox/issues/524) — does the no-pill row rule apply to *every* data-table?
- [#525](https://github.com/mtaanquist/ALDevToolbox/issues/525) — where link colour lives once `base.css` retires

Deferred work and things to verify:

- [#526](https://github.com/mtaanquist/ALDevToolbox/issues/526) — delete the legacy `--blue*` aliases
- [#527](https://github.com/mtaanquist/ALDevToolbox/issues/527) — `BuildStatusPill` / `DeliveryStatusPill` onto `.status-pill`
- [#528](https://github.com/mtaanquist/ALDevToolbox/issues/528) — div-based run histories onto `.run-list` / `.run-row`
- [#529](https://github.com/mtaanquist/ALDevToolbox/issues/529) — remaining component families
- [#530](https://github.com/mtaanquist/ALDevToolbox/issues/530) — Selawik's Latin-only coverage vs the Translator grid
- [#531](https://github.com/mtaanquist/ALDevToolbox/issues/531) — screenshot-diff against the rendered `.dc.html` sheets
- [#532](https://github.com/mtaanquist/ALDevToolbox/issues/532) — status vocabulary on the remaining admin tables
- ~~[#536](https://github.com/mtaanquist/ALDevToolbox/issues/536) — `RecipeTypeBadge` is the last rounded object on the Cookbook page~~ — **closed in 6d**
- [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537) — component-layer class collisions leak properties the old rules don't override
- [#542](https://github.com/mtaanquist/ALDevToolbox/issues/542) — the collision test is blind to same-class-different-value overrides, which is the half that bites a *migrated* page
- [#543](https://github.com/mtaanquist/ALDevToolbox/issues/543) — Add a user presents two forms where the first can already do the second's job

## What the archetype sheet specifies (read before PRs 5+)

Pulled and rendered `PagesStandard.dc.html` + `ShellPageBody.dc.html` against our
shipped CSS. Our component layer renders the spec's own markup correctly — edge
table, `.stat-card`, `.alert`, `.card`, `.run-list` all match. What the prose
adds, and none of it is guessable from the CSS:

- **Launcher tiles are grouped** under `.section-label` headings (Build /
  Translate and compare / Deliver) *"rather than one flat wall"*. Today's Home is
  a flat wall.
- **A locked tile is a `<span role="link" aria-disabled>`, never an `<a>`**, so it
  cannot be followed, and *"copy says what unlocks it"*. Today's Home links a
  locked tile straight to `/login`.
- **The list archetype has four states, not three.** The fourth is
  **filtered-empty**, which offers "Clear filters" instead of "New" — *"because
  'no templates yet' is a lie when 18 exist."* Worth adopting everywhere.
- **The loading state is skeleton cells under the real header**, so column widths
  do not jump when data arrives.
- **Card view keeps the `.status-pill`** — *"a card has no shared edge to line
  up"*. Only table and list *rows* take the edge treatment.
- **Run history is a real `.data-table`**, *"not a bespoke row layout, so it
  sorts, filters and scans like every other table in the toolbox."*
  `.run-list` / `.run-row` are only for *"card-like histories that are not
  tabular"*. This changes [#528](https://github.com/mtaanquist/ALDevToolbox/issues/528):
  the div-based `.hist-*` / `.del-row` lists should most likely become tables,
  not `.run-list`.
- **Dashboard**: cue tiles carry a "last activity" line so *"a count always says
  how fresh it is"*; the "Needs attention" list uses `.activity--edge`, while
  *"Recent activity stays unstatused — those rows are history, not work items."*
- **Archetype per tool**: Launcher → Home. List → Templates, Cookbook, Projects,
  Pipelines, Releases, Admin Users. Detail → project / pipeline / release pages.
  Dashboard → Admin and Site-admin landings. Object Explorer, Translator, Compare
  and Piper are power tools with their own archetypes, not covered by this sheet.

**Caution: the prototype's own markup is not always consistent with its prose.**
`ShellPageBody`'s top pipelines table still uses `.status-pill` inside `<td>`,
which its own "Status placement, one rule" section forbids; a couple of its rows
are malformed (one `is-failed` row labelled `aria-label="Succeeded"`, one row
missing its state cell, one with a stray trailing `<td>`). Where markup and
prose disagree, **follow the prose** — and the corrected "Recent runs" table
lower in the same file, which does it properly.

### What `PageList.dc.html` adds on top of the prose

The archetype's own screen file is more specific than the spec column, and three
of its details were missed on the first pass at Templates:

- **Both empty states sit inside a `.card`.** A bare `.empty-state` floats on the
  page background with nothing holding it; the handoff always wraps it.
- **The skeleton table is paired with a `.loading-block` caption** underneath —
  spinner plus "Loading templates...". Shipped as `.loading-block--under-table`,
  which is only the padding trim the handoff does with an inline style.
- **`.empty-state__title` / `__text` are `<span>`s, not `<p>`s.** `.empty-state`
  is a grid with its own `gap`; a `<p>` brings the UA's `1em` margins and the
  block visibly loosens.

Also in the file but *not* adopted, and why:

- The filter bar carries `.select-wrap` filters, `.pill-tabs` with counts and a
  `.view-switch` (table ↔ cards). All optional — the spec says the switch
  "toggles table vs cards **per tool**". Templates has no second view; Cookbook
  took the `.pill-tabs` (its type filter maps onto them exactly, counts included)
  but stayed cards-only.
- Row actions are a `.ra` kebab. Templates keeps "View" and "New workspace" as
  visible `.btn--sm` links: hiding a page's main verb behind a kebab is the one
  place the prototype would be distinctly worse for us.

## Gotcha: "the old rules still win" is only true property by property

The whole migration rests on load order — `components.css` before `base.css` /
`tools.css` / `admin.css`, so an unmigrated page's own rules override the new
component rules until the old ones are deleted. That holds **per property**, not
per rule. Every property the new rule sets and the old one doesn't name still
applies, to whatever element happens to carry that class.

It bit hard once already. `.ra__menu` is the *popup* in the design system
(`position: absolute; top: calc(100% + 4px); right: 0; display: none`) and the
`<details>` *wrapper* in this app. `tools.css` overrode `position` and `display`
— enough that the menu still worked — but `top` leaked onto a `position:
relative` box and pushed **every kebab in the app** down by its own height, from
the moment the component layer landed until the Pipelines port happened to put
one in a table cell and someone looked. Six more colliding classes are listed in
[#537](https://github.com/mtaanquist/ALDevToolbox/issues/537).

So: when a component family is only *partly* migrated, check the whole property
set on both sides, not just the ones you meant to change. And prefer renaming
the app's class to the design system's meaning over patching an override — the
override is a holding action, the rename is the migration.

## Gotcha: interactivity is opt-in per page

The list archetype's filter box is the first piece of the redesign that needs
*behaviour*, and it silently did nothing at first. Blazor interactivity in this
app is opt-in: `Program.cs` calls `AddInteractiveServerRenderMode()`, but a page
only becomes interactive when it declares `@rendermode InteractiveServer`.
Without it a page still renders perfectly — `@oninput`, `@onclick` and `@bind`
just never fire, and nothing warns you.

**A screenshot cannot catch this.** The page looked right; only driving the
filter and asserting on the result did. Every archetype with a filter bar, view
switch, sort header or "Clear search" needs the directive, and its PR needs an
interaction check, not just a picture. `CookbookBrowser.razor` is the existing
example.

## Divergence register

**The default is fidelity.** The handoff's screens are worked-through
end-results, not first drafts, so implement them as specified and take a
divergence only where the handoff would be *distinctly worse* in our app —
never because something was easier to reuse or quicker to build. Every
divergence lives here with its reason, so it can be overruled in one place.

| # | Where | What the handoff does | What we do instead | Why |
| --- | --- | --- | --- | --- |
| 1 | `--font-sans` | Bare Segoe UI stack, no web fonts | Segoe → Selawik → `system-ui` | Segoe cannot be self-hosted, and the bare stack fell to Tahoma/Helvetica off Windows. **Pushed upstream** — no longer a divergence. |
| 2 | `--blue*` legacy aliases | `--primary` | `--primary-ink` | `--primary` is 2.5:1; these aliases feed 40 `color:` sites. Migration scaffolding only, deleted as tools migrate. **Pushed upstream.** |
| 3 | `a { }` in `components.css` | `color: var(--primary-strong)` | ~~Rule dropped~~ **Restored at `--primary-ink`.** | **Resolved.** `--primary-strong` measures 4.4:1 on `--bg` and 4.26:1 on `--primary-weak`, both under AA; `--primary-ink` is 6.44 / 6.25. Dropping the rule outright was wrong for the *design system*, whose spec sheets render links against `components.css` alone — the fix was the colour, not the deletion. `base.css` still wins in the app (it loads later). **Pushed upstream.** |
| 4 | `.btn--loading` | Blanks the label, draws a bare `::after` spinner | ~~Our two-span swap only~~ **Both. Pushed upstream.** Markup with a `.btn__label-busy` swaps to it; markup without one gets the handoff's spinner, at full width | **Resolved.** Rendering the handoff's own sheet against our CSS showed its loading buttons collapsing to empty boxes — our version had quietly broken its markup contract. Now additive rather than a divergence. |
| 5 | `.btn--lg`, `.btn--disabled`, `.status-pill--inline` | Not present | Carried over from the app, expressed on tokens | The app uses them; the handoff simply has no equivalent. Additive, not a contradiction. **Pushed upstream.** |
| 6 | `.data-table` | `overflow: hidden` | Dropped; the two header cells take the radius instead | The sheet puts a row-actions kebab *inside* the table (`.data-table__actions` + `.ra`), and `overflow: hidden` clipped its menu — every table's bottom rows lost the end of their menu. The overflow was only clipping the header fill to a 2px radius, which rounding the header cells does just as well. A contradiction in the handoff rather than a preference. **Pushed upstream.** |
| 7 | `.gen { --sticky-head }` | `132px` | `0px` | The variable offsets the sticky preview aside by the height of the handoff's sticky page header — which comes from its `shell.css`, a file we never adopted, so `.page-head--sticky` does not exist in this app. Nothing overlaps the scrollport here (our top bar sits *outside* `main.content`, the actual scroll container), so the clearance is zero. Left at 132px it pushed the preview 57px below the first form section **at rest**, because sticky clamps an element to its top offset even at `scrollTop: 0`. Restore the handoff's value if we ever ship a sticky page head. |

**Upstream sync — done.** `components.css` has been pushed back to the design
project, so divergences 3, 4, 5 and 6 are now the design system's own text and a
re-sync will not silently undo them. The hold-up had been that pushing the file
wholesale would carry all four at once; the answer was simply to check each on
its merits rather than treat "several at once" as a reason not to. All four are
corrections or additions, not preferences:

- **6** and the `.check` native-input hiding are outright bugs (a clipped kebab
  menu; a styled checkbox rendered next to the real one).
- **4** restores a markup contract the handoff's own sheet was breaking.
- **5** is additive — classes the app needs and the handoff has no equivalent for.
- **3** needed *changing* before pushing, not pushing as-is: our copy had deleted
  the rule, which is right for our app (base.css owns links) but wrong for a
  design system whose spec sheets render against `components.css` alone. Pushed
  as `--primary-ink`, which fixes the contrast without removing the rule.

Divergence 7 is deliberately **not** pushed: 132px is correct for the handoff's
own shell, which really does have a sticky page head. That one is a difference
between two apps, not an error.


Anything not in this table should match the handoff. If you find something that
does not, it is drift — fix it toward the handoff.

## PR order

**1. Token layer** — *landed on this branch.* `wwwroot/tokens.css` added as a
verbatim copy of the handoff, the `:root` blocks cut out of `base.css`
(lines 10-189), and the stylesheet linked ahead of `base.css` in `App.razor`.
No markup changed. Verified: `dotnet build` green, app runs, Home and the
workspace generator render in both themes.

**2. Fonts + dead-token housekeeping** — *landed on this branch.* Settled the
font stack (above); replaced all 13 dead `var()` references with real tokens
(68 sites — the dead names repeated far more than the unique count suggested);
and stripped 66 *unreachable* fallbacks, i.e. `var(--defined-token, #hex)` where
the hardcoded second argument could never be reached. `tools.css` hardcoded hex
fell 88 → 26 and `auth.css` reached zero.

That pass also caught a contrast regression the token swap had introduced — see
below. Verified: build green, `dotnet test` 1789 passed / 0 failed, contrast
measured in the running app in both themes.

### The legacy blue alias had to be re-pointed

`--blue` is consumed 40 times as `color:` and 4 times as a focus-ring
`outline:`. The handoff aliased it to `--primary`, but the old ramp's `--blue`
was `#2563eb` at 5.2:1 on white, and `--primary` `#00B7C3` is **2.5:1** — so the
token swap alone had pushed 44 foreground sites below AA. Measured in the app:

| Candidate | on `--bg` | on `--primary-weak` |
| --- | --- | --- |
| `--primary` `#00B7C3` | 2.5:1 | fails |
| `--primary-strong` `#008089` | 4.4:1 | 4.26:1 |
| **`--primary-ink` `#00646B`** | **6.44:1** | **6.25:1** |

`--primary-strong` is calibrated against white `--surface`, but the app paints
page content on `--bg` and selected rows on `--primary-weak`, where it lands
just under 4.5:1. `--primary-ink` is the token the system designates for teal
text on light surfaces and clears AA on every surface we actually use, so the
whole blue ramp collapses onto it for the duration of the migration. Dark theme
is unaffected — `--primary-ink` and `--primary-strong` are both `#5AD8E2` there.

The lesson generalises: **check contrast against the surface a token is actually
painted on, not the one it was calibrated against.** Expect the same question
each time a tool moves onto the component layer.

**3. Core components** — *the layer is in; two families migrated.*
`wwwroot/pages.css` (the archetype layer) now ships too, loaded after
`components.css`.
`wwwroot/components.css` now loads between `tokens.css` and `base.css`, so a
not-yet-migrated page can still override a component and the new rules stay
inert where old ones exist. Each family "activates" when its old rules are
deleted.

Migrated: **buttons** and **status pills**. Their base definitions are gone from
`base.css` (80 lines), `tools.css` (35) and `admin.css` (15).

Still to migrate, each activating when its old rules are deleted:
`.data-table` (tools 14, admin 2) · `.field` / `.input` (tools 19, needs the
`.field__input` → `.input` markup rename) · `.ra*` (tools ~20) · `.module-card`
(base 3, tools 6) · `.form-grid` (base 28) · `.card` / `.stat-card` / `.toast` /
`.page-head` / `.pill-tab` / `.section-label` (tools, small). Then the renames:
`.confirm-modal__*` → `.confirm-dialog__*`, `.page-header` → `.page-head`.

Three things the port found, all of the "every data-driven omission is a flag"
kind — worth expecting again on the next component:

- **`btn--outline` (10 sites) was a dead class**, defined in no stylesheet. The
  component layer's base `.btn` *is* the outline style, so removing it was a
  no-op. Conversely `btn--ghost` (2 sites) was undefined in the app but *is* in
  the handoff, so porting fixed two buttons that had been rendering as plain.
- **`.status-pill--warn` was hardcoded red**, not amber, and marks "Unhealthy"
  and "failed" workers — it meant *danger*. Taking the handoff's amber `--warn`
  verbatim would have silently downgraded a failure signal. Those moved to
  `--danger` / `--failed`; the one genuinely-warning site (a non-active user)
  kept `--warn`.
- **The handoff's `.btn--loading` is worse than ours** and was not taken. It
  blanks the label and draws a bare spinner; the app swaps in a "Generating..."
  busy label, which tells the user what is happening. Our two-span pattern was
  translated onto tokens instead, and `components.css` carries a comment saying
  why. Same for `.btn--lg` and `.btn--disabled`, which the handoff has no
  equivalent for.

### Decisions 4 and 5, as taken

**5 — `.is-*` for runtime state: adopted.** It is a stated rule of the handoff,
and reversing it later is a mechanical rename.

**4 — the status-pill row rule: adopted.** This one was not an open question:
the maintainer directed the design agent to replace the pill column with the
edge colour *plus a state icon*, so it is a decided end-result to implement, not
a trade-off to weigh. A run/delivery-history table now carries:

- `.data-table--edge` on the table, and `is-<state>` on each `<tr>`, which
  paints the 4px keyline on the row's right edge from the `--bar-*` ramp;
- a narrow `.data-table__col-state` column holding `<RowStateIcon>`, which
  renders `.data-table__state--icon` — the glyph that disambiguates the colour
  for anyone who cannot use it, with the state word in `aria-label` + `title`;
- no `.status-pill` anywhere in the row.

`Components/Shared/RowStateIcon.razor` owns the state → row-class + glyph
mapping so the keyline and the icon can never disagree, and exposes
`RowStateIcon.RowClass(state)` for the `<tr>`. That is the translation step: the
handoff specifies CSS classes, and three classes that only make sense together
belong behind one component in our codebase.

**Where the rule applies: everywhere.** I first scoped this to run/delivery
histories, from a qualifier in the `components.css` comment. Rendering the
component sheet settled it — the spec is unambiguous and broader:

> *"Pills are for cards, detail headers and inline text... **Table and list rows
> never use pills.**"* — and of `.data-table--edge`, *"it is the default for
> every table in the toolbox."*

So every `.data-table` takes the edge treatment, and the div-based run and
delivery lists do too. Converted so far: both tables on `/site-admin/workers`,
both on `/templates`, and the project directory. Still on pills, tracked in
[#524](https://github.com/mtaanquist/ALDevToolbox/issues/524): user lists, token
and OAuth-client tables.

**A row with nothing to report gets no bar and no glyph.** The project directory
made this concrete: a project with no build yet has no *status* — colouring its
edge queued-grey and giving it a clock would invent one. The cell stays empty
and the `<tr>` takes no `is-*` class, so the keyline renders transparent.

**The glyph column leads the row.** *"a 4px coloured right edge plus a leading
glyph in a `.data-table__col-state` cell"* — it is the first column, not
wherever the old status column happened to sit.

**Row states come in families**, and class, label and glyph always agree within
one: object diffs (`is-new` / `is-modified` / `is-unchanged`), runs (`is-queued`
/ `is-running` / `is-succeeded` / `is-failed` / `is-cancelled`), lifecycle
(`is-published` / `is-draft` / `is-archived`), XLIFF (`is-untranslated` /
`is-fuzzy` / `is-translated` / `is-final`). `RowStateIcon` implements all four;
five Lucide glyphs were vendored for them at the pinned 1.14.0 tag
(`circle-plus`, `minus`, `circle`, `check-check`, `circle-alert`).

**6a. Templates + Cookbook onto the list archetype** — *landed on this branch.*
Templates took the table view, Cookbook the card view, which is the handoff's
own split: an edge keyline needs a shared edge to line up against, so *"card view
keeps the `.status-pill`."* Both now render four states. `TableSkeletonRows` and
`RowStateIcon` came out of the Templates port; `RecipeTypeBadge` survived
this PR unchanged pending [#536](https://github.com/mtaanquist/ALDevToolbox/issues/536);
6d squared it.

Two carry-overs on Cookbook's card, both invisible when the handoff's own data
is used and both wrong without it:

- **The card is the link, not the title.** A browse grid is scanned and clicked;
  the title-only hit target is a worse affordance at the same pixels.
- **`.browse-card` re-expressed as flex with `margin-top: auto` on the foot.**
  With the handoff's `align-content: start`, the "N files / View recipe" line
  floats at a different height in every card as soon as descriptions and keyword
  rows differ in length. Identical rendering when they don't.

`tools.css` 5431 → 5358. `.cb-head` / `.cb-toolbar` / `.cb-search` survive only
because Projects and the admin Object Explorer index still use them.

**6b. Projects onto the list archetype, table view** — *landed on this branch.*
Replaces `BuildStatusPill` in the "Latest build" cell with the row's edge keyline
plus a leading glyph, which closes this page's slice of
[#524](https://github.com/mtaanquist/ALDevToolbox/issues/524) and
[#527](https://github.com/mtaanquist/ALDevToolbox/issues/527). The freed cell now
carries the BC version as a `.code` chip, falling back to the status word when a
queued or failed build never got one.

**Its search deliberately stays a plain GET form**, so this page needs no
`@rendermode` at all and a filtered result stays a shareable URL. Worth
remembering as the counter-example to the interactivity gotcha below: the
archetype's filter bar is a *look*, not a commitment to client-side filtering.
Verified by driving it in the browser — Enter in the box lands on
`/projects?q=Denmark` with one row.

`.proj-page` and `.art-latest` deleted; `tools.css` 5358 → 5356.

**6c. Pipelines + Releases onto the list archetype** — *landed on this branch.*
Both were bespoke `.alr` row grids; the handoff is explicit that a list like that
should be *"a real `.data-table` ... not a bespoke row layout, so it sorts,
filters and scans like every other table in the toolbox."* ~90 lines of
`.art-*` / `.alr` / `.al-*` went with them; `tools.css` 5356 → 5287.

Pipelines' passive counts strip ("3 pipelines · 1 building now · 2 need
attention") became `.pill-tabs` that actually filter — the same numbers, now an
affordance. Releases has no build status, so its edge keyline carries the one
thing that can be wrong with a release target instead: an environment that has
disappeared from Business Central takes `is-failed`. That signal used to be a
7-word note in a row's meta line.

Three things the port turned up, none of them cosmetic:

- **Pipelines' search had never worked.** The page reads `?q=` through
  `IHttpContextAccessor`, but it is `@rendermode InteractiveServer`, so
  `OnInitializedAsync` runs twice — once prerendering, where `HttpContext`
  exists, and again on the circuit, where it is null. The second pass reset the
  term and every row came back. Confirmed by fetching the prerendered HTML (1
  row) against the settled page (4). Now `[SupplyParameterFromQuery]`, which is
  populated in both passes, on `OnParametersSetAsync` so submitting the form
  reloads. **Any page combining a query-string filter with a render mode needs
  this** — Projects is safe only because it is static SSR.
- **Every kebab in the app was 35px out of place.** See the class-collision
  gotcha above and [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537).
- **`ReleasePipelineRow` had no `ProjectName`**, so the old page linked the
  literal word "Project". Added to the DTO and the projection; the
  `list_release_pipelines` MCP tool returns the same record, so agents get it
  too (description updated to match, per CLAUDE.md's MCP-parity rule).

`RelativeTime.Ago` came out of `Components/Shared/` here rather than becoming a
third identical copy. The *shorter* phrasing on `PipelineEditorDialog` and
`ReleasePipelineDetail` is deliberately left alone — merging it would change
visible copy on two unmigrated pages as a side effect of a refactor.

**7a. New Workspace onto the generator archetype** — *landed on this branch.*
Ships `wwwroot/pages-forms.css` (the form-centric archetype layer, covering PRs
7, 8, 9 and 11) and puts the first page on it. The form is one column beside a
sticky aside holding the tree, the two counts and Generate — so the button left
its sticky bottom bar and now sits next to a preview of what it will produce.

**The folder tree was rebuilt flat.** The handoff's `.tree` indents rows with a
`--d` custom property instead of nesting lists, so the recursion moved out of the
markup into a `Flatten()` and `FolderTreeNode` is gone — one component instead of
two. Collapsing is ours and stays: these trees run to dozens of rows. It costs no
chrome, because the whole folder row is the toggle and the glyph opens, which
keeps the row shape the sheet specifies.

`IdRangeEditor` became one `.id-range` field rather than two labelled ones, and
gained the running **"N IDs"** count the handoff puts there. That is the reason
to prefer it: a range is a size, and the old pair made you do the subtraction.

Two more collisions of the [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537)
kind, both found by looking at the page rather than the markup:

- `tools.css` still owns `.field__label` and renders it as an **uppercase
  micro-label**, which sat next to the real `.section-label` and read as a second
  tier of heading.
- `base.css` still defines `.form-grid` as a **single flex column**, so the
  component layer's two-column grid never applied — the leaked
  `grid-template-columns` does nothing to a flex box, and every pair stacked.

Both are reset in the page's scoped CSS until those families migrate. That is
now the recognisable shape of this failure: *the app's rule wins, and it is the
properties it does **not** name that decide what you get.*

**7b. New Extension onto the generator archetype** — *landed on this branch.*
The sibling page, same shape: the aside now carries the import card, the tree,
the counts, Generate and the drop-in instructions. With both pages moved, the
whole `.workspace-page` shell went — layout grid, scoped form-input styling,
module cards, import card, sticky action bar, `.success-tip`. `tools.css`
5207 → 4894, and it has now roughly halved since the branch started (9,000+ at
the token swap is not the right baseline; 5472 at the first archetype PR is).

Two bugs a screenshot caught that markup review would not:

- **`<InputFile>` renders its `<input>` inside its own component**, so the page's
  scoped CSS never reached it and the browser's native file widget sat visible
  beside our styled button. `::deep` fixes it — the same trap as `IdRangeEditor`'s
  labels.
- **Razor read `1 extension@if (...)` as an email address** and emitted the
  literal `@if` block into the page. A `@`-sign directly after a word is the
  email heuristic; the note is now one computed expression.

**8a. Audit history + diff onto the admin-edit archetype** — *landed on this
branch.* `AuditHistoryPanel` is now the sheet's `.audit` list: one row per
change, expandable **in place** to the diff. It was a table whose "View change"
column linked out to `/admin/audit/{id}/diff`, so the diff was always one page
away from the thing it described. The permalink survives, in the expanded body.

Pairing the snapshots costs nothing — the entries load newest-first, so the
state *after* entry `i` is entry `i-1`'s "before". No second query.

**The diff stays field-level.** The handoff's `.diff` is line-level with two
line-number gutters; ours is field-level, because the inputs are JSON snapshots
of a row and naming the field beats pointing at a line. The sheet's own example
writes a changed line as `"idRangeFrom": 50000 -> 50100` — exactly the shape a
field diff produces — so the rows read the same; they just have no gutters, and
the `+`/`-`/`~` marker still comes free from `.diff__ln--*::before`.

**A pre-existing lie, fixed rather than carried over.** Each audit row stores
the state *before* its change, so the newest row has nothing to diff against —
and `Compute(before, null)` renders every field as *removed*. The old page did
this too, but behind a click-through where few people went; expanding in place
puts it top of the list. That case now shows the recorded state and says nothing
newer exists.

**6d. Recipe detail + the Cookbook's loose ends** — *landed on this branch.*
Four things the maintainer caught on the staging instance, all one slice: the
Cookbook migration had stopped at the browser.

- **The generator preview floated 57px low** (divergence 7). Measured, not
  guessed — `getBoundingClientRect` on `.gen__form` vs `.gen__aside` in the
  running app. Affected both generator screens; drift is now 0 and the aside
  still sticks when scrolled.
- **Long chips escaped the browse cards.** `.code` is `white-space: nowrap`,
  right for what the handoff puts in one — an id, a version number, a short
  keyword. Our first chip is the minimum-version label and BC spells those out
  in full ("Min: Business Central 2024 release wave 2"), so it was wider than
  the card. The tag row's chips now wrap; truncating would have eaten the
  release name, which is the part being read.
- **`RecipeDetail` was never migrated** — still rounded `.rcd-tag` pills, a 26px
  `h1`, and the whole `.codeblock` / `.rd-*` family. Now archetype 3: the
  handoff has no recipe screen, so it is *composed* from its detail pieces
  rather than copied — `.detail-head`, `.meta-row` for the facts that used to
  sit in a rail card, `.code-block` per file, `.code` for keywords. The rail
  keeps only what a rail is for (jumping between files) and appears once there
  is more than one to jump between.
- **The admin table's keyword cell blew the layout open.** Unbounded, its
  max-content width drove the table past the viewport, which squeezed Title to
  its min-content — one word per line — and pushed Last updated off the edge.
  Capping the chip row caps the cell's contribution, so the keywords wrap
  instead of the title.

Two things followed from the work rather than from the report. **#536 is
closed**: `.rtype` was the last rounded object here at `border-radius: 7px`, and
the redesign's whole shape argument is that the round lozenge was the one curve
it removed. It keeps its own tinted colour — a *type* is not a status, so it
does not borrow `.status-pill`'s 3px state keyline — but takes the square
`--r-badge` and, more importantly, `.status-pill`'s 20px height: on the new
detail head a type badge and a Deprecated pill sit side by side, and any height
difference between them reads as a mistake. Both measure 20px at the same top.
And the file bar had been rendering **"1 lines"**, invisible until the block
moved somewhere it could be read.

Our line-numbered, `.tok-*`-highlighted code body is kept over the handoff's
`<b>`/`<i>` scheme: theirs is a prototype stand-in for real AL highlighting,
and the gutter is why `.code-block pre`'s own padding and scroll had to move to
the wrapper. `tools.css` is 6.4KB lighter; the whole `.codeblock` / `.cb-*` /
`.rd-*` / `.rcd-tag` / `.rail-*` family is gone, with `.tok-*` (shared with
`Account.razor`) and `.rtype` kept.

**14a. Object Explorer landing onto the list archetype** — *landed on this
branch.* The one slice of PR 14 that needs no scoping conversation: the release
picker is a browser, not a power tool. Its category tabs map onto `.pill-tabs`
exactly as the Cookbook's type filter does, counts included, and the release
cards onto `.browse-card`.

The page had already been restyled once, against an *older* design study
(`.design/explorer-cookbook/styles/screens2.css`) — rounded 8–11px cards, a
segmented tab bar, a bespoke hero. That is precisely what this migration exists
to retire, so the previous pass is not a reason to leave it alone. 222 lines of
`rel-*` / `rh-*` / `rc-*` / `rg-*` / `src-tabs` went; `tools.css` is at **4,559**.

Two things have no component in the handoff and are composed from its pieces
rather than given a private look: the **latest-release hero** (a `.card` whose
head carries `.section-label` + title + a status pill, over a `.meta-row` of
facts and the two actions) and the **version timeline** (`.section-label`, a
hairline rule, a count). The old timeline's leading dot went — it was the only
instance of that decoration in an otherwise square system.

Judgment calls worth naming:

- **The Ready pill was `--live`, with a pulsing dot.** A release that finished
  importing is not *live*; the pulse implied something was still happening. Now
  `--success` with a check.
- **Tab icons kept**, against Cookbook's icon-less `.pill-tabs`. Microsoft /
  Third-party / C/AL are three similar words and the glyph is the only thing
  that separates them at a glance. `.pill-tab` has no icon slot but nothing in
  it objects to one.
- **"Import a package" added to the page head** (Editor/Admin only). It was
  previously reachable only from the empty state — the one situation where you
  have nothing to import *into*. The archetype has an actions slot; this is what
  it is for. Still the outline style: `Explore objects` is the page's one
  primary.
- **`.oe-release-repos` moved to `OeReleaseDetail.razor.css`.** It had been
  sitting in the *landing* block despite belonging to a different page, and
  would have been deleted with it. Caught by diffing the removed block's class
  names against what the markup still renders — worth repeating on every
  deletion this size.

**4. Shell** — *landed on this branch, out of order.* Planned fourth and taken
after 8a, because every later PR sits inside it: leaving it unmigrated meant
each one inheriting the same scroll-container mismatch that produced divergence
7 on the generators.

`shell.css` is a byte-identical copy of the handoff. The grid is 2×2 with named
areas (`"brand top" / "nav content"`), so the brand became a sibling of the nav
rather than a child, and the old `.main-col` wrapper is gone — the top bar is
its own cell. Renames: `.app-shell`→`.app`, `.sidebar`→`.app__nav`,
`.top-bar`→`.app__top`, `.content`→`.app__content`, `.nav-link`→`.nav-item`,
`.nav-section`→`.nav-group`, `.nav-sublist`→`.nav-sub`, `.sidebar-footer`→
`.app__nav-foot`, `.user-button`→`.user-btn`, `.storage-bar`→`.quota`,
`.theme-toggle__btn--active`→`.is-active` (also in `theme.js`, which toggles
it). `base.css` fell 1,095 → 772 lines.

The `<ul>/<li>` scaffolding went with it: `.nav-group` *is* the grid, so the
lists sat between it and `.nav-item` and both the row gap and the active keyline
landed on the wrong box.

**Two bugs the port produced, neither visible in the markup.**

- **`margin-inline: auto` is not the no-op it was.** The old `.content` was a
  block, where auto inline margins do nothing to an auto-width child; the
  handoff's `.app__content-inner` is a **grid**, where they make the item shrink
  to fit-content and centre. Every page collapsed to its content width, and any
  `.page` that landed under 1080px silently tripped the container query in
  `pages-forms.css` — so the generator's preview pane stacked under the form
  again, 1,493px down. The transition rule now pins `width: 100%` first.
- **`.nav-link-btn` beat `.brand__toggle`.** Putting both on the drawer's close
  button gave it `display: grid` from the later rule, overriding the
  `display: none` that hides it above the drawer breakpoint — a ✕ on every
  desktop page. Same shape as the `.ra__menu` collision in [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537),
  found the same way: by looking at a screenshot, not the CSS.

**The drawer needed an opener.** `shell.css` hides the nav below 700px and shows
it again on `.app.is-drawer-open`, but ships only `.brand__toggle` — which lives
inside `.app__brand`, itself hidden until the drawer is already open. That is
the close control; there was nothing to open with. Rather than ship a nav that
cannot be reached, `wwwroot/shell-drawer.js` (delegated, idempotent, so it
survives enhanced navigation without making the layout interactive) toggles the
class, with a hamburger in `.app__top-lead` at the same breakpoint.

**Divergence 8: expandable nav groups render permanently `.is-open`, without the
caret.** The handoff's `.nav-parent` collapses; ours never has, and collapsing
would hide navigation as a side effect of a restyle — a behaviour change the
work did not call for. A caret that never toggles is worse than none, so it is
omitted rather than rendered inert. The indent and left rule are unchanged, so a
group looks the same open. Revisit as its own change if the sidebar gets long
enough to want it.

`ReconnectModal` keeps Blazor's `components-reconnect-*` class names — its
reconnect JS drives them — but is restyled onto the `.reconnect__card` look:
squared, warning keyline on top, danger once the rejoin has failed. Three
hardcoded hex values went with it. `StorageBar` lost its own two.

Verified by sweeping all 34 routes under the new shell (no horizontal overflow,
no console errors, shell present on each), plus the drawer opening, closing on
Escape, and closing on nav-click. Renames: `.app-shell` → `.app`, `.sidebar` → `.app__nav`,
`.top-bar` → `.app__top`, `.content` → `.app__content`, `.nav-link` →
`.nav-item`, `.nav-section__label` → `.nav-group__label`, `.user-button` →
`.user-btn`, `.sidebar-brand__*` → `.brand__*`. Pull `Shell.dc.html` to diff
against.

**8b. The four global audit pages** — *landed on this branch.* The rest of PR
8's audit half: `/admin/audit`, `/site-admin/audit` and both `/…/{id}/diff`
permalinks, onto the same `.audit` list 8a gave `AuditHistoryPanel`. The two are
the same list with a different filter, so they should not look like different
things.

**A row here does not expand, unlike the panel's.** The panel's entries are all
one entity, so the state after entry *i* is entry *i-1*'s snapshot and pairing is
free. This list is cross-entity and paginated: neighbouring rows are unrelated,
so an expansion would cost a query each. Rows link to the diff page, which
already pairs properly.

That retires the **"Snapshot / Show JSON"** column, which printed the raw
before-state as a `<details>` blob. The page it now links to shows before *and*
after with the changed fields marked, so nothing is lost — and the row is a
sentence ("kirsten.jensen@example.com deleted Catalogue entry #42") rather than
seven columns to reassemble. On the SiteAdmin page the organisation joins that
sentence instead of holding its own column.

Both diff pages move onto archetype 3's pieces: `.detail-head` for the title
row, `.meta-row` for the facts that were a `<dl>`, `.status-pill` for the action
badge that was a bespoke rounded `.audit-action`.

Two additive classes, composed because the handoff has neither: **`.filter-grid`**
(its `.filter-bar` is a search plus tabs; these pages filter on five or six
fields, which is a form) and **`.pager`** (its list archetype renders the whole
result set; four of ours page). The shared `AuditVerb` / `AuditActionPill` /
`FriendlyAuditType` helpers moved to `AdminPageHelpers` on their third call site.

`admin.css` drops 91 lines — the whole `.audit-table` / `.audit-snapshot` /
`.audit-action` / `.audit-pagination` / `.audit-pager` / `.audit-diff__meta` /
`.audit-page` family, every user of which was one of these four pages.

Two bugs the screenshots caught that the class-count probe did not:

- **`#42in Default`** on the SiteAdmin list. Razor strips the whitespace between
  `</code>` and an `@if`, so the id ran into the next word. The space has to live
  *inside* the `<text>` block. The verify script now greps rendered rows for a
  character butting into a capital.
- **A black box around every page title.** `<FocusOnNavigate Selector="h1">` in
  `Routes.razor` focuses the heading after each navigation so a screen reader
  announces the new page — right, and kept. But it stamps `tabindex="-1"`, and on
  a full page load Chrome then matches `:focus-visible` and paints its own
  `outline: auto` in near-black. Measured first: in-app *mouse* navigation leaves
  focus on `<body>` and is clean; a fresh load and keyboard navigation both ring.
  The ring is now suppressed on `h1[tabindex="-1"]` only — the "never remove
  focus rings" rule protects keyboard-operable things, and a heading outside the
  tab order is not one. Interactive rings are untouched, checked by tabbing.
  **This one is app-wide, not audit-specific.**

**Design-review follow-ups: the toast, and the three Cookbook issues** —
*landed on this branch.* Not an archetype PR — the fallout from running the
`design-review` subagent over the migrated recipe detail page, plus the audit it
prompted. Three commits.

**The toast (`ToastHost`).** The recipe page and the Translator had written the
same centred dark pill twice, each with its own copy of a cancel-and-restart
dismiss timer. Both are now the design system's `.toast` in a fixed
`.toast-stack`, behind one shared component. Two bugs fell out: both toasts were
rendering **full-viewport-width** (bug class 2, third instance — see above), and
the Translator flashed a green tick for all fifteen of its messages including
"Couldn't record that vote", so failures and no-ops now take `--danger` and
`--warn`. That also closed the `.toast` entry in #537 — `components.css` was
leaking a border and a width onto the legacy pill, which is moot now the pill
is gone.

**[#539](https://github.com/mtaanquist/ALDevToolbox/issues/539) — the download
modal.** The customer name was required and the button stayed disabled, so
anyone downloading for a demo typed `test`. The interesting part was the
*premise*: single-file recipes are almost always taken with per-file **Copy**,
which never opened the modal, so the attribution was already systematically
blind to the recipes people used most — the required field was buying less than
it cost. Copies are now recorded too (once per visit, no customer), the name is
optional with the reason given as copy rather than as a control, `customer_name`
is nullable and renders as "Not recorded", and a `source` column keeps the admin
panel from becoming a wall of anonymous rows. Follow-up
[#541](https://github.com/mtaanquist/ALDevToolbox/issues/541): most Pipelines
projects *are* a customer, so that box should be suggesting them.

**[#540](https://github.com/mtaanquist/ALDevToolbox/issues/540) — keyword
chips.** Mono said "identifier", chip shape said "filter", clicking did nothing.
They link to `/cookbook?q=<keyword>` now (which works because `SearchAsync`
already matched on keywords — only the affordance was missing) and the browser
page reads `?q=` the same way `PipelinesBrowser` does. On the *card* they stay
labels: the whole card is already an anchor. Added a **`.tag`** component to the
design system — `.code`'s box in the UI face, free to wrap, squared like
everything else — and pushed it upstream.

**[#537](https://github.com/mtaanquist/ALDevToolbox/issues/537) — the audit is
now a test.** Two of its original six were false positives (it compared property
*names*, so a legacy `padding` shorthand looked like it did not override a
component `padding-right`). `.btn--lg`'s legacy rule is deleted — its only two
call sites are the Generate buttons, both migrated. Three reviewed exceptions
remain in the test's allow-list, and the test fails if that list grows *or* goes
stale.

**8c. The whole admin edit-form family** — *landed on this branch, in four
commits.* 25 files, 305 stale refs to zero. `tools.css` 4,548 → 3,758,
`base.css` 880 → 794, `admin.css` 569 → 421.

- **8c-1** the Administration family (9 files) and `PillTabs` off the legacy
  pill, which pulled in the SiteAdmin, Object Explorer and Import section
  headers. Also the **form-scaffolding bridge**: see bug class 1 above.
- **8c-2** the two row-table catalogues onto `.data-table`, not `.sub-rows`.
- **8c-3** the module editor, the recursive folder/file tree, and the four
  template pages, retiring `.preview-card`, `.folder-editor`, `.extension-editor`,
  `.org-file-*`, `.mustache-*` and the CodeMirror mount frame.
- **8c-4** the list, bulk and dashboard pages, plus the tools home (it shared
  `.tile` with the admin dashboard, so neither could retire alone), and a
  rule-aware deletion pass over the legacy sheets.

Bugs found on the way that were not styling: a control that rendered **two of
itself** (the logo picker's native `<input type=file>` had never had a rule, so
the browser's own "Choose File / No file chosen" sat inside our button), a link
that 403'd for the audience it was written for (org admins sent to a SiteAdmin
route for personal access tokens), and a checkbox that promised something that
could not happen (ticked *and* disabled, and a disabled input posts nothing).
Follow-up [#543](https://github.com/mtaanquist/ALDevToolbox/issues/543): the two
overlapping forms on Add a user.

**5+. One tool per PR**, each pulling its archetype CSS from the design project
and deleting its slice of `tools.css`. Suggested order — cheapest proof first,
then best leverage, then the hard ones:

| # | Surface | Archetype | Why here |
| --- | --- | --- | --- |
| 5 | Piper, Compare | 11 (diff) | Small scoped CSS; proves the loop end to end |
| 6 | Templates, Cookbook, Projects, Releases, Pipelines browsers | 2 (list) | One archetype, five pages — the biggest `tools.css` deletion |
| 7 | New Workspace / New Extension | 5 (generator) | The signature screen; live preview + sticky pane |
| 8 | Admin CRUD + audit history | 6 | **Done** (8a–8c). `AuditHistoryPanel`, `AuditDiffViewer`, the `.diff` block, then every admin edit form |
| 9 | Administration / site-admin settings | 7 | Sub-nav header tabs; many small forms |
| 10 | Admin + site-admin dashboards | 4 | Where `.cue` tiles would land. **The admin dashboard and the tools home moved in 8c-4** onto `.tool-grid`; what is left here is the SiteAdmin one |
| 11 | Auth pages | auth card | Self-contained, no shell; `auth.css` is nearly clean already |
| 12 | Docs, MCP setup, 404/500 | 12-14 | Light surfaces, mostly prose |
| 13 | Translator | 9 (grid, compact) | Power tool; `.tgrid` is a full rewrite of the grid |
| 14 | Object Explorer | 10 (source viewer, compact) | Largest surface; three panes, tabs, refs, resizers. **Landing page done (14a)** — it is a list, not a power tool. What is left is the source viewer, which is the part that needs scoping. |

Every user-facing PR still owes the **UX definition of done** checklist from
CLAUDE.md and a `design-review` pass — a restyle is exactly when jargon and bad
empty states survive unnoticed, because the page "looks finished".
