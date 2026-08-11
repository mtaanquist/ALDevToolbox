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

**4 — the status-pill row rule: deferred, deliberately.** The pill *component*
is ported (squared tag, 3px keyline). The separate rule that **table rows must
drop their pill for a 4px right edge bar** is not applied, because it is an
information-design change per table rather than a restyle. Make that call when
each table migrates — `.data-table--edge` and the `tr.is-*` classes are already
in `components.css` waiting.

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
