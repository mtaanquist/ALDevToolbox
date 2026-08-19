# AL Dev Toolbox - design system index

Internal tool for Microsoft Dynamics 365 Business Central developers and consultants.
Plain HTML + CSS, no build step, no JS framework, Lucide icons vendored as inline SVG.
Everything is authored for hand-translation to Blazor: semantic tokens, BEM-ish classes,
`.is-*` for runtime state.

## Files, in load order

| File | Owns |
| --- | --- |
| `tokens.css` | The whole token contract. Light under `:root`, dark under `:root[data-theme="dark"]` and `@media (prefers-color-scheme: dark)` on `:root:not([data-theme="light"])`. |
| `components.css` | Every reusable component. References tokens only. |
| `shell.css` | App shell: sidebar, top bar, content column, sticky page head, responsive steps. |
| `pages.css` | Standard page archetypes 1-4, 8 (launcher, list, detail, dashboard, run monitor). |
| `pages-forms.css` | Archetypes 5-8: generator, admin edit + audit, settings, auth. Owns `.diff`. |
| `pages-power.css` | Archetypes 9-11 at compact density: translation grid, object viewer, compare. |
| `pages-content.css` | Archetypes 12-14: docs/prose, setup steps, error pages. |
| `foundations.css` | Review-sheet scaffolding only. **Not shipped in the app.** |

## Token contract

Semantic names only; a component never names a colour or a pixel.

