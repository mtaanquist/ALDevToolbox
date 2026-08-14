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

5. **`.is-*` for runtime state, `--modifier` for variants.** The design uses
   `.is-active` / `.is-selected` / `.is-open` / `.is-checked`; we currently use
   `.theme-toggle__btn--active`. The design's split is the better convention and
   is one of its stated rules — adopt it, and rename ours as each component moves.
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
   codebase and worth knowing before someone "fixes" them.

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

This is the one deliberate deviation between `.design/handoff/tokens.css`
(pristine upstream) and `ALDevToolbox/wwwroot/tokens.css`. See
`handoff/README.md` for how to keep the two honest, and consider pushing the
corrected `--font-sans` back to the design project so a re-sync doesn't undo it.

## Tracked issues

Judgment calls and check-later items are GitHub issues labelled **`redesign`**,
so they survive the branch and this document. Add to that label rather than
growing a TODO list here — there will be many.

Open decisions (these need a human answer):

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
- [#536](https://github.com/mtaanquist/ALDevToolbox/issues/536) — `RecipeTypeBadge` is the last rounded object on the Cookbook page

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
| 3 | `a { }` in `components.css` | `color: var(--primary-strong)` | Rule dropped; `base.css` owns links at `--primary-ink` | `--primary-strong` measures 4.4:1 on `--bg`, under AA. Revisit if link colour moves onto the component layer. |
| 4 | `.btn--loading` | Blanks the label, draws a bare `::after` spinner | ~~Our two-span swap only~~ **Both.** Markup with a `.btn__label-busy` swaps to it; markup without one gets the handoff's spinner, at full width | **Resolved.** Rendering the handoff's own sheet against our CSS showed its loading buttons collapsing to empty boxes — our version had quietly broken its markup contract. Now additive rather than a divergence. |
| 5 | `.btn--lg`, `.btn--disabled`, `.status-pill--inline` | Not present | Carried over from the app, expressed on tokens | The app uses them; the handoff simply has no equivalent. Additive, not a contradiction. |

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
`RowStateIcon` came out of the Templates port; `RecipeTypeBadge` survives
unchanged pending [#536](https://github.com/mtaanquist/ALDevToolbox/issues/536).

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

**4. Shell** — `MainLayout`, `NavMenu`, `ThemeToggle`, `ReconnectModal` onto
`handoff/shell.css`. Renames: `.app-shell` → `.app`, `.sidebar` → `.app__nav`,
`.top-bar` → `.app__top`, `.content` → `.app__content`, `.nav-link` →
`.nav-item`, `.nav-section__label` → `.nav-group__label`, `.user-button` →
`.user-btn`, `.sidebar-brand__*` → `.brand__*`. Pull `Shell.dc.html` to diff
against.

**5+. One tool per PR**, each pulling its archetype CSS from the design project
and deleting its slice of `tools.css`. Suggested order — cheapest proof first,
then best leverage, then the hard ones:

| # | Surface | Archetype | Why here |
| --- | --- | --- | --- |
| 5 | Piper, Compare | 11 (diff) | Small scoped CSS; proves the loop end to end |
| 6 | Templates, Cookbook, Projects, Releases, Pipelines browsers | 2 (list) | One archetype, five pages — the biggest `tools.css` deletion |
| 7 | New Workspace / New Extension | 5 (generator) | The signature screen; live preview + sticky pane |
| 8 | Admin CRUD + audit history | 6 | `AuditHistoryPanel`, `AuditDiffViewer`, the `.diff` block |
| 9 | Administration / site-admin settings | 7 | Sub-nav header tabs; many small forms |
| 10 | Admin + site-admin dashboards | 4 | Where `.cue` tiles would land |
| 11 | Auth pages | auth card | Self-contained, no shell; `auth.css` is nearly clean already |
| 12 | Docs, MCP setup, 404/500 | 12-14 | Light surfaces, mostly prose |
| 13 | Translator | 9 (grid, compact) | Power tool; `.tgrid` is a full rewrite of the grid |
| 14 | Object Explorer | 10 (source viewer, compact) | Largest surface; three panes, tabs, refs, resizers |

Every user-facing PR still owes the **UX definition of done** checklist from
CLAUDE.md and a `design-review` pass — a restyle is exactly when jargon and bad
empty states survive unnoticed, because the page "looks finished".
