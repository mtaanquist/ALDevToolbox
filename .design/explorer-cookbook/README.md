# Handoff: Object Explorer, Cookbook & Recipe screens

## Overview
This package covers a redesign of three screens in the **AL Dev Toolbox**, all living inside the main work area (the area to the right of the left nav and below the top bar):

1. **Object Explorer — landing**: the first screen you hit from the "Object Explorer" nav item. A picker for every imported reference package (Microsoft base app releases, third-party apps, customer apps). Selecting a release drills into the existing object-list view.
2. **Cookbook**: a searchable, filterable grid of reusable AL recipes (snippets, patterns, modules).
3. **Recipe detail**: the view for a single recipe — description, keyword tags, and its AL source files with syntax highlighting and copy/download.

All three reuse the existing app chrome (left nav, top bar) and the design-token system already established for the Translator and the earlier "New workspace" / Object-Explorer-list redesigns. Only the work-area content is new.

## About the Design Files
The files in this bundle are **design references created in HTML/CSS/React (via inline Babel JSX)** — prototypes that show the intended look and behavior. They are **not production code to copy directly**.

The task is to **recreate these designs in the AL Dev Toolbox's existing front-end environment**, using its established component library, routing, state, and styling patterns. Where this bundle ships raw CSS and React, treat it as a precise spec of layout/spacing/color/type — re-express it in the target stack rather than pasting it in. If part of the app has no front-end environment yet, pick the framework that best matches the rest of the toolbox and implement there.

The HTML prototype switches between screens with a banner segmented control and in-memory React state purely for demo purposes. In the real app, these are separate routes/views reached through the existing left-nav and click-throughs — there is no banner switcher to port.

## Fidelity
**High-fidelity (hifi).** Final colors, typography, spacing, border radii, shadows, hover/active states, and interactions are all specified below and present in the prototype. Recreate the UI to match, using the codebase's existing primitives (buttons, inputs, cards, badges) wherever they already exist — match the visual result, don't introduce a parallel component set.

---

## Design Tokens
These come from the existing app stylesheet (`styles/app.css`) — **reuse the app's real tokens; do not hard-code these hex values if equivalents already exist.** Listed here so the spec is self-contained. Light theme shown; the app also has a dark theme (`:root[data-theme="dark"]`) and a system mode.

### Colors (light)
| Token | Value | Use |
|---|---|---|
| `--blue` | `#2563eb` | primary action, active states, accents |
| `--blue-700` | `#1d4ed8` | hover text, emphasis |
| `--blue-50` | `#eff6ff` | active pill/tab background |
| `--blue-100` | `#dbeafe` | active pill border |
| `--ink` | near-black | primary text/headings |
| `--ink-2` | dark gray | body text |
| `--ink-3` | mid gray | secondary text |
| `--ink-4` | light gray | tertiary text, icons at rest |
| `--surface` | white | cards, panels |
| `--surface-2` | very light gray | inset panels, code bars |
| `--surface-sunken` | light gray | tab tracks, chips, meters |
| `--border` | light | default borders |
| `--border-strong` | medium | dashed empty-state borders, dot separators |
| `--border-input` | input border | buttons/inputs |

### Recipe-type accent colors (defined in `styles/screens2.css`)
| Type | Text color | Background |
|---|---|---|
| Snippet | `--st-trans` (teal) | `--st-trans-bg` |
| Pattern | `--code-num` = `#9333ea` (purple) | 12% of that color |
| Module | `--st-final` (green) | `--st-final-bg` |

### Code syntax highlight tokens (in `styles/screens2.css`)
| Token | Light | Dark |
|---|---|---|
| `--code-type` | `#0e7490` | `#67d6e8` |
| `--code-num` | `#9333ea` | `#c084fc` |
| keyword | `--blue-700` | (bold) |
| string | `--st-final` | |
| identifier (`"..."`) | `--st-fuzzy` | |
| comment | `--ink-4` italic | |