- **Brand** `--primary` #00B7C3 - `--primary-strong` #008089 (hover/active) - `--primary-stronger` - `--primary-weak` (selected fills) - `--primary-ink` (teal text on light surfaces) - `--on-primary`, `--on-primary-strong`
- **Neutrals** `--bg` - `--surface` - `--surface-2` - `--surface-sunken` - `--border` - `--border-strong` - `--border-input`
- **Ink** `--ink` (body) - `--ink-2` - `--ink-3` (captions) - `--ink-4` (meta). All >= 4.5:1 on their intended surface; `--ink-4` is for >= 12px meta only.
- **Semantics**, each a triple of fill / `-bg` / `-text`: `--success` (favorable #35AB22), `--warning` (ambiguous #9F9700), `--danger` (unfavorable #EB6965) + `--danger-strong`, `--info`. The `-text` members are the derived dark variants, because the BC guide's mustard and coral fail text contrast on white.
- **Status bars** `--bar-*`: succeeded, failed, running, queued, cancelled, published, draft, archived, new, modified, unchanged, untranslated, fuzzy, translated, final. Always a 3px keyline.
- **XLIFF states** `--st-untrans|fuzzy|trans|final` x `{base, -bg, -text}`
- **Diff** `--diff-add-bg` - `--diff-del-bg` - `--diff-chg-bg` - `--diff-gutter`
- **Code** `--code-bg` - `--code-key` - `--code-type` - `--code-num` - `--code-str` - `--code-com` - `--code-obj`
- **Cues** `--cue-*` (BC activity tiles). **Charts** `--chart-1..12`, assign in order.
- **Type** `--font-sans` (Segoe UI stack), `--font-mono` (Cascadia/JetBrains/Consolas), `--text-2xs..3xl` (11-34px), `--leading-*`, `--fw-regular|medium|semibold`
- **Space** `--space-1..8` = 4 8 12 16 20 24 32 40. **Shape** `--r-sm|r|r-lg|r-xl|r-pill|r-control|r-badge`
- **Focus** one ring: `--focus-ring`, `--focus-offset`, `--focus-halo`, `--focus-shadow`
- **Motion** `--ease`, `--transition-fast`, `--transition`
- **Density** `--control-h`, `--control-h-sm`, `--control-h-lg`, `--row-h`, `--row-h-head`, `--cell-pad-x`. Balanced by default; `.u-compact` re-declares them (28px rows) for power tools.
- **Layout** `--nav-w` 248px, `--top-h` 64px. **Elevation** `--shadow-xs|sm|base|lg`.
- **Legacy aliases** at the bottom of the file keep older names resolving; add there rather than renaming in place.

WCAG AA in both themes: >= 4.5:1 body text, >= 3:1 large text and UI boundaries.

## Component inventory

**Actions** `.btn` + `--primary --danger --ghost --icon --sm --loading`, `.copy-btn.is-copied`, `.kbd` / `.kbd-hint`
**Forms** `.field` + `__label __hint --invalid --full`, `.field-error`, `.req`, `.input` + `--num --ro --invalid`, `.input-group` + `__btn`, `.textarea`, `.select` / `.select-wrap` + `__caret`, `.check` + `__box`, `.switch` + `__track __knob`, `.search` + `__icon`, `.form-grid`, `.form-sec` + `__head __note __cap`, `.form-actions` + `__spacer __note --sticky`, `.id-range` + `__sep __count`, `.module-card` + `__title __text __deps __check __row __id`, `.check-list`, `.setting` + `__label __name __hint __ctl --danger`, `.setting__lock`
**Status** `.status-pill` + `--succeeded --running --failed --queued --warn --untrans --fuzzy --trans --final`, `.badge` + `--solid --danger`, `.state-label`, `.commit-chip`, `.refchip` + `--base --target`
**Feedback** `.alert` + `--info --success --warn --danger`, `.note` + `--info --warn --tip --danger` + `__icon __title __body`, `.toast` + `--success` + `__icon __body __title __text`, `.toast-stack`, `.confirm-dialog` + `--danger` + `__head __icon __title __body __actions`, `.modal-backdrop`, `.empty-state`, `.skeleton`
**Containers** `.card` + `__head __title __sub __body __foot`, `.stat-card` + `--accent __label __value`, `.panel`, `.meta-row` / `.meta-item`, `.pane` + `__head __title __count __body __sec __sec-h`
**Navigation** `.header-tabs` / `.header-tab`, `.pill-tabs` / `.pill-tab` + `__count`, `.view-switch`, `.page-head` + `__crumbs __title __sub __actions --sticky`, `.ra` / `.menu` + `__item --danger __sep __icon`, `.toc-link` + `--sub`, `.ftabs` / `.ftab` + `--dirty __ico __x`
**Data** `.data-table` + `__state __num` + row `.is-*`, `.sub-rows` + `__head __foot __empty` / `.sub-row` + `__grip __name __val __acts --drop .is-dragging`, `.run-list` / `.run-row`, `.audit` + `__entry __sum __time __avatar __what __who __caret __body __foot`, `.diff` + `__bar __keys __key __chip __body __ln __gut __code` + `--add --del --chg --void --split`, `.tgrid` / `.trow` (+ `--editing __edge __key __src __tgt __st __acts __ta`), `.tprog`, `.crail` / `.crow`
**Code** `.codeblock` + `__bar __lang __name __pre --inlinebar`, `.codev` + `__ln __n __c __fold` + `.k .t .n .s .c .o`, `.sym` / `.symcard`, `.hunk`, `.prose` (element-scoped)
**Trees and symbols** `.tree` + `__row --gen --group --file __ico __name __meta __legend __key`, `.otree` + `__row __caret __ico __name __id`, `.okind` + `--tab --pag --cod --rep`, `.olist` / `.orow` + `__glyph __name __type`, `.refs` / `.refgrp` / `.refhit`
**Shell** `.app` + `__nav __top __content __content-inner`, `.nav-item`, `.nav-group`, `.quota`, `.user-btn`, `.pw` + `__head __bar __body __foot __title __name __file __spacer __sep`, `.pw-split`
**Auth and errors** `.auth` + `__card __brand __mark __product __head __title __sub __fields __foot __legal __or __ok __ok-icon __mail __link`, `.errpage` + `__inner __glyph __code __title __text __path __acts __links __ref`, `.errlink`
**Steps** `.steps` / `.step` + `__n __head __title __text __body --current .is-done`

## Page archetype catalogue

| # | Archetype | Surfaces | Density | Layer |
| --- | --- | --- | --- | --- |
| 1 | Tool launcher | Home | balanced | pages |
| 2 | List / index | Projects, Templates, Cookbook, Releases | balanced | pages |
| 3 | Entity detail | Workspace, release, recipe | balanced | pages |
| 4 | Dashboard | Admin home, site admin | balanced | pages |
| 5 | Generator + live preview | New workspace, new extension | balanced | forms |
| 6 | Admin edit + audit history | Every admin CRUD edit page | balanced | forms |
| 7 | Settings + sub-nav | Administration, site-admin settings | balanced | forms |
| 8 | Run monitor | Pipelines, Releases | balanced | pages |
| 9 | Translation grid | Translator (XLIFF) | compact | power |
| 10 | Source / object viewer | Object Explorer | compact | power |
| 11 | Diff / compare | Compare, Piper, audit diff | compact | power |
| 12 | Docs / long-form | MCP docs, What's next, Cookbook articles | balanced | content |
| 13 | Setup steps | Connect an agent, onboarding | balanced | content |
| 14 | Not found / server error | 404, 500 | balanced | content |
| - | Auth card | Login, signup, reset, invite | balanced | forms (no shell) |

## Review sheets

`Foundations` (tokens, type, states) - `Components` - `Shell` - `PagesStandard` (1-4, 8) -
`PagesForms` (5-7 + auth) - `PagesPower` (9-11) - `PagesContent` (12-14) - `KitchenSink`
(everything at once, plus the how-to-extend note).

## Non-negotiables

1. Components reference tokens, never raw hex. A `#` in a component layer is a defect.
2. Every interactive element ships all eight states: default, hover, focus-visible, active, disabled, loading, error, empty.
3. One focus ring, everywhere.
4. Status is always keyline + pill + word. Colour alone never carries meaning.
5. Both themes are first-class. A token declared in one theme only is a bug.
6. Sample copy uses CRONUS and ASCII punctuation.