### Radii / shadows / type
- Radii: `--r-lg` (cards), `--r-xl` (hero, empty states). Pills use `999px`.
- Shadows: `--shadow-xs` (cards at rest), `--shadow` (hover, sticky meta card).
- Fonts: `--sans` (UI), `--mono` (versions, filenames, code). Headings are weight 700–800 with negative letter-spacing (`-0.5px` to `-1.5px` on the largest).

---

## Screen 1 — Object Explorer (landing / release picker)

### Purpose
Let the user browse every imported reference package and pick one to explore. Replaces a flat table with a featured "latest import" panel plus version-grouped cards.

### Layout
- Standard work-area page: a scroll container (`.page`) with a centered `.page-inner` (max width matching the rest of the app, generous horizontal padding).
- Vertical stack:
  1. **Page head** — `h1` "Object Explorer" (~27px/700) + subtitle (`.sub`, ~14px, `--ink-3`, max-width ~680px, line-height 1.55).
  2. **Source tabs** (segmented).
  3. **Featured hero** (latest import).
  4. **Count line**.
  5. **Version-grouped cards**.

### Components

**Source tabs** (`.src-tabs`)
- Inline-flex segmented control on a `--surface-sunken` track, `--border`, radius 11px, 4px padding, 3px gap between buttons.
- Each button: 13px/600, `--ink-3`; padding 8px 15px; radius 8px; icon (16px) + label + count badge.
- Active button: `--surface` background, `--ink` text, `--shadow-xs`; its icon turns `--blue`.
- Count badge (`.src-n`): pill, 11px/700 tabular, min-width 18px, on `--surface-sunken`/`--border`; when active → `--blue-50` bg, `--blue-700` text, `--blue-100` border.
- Tabs: **Microsoft** (icon "layersList", count 11), **Third-party** (icon "box", count 4), **Customer** (icon "users", count 0).

**Featured hero** (`.rel-hero`) — shown for the release flagged `latest` (else first)
- Two-column grid `1fr auto`, gap 28px, on `--surface`/`--border`, radius `--r-xl`, `--shadow`, overflow hidden. A 4px-wide `--blue` bar runs down the left edge (`::before`).
- **Left (`.rh-main`)**, padding 26px 28px, vertical stack gap 13px:
  - Caption row: a `.cap-label` "Latest import" (uppercase, letter-spaced, tiny, `--ink-4`) + a status pill "● Ready" (existing `.status-pill` with green dot).
  - `h2` title (`.rh-title`): 30px/800, letter-spacing -0.8px, line-height 1.
  - Meta row (13px, `--ink-3`, wraps): mono version · optional publisher · "📅 Imported {date} · {relative}". Dot separators are 3px circles (`.dotsep`, `--border-strong`).
- **Right (`.rh-side`)**, `--surface-2`, left border, padding 24px 28px, min-width 280px, vertical stack gap 18px centered:
  - Stat: number (`.rh-num`, 38px/800, letter-spacing -1.5px, tabular) + label (`.rh-lbl`, uppercase tiny) "Files indexed".
  - Actions row: **Explore objects** (primary, large, search icon) → drills into object list; **Compare** (secondary, large, compare icon). Each flexes to equal width.

**Count line** (`.rel-count`): 13px `--ink-3`, e.g. "**11** releases imported · **236,765** files indexed" (bold numbers in `--ink`, tabular).

**Version groups** (`.rel-group`) — Microsoft tab groups releases by major version (28, 27, 18, 14…), descending. Other tabs render a single unlabeled group.
- Group head (`.rel-group-head`): 9px blue dot with a 4px `--blue-50` halo + title "Version 28" (13px/700) + a flex spacer hairline (`--border`) + right-aligned count "N releases" (11.5px, `--ink-4`).

**Release card** (`.rel-card`) — grid `repeat(auto-fill, minmax(296px, 1fr))`, gap 14px. Button element, left-aligned.
- `--surface`/`--border`, radius `--r-lg`, `--shadow-xs`, padding 16px 18px, column gap 13px.
- Hover: border → `--blue`, `--shadow`, `translateY(-2px)`.
- Top row: left = name (15px/700, -0.2px) + mono version (11.5px, `--ink-3`); right = arrow icon (`.rc-arrow`, `--ink-4`) that turns `--blue` and slides +3px on hover.
- Foot row (12px, space-between): "**{files}** files" (bold in `--ink`, tabular) and a right "📅 {relative date}" (`--ink-4`).
- **Note:** an earlier version had a file-count progress meter bar between top and foot — it was intentionally **removed**. Do not reintroduce it.

**Empty state** (`.rel-empty`) — Customer tab (no packages) or no search results
- Dashed `--border-strong`, radius `--r-xl`, `--surface-2`, padding 52px 32px, centered column gap 11px.
- Icon tile (58px, radius 16px, `--surface`/`--border`, `--shadow-xs`) + heading (17px/700) + paragraph (13.5px, `--ink-3`, max 460px) + primary CTA "Import a package".

### Data shown (sample content in prototype)
- Microsoft: Business Central 28.2 (latest, 28.2.50931.51034, 15,268 files) down to 14.52, grouped by major.
- Third-party: Continia Document Capture (latest), ForNAV Reports, Tasklet Mobile WMS, Sana Commerce Cloud — each with a `pub` (publisher) shown in the hero meta.
- Customer: empty.
- Relative dates computed against "today"; in production use real timestamps.

### Interaction
- Clicking a tab swaps the list; the featured release is recomputed.
- Clicking the hero "Explore objects" button **or** any release card navigates to the existing **object-list** view for that release, passing the release (label, version, file count) through so the object-list header reads the selected release instead of being hard-coded.
- The object-list view gets a "Back to releases" button (already wired in `screen-explorer.jsx`).

---

## Screen 2 — Cookbook (recipe grid)

### Purpose
Browse/search reusable AL recipes and open one.

### Layout
`.page` → `.page-inner`, vertical stack:
1. **Head** (`.cb-head`, space-between): left = `h1` "Cookbook" (27px/700) + subtitle (max ~680px); right = secondary button "Suggest a recipe" (bulb icon).
2. **Toolbar** (`.cb-toolbar`, flex, gap 14px, wraps):
   - Search (`.cb-search`): flexes to fill, min-width 260px; search icon absolutely placed left; input has 40px left padding. Placeholder "Search the cookbook…".
   - Type pills (`.cb-pills`): All / Snippet / Pattern / Module. Pill = 13px/600, padding 8px 15px, radius 9px, `--surface`/`--border-input`. Active pill = `--blue` bg, white text.
   - Toggle (`.cb-toggle`): custom checkbox "Include deprecated". 19px box, radius 6px, `--border-strong`; checked → `--blue` with white check.
3. **Grid** (`.cb-grid`): `repeat(auto-fill, minmax(340px, 1fr))`, gap 18px.

### Recipe card (`.recipe-card`)
- Button, left-aligned, `--surface`/`--border`, radius `--r-lg`, `--shadow-xs`, padding 20px, column gap 13px. Hover: border `--blue`, `--shadow`, `translateY(-2px)`.
- **Top row** (space-between): type badge (see below) + min-version note (`.rcd-min`, 11.5px `--ink-4`, tag icon + "Min: {version label}", nowrap).
- **Title** `h3`: 16px/700, -0.2px, line-height 1.3; turns `--blue-700` on card hover.
- **Description** (`.rcd-desc`): 13px `--ink-3`, line-height 1.55, clamped to 3 lines (`-webkit-line-clamp`).
- **Tags** (`.rcd-tags`): up to 6 keyword chips (`.rcd-tag`, 11px, `--ink-3`, `--surface-sunken`/`--border`, radius 999px, padding 2px 9px); overflow shown as a "+N" chip (`.more`, `--ink-4`/600).
- **Foot** (`.rcd-foot`): top border `--border`, padding-top 13px, space-between, 12.5px. Left = file-code icon + "{n} files". Right = "View recipe →" (`.rcd-open`, `--ink-4`/600) that turns `--blue` and widens its gap on hover.

### Type badge (`.rtype`)
- Inline-flex, 6px gap, 11px/700 uppercase, letter-spacing 0.4px, padding 4px 10px, radius 7px, 13px icon.
- `.snippet` → teal (`--st-trans` on `--st-trans-bg`), icon "scissors".
- `.pattern` → purple (`--code-num`, 12% bg), icon "puzzle".
- `.module` → green (`--st-final` on `--st-final-bg`), icon "package".

### Filtering / search
- Type pills filter by `type`. Search matches case-insensitively against title + description + tags. "Include deprecated" is a stub toggle (no deprecated entries in sample data) — keep the control but wire to real data.
- No matches → the `.rel-empty` empty state (search icon, "No recipes match", hint).

### Sample recipes (prototype)
"Add extra fields to Posted Sales Inv. - Update" (pattern), "Doc. Attachment List Factbox" (snippet), "Document Folders factbox" (snippet), "HTTP Client Module" (module), "Number series on master-type records" (snippet), "Attach report PDF to outgoing email" (pattern). Each has `min` version label, `minVer`, description, tags, file count.

---

## Screen 3 — Recipe detail

### Purpose
Show one recipe's description, metadata, and AL source files; let the user read, copy, and download them.

### Layout
`.page` → `.page-inner`:
1. **Top bar** (`.rd-topbar`): a single secondary "← Back to cookbook" button on the left. (An earlier duplicate "Download all" here was **removed** — there is only one Download all, in the meta card. Don't re-add it.)
2. **Two-column grid** (`.rd-grid`): `1fr 264px`, gap 32px, items start. Collapses to one column under 980px (rail moves below, becomes a horizontal row).

### Main column (`.rd-main`)
- **Head** (`.rd-head`):
  - Badges row: type badge + min-version note (`.rd-min`, 12.5px, tag icon + "Min: {label} (version)" with the version in mono `--ink-4`).
  - `h1`: 26px/700, -0.5px, line-height 1.25.
  - Description (`.rd-desc`): 14px `--ink-2`, line-height 1.6, max-width 760px.
  - Tags: full keyword set as chips.
- **Files header** (`.rd-files-h`): `.cap-label` "Files" + count.
- **Code blocks** (one per file).

### Code block (`.codeblock`)
- `--border`, radius `--r-lg`, `--surface`, `--shadow-xs`, margin-bottom 18px, overflow hidden. Collapsible.
- **Bar** (`.cb-bar`): `--surface-2`, bottom border (removed when collapsed), space-between.
  - Left: collapse toggle button = chevron (rotates -90° when collapsed) + file-code icon + filename (mono 12.5px/600) + line count (sans 11px `--ink-4`).
  - Right: "Copy" button (`.cb-copy`, 12px/600, copy icon) → copies file source to clipboard, shows toast.
- **Body** (`.cb-body`): horizontal scroll; `<pre><code>` in mono 12.5px, line-height 1.7. Each line is a grid `46px 1fr`: gutter line number (`.cl-n`, right-aligned, `--ink-4` 0.65 opacity, tabular, non-selectable) + code (`.cl-c`, `white-space: pre`).
- **Syntax highlighting** is a lightweight tokenizer (see `screen-cookbook.jsx` → `alTokens`). Token classes: `.tok-kw` (keywords, `--blue-700`/600), `.tok-type` (`--code-type`), `.tok-str` (single-quoted strings, `--st-final`), `.tok-id` (double-quoted AL identifiers, `--st-fuzzy`), `.tok-num` (`--code-num`), `.tok-com` (`//` comments, `--ink-4` italic), `.tok-punc`/`.tok-txt` (`--ink-2`). In production, prefer a real AL grammar (e.g. the existing editor's tokenizer / Monaco / Shiki) rather than this regex tokenizer.

### Right rail (`.rd-rail`) — sticky, top 30px, column gap 16px
- **File index card** (`.card`): head `.cap-label` "Files", then a list of file buttons (`.rail-file`, mono 11.5px, file-code icon + truncated name). Active = `--blue-50` bg / `--blue-700` text / blue icon. Clicking selects that file and ensures its code block is expanded.
- **Meta card** (`.rd-meta`): `--surface`/`--border`, radius `--r-lg`, `--shadow`, padding 16px, rows (`.rdm-row`, space-between 12.5px): Type (badge), Min. version (mono bold), Files (count), then a full-width primary **Download all** button → toast.

### Toast (`.toast`)
- Reuses the app's existing toast pattern: a check-circle icon + message ("Copied {filename}", "Downloading {n} files…"); appears (`.show`) ~1.9s then hides.

> **Prototype caveat:** the detail view currently loads the same two sample AL files (`SalesEventHandler.Codeunit.al`, `PostedSalesInvUpdate.PageExt.al`) for any recipe opened. In production, load each recipe's own files.

---

## Interactions & Behavior (summary)
- **Object Explorer**: tab switch → swap list + recompute featured; card / hero CTA → navigate to object-list for that release (pass release through); object-list → back to releases.
- **Cookbook**: search + type-filter + deprecated toggle filter the grid live; card → recipe detail (pass recipe through).
- **Recipe detail**: collapse/expand each file; rail file click → select + expand; Copy → clipboard + toast; Download all → toast (wire to real download). Back → cookbook grid.
- **Hover**: cards lift `translateY(-2px)` with border→blue and shadow bump; arrows/links slide and recolor to blue. Transitions ~0.12–0.14s.
- **Responsive**: release cards and cookbook cards are auto-fill grids; recipe detail collapses to single column under 980px; hero collapses to single column (side panel moves below with a top border instead of left border).
- **Theming**: everything resolves through tokens, so light/dark/system all work. Verify the code-highlight tokens in dark mode.

## State Management
Per screen, the minimum state:
- Object Explorer: active source tab; selected release (drives drill-in).
- Cookbook: search query, active type filter, include-deprecated flag; selected recipe (drives drill-in).
- Recipe detail: per-file collapsed/expanded set, active file (rail selection), transient toast message.
In the real app, selected-release and selected-recipe should be route params, not local state.

## Assets
- **Icons**: Lucide-style inline stroke SVGs from `app/icons.jsx` (`I.search`, `I.arrowRight`, `I.arrowLeft`, `I.calendar`, `I.box`, `I.users`, `I.layersList`, `I.scissors`, `I.puzzle`, `I.package`, `I.copy`, `I.bulb`, `I.tag`, `I.fileCode`, `I.chevDown`, `I.download`, `I.check`, `I.checkCircle`, `I.compare`, `I.pkgPlus`, …). Use the codebase's existing icon set (Lucide or equivalent) — match by name.
- No raster images or external fonts are required.

## Files in this bundle
- `Reimagined screens.html` — entry point; loads React + Babel and all JSX/CSS below.
- `app/screen-releases.jsx` — **Object Explorer landing** (release picker).
- `app/screen-cookbook.jsx` — **Cookbook grid + Recipe detail** + the AL tokenizer.
- `app/screen-explorer.jsx` — the existing object-list view (drill-in target; updated to accept a release + back handler).
- `app/screen-workspace.jsx` — the earlier "New workspace" redesign (context).
- `app/data-cookbook.jsx` — sample data: releases, recipes, recipe files.
- `app/icons.jsx` — icon set.
- `app/screens-shell.jsx` — left nav + top bar chrome.
- `app/screens-app.jsx` — demo shell wiring the screens together (banner switcher — **demo only**, do not port).
- `styles/app.css` — design tokens + app chrome.
- `styles/screens.css` — shared screen scaffold (page, cards, buttons, status pills).
- `styles/screens2.css` — styles specific to these three screens.

To view the prototype: open `Reimagined screens.html` and use the banner control to switch between New workspace / Object Explorer / Cookbook.
