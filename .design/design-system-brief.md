# AL Dev Toolbox — Design System Brief & Claude Design Prompts

Purpose: hand off to a **Claude Design agent** to produce a Business-Central-inspired
design system (tokens, components, page layouts) that we then translate to Blazor per
the "Implementing a Claude Design handoff" section of `CLAUDE.md`.

This document is the brief. Part 1 orients you and records the decisions. Part 2 is a
sequence of copy-pasteable prompts — run them in order, one per Design agent turn.

---

## Part 1 — Orientation

### What we're replacing

Today's styling works but has drifted: `wwwroot/base.css` (tokens + shell),
`admin.css`, `auth.css`, and a **5,472-line `tools.css`** with ~645 classes named
per-page/per-tool (`.source-viewer`, `.object-explorer`, `.ra`, `.data-table`,
`.workspace-page`, `.ftp`, `.snippet-file`, `.audit-diff`, …), plus ~15 scoped
`.razor.css` files. There is no shared component contract, so every new page invents
its own classes and the app slowly diverges. A real token + component system fixes this.

There *is* an existing token layer (adopted from an earlier Claude Design "Translator"
hand-off): cool-slate neutrals, a **blue** brand (`#2563eb`), XLIFF status colours,
radius/shadow scales, and full **light + dark** theming via `data-theme` +
`prefers-color-scheme`. We keep that *structure* and re-point it to Business Central.

### Decisions (locked)

| Axis | Decision |
|------|----------|
| **Brand colour** | **Full Business Central teal primary** — `#00B7C3`, deepened to `#008089` for hover/active/press. Teal drives primary actions, active/selected states, focus rings, links. |
| **Typography** | **Segoe UI native system stack** for UI (BC's own stack, zero web-font load). Keep a monospace for code/AL/JSON. |
| **Density** | **Balanced** — comfortable spacing for forms, browsers, dashboards, auth; a **compact variant** for the data-heavy power tools (Object Explorer source viewer, Translator grid, diffs). |
| **Theming** | Keep **light + dark**, both required, driven by `[data-theme]` on `:root` plus `prefers-color-scheme` fallback. Light is the default surface. |
| **Framework** | None. Vanilla HTML + CSS (+ tiny vanilla JS only where unavoidable). Output is **translated to Blazor**, not embedded — so favour plain class-based components. |

### The Business Central palette (source of truth)

From Microsoft's Control Add-in Style Guide
(https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/devenv-control-addin-style):

- **Primary** `#00B7C3` (teal) · **Secondary** `#505C6D` (slate) · **Tertiary teal-dark** `#008089`
- **Standard/Strong text** `#212121`
- Sentiments: **Favorable** `#35AB22` · **Ambiguous** `#9F9700` · **Unfavorable/Attention** `#EB6965` · **Subordinate** `#A7ADB6`
- Extended: Yellow `#C9C472` · Green `#88CE81` · Red `#E97768` · Blue `#75B5E7` · Light-green `#59CCB4` · Sky `#75D8E7` · Egg `#EEEA86` · Orange `#E89E63` · Violet `#DBBDEB` · Teal `#39B294` · Grass `#73BA5A` · Scarlet `#E65E6D`
- Chart set (12): `#505C6D` `#008089` `#00B7C3` `#C9C472` `#E97768` `#75B5E7` `#59CCB4` `#75D8E7` `#EEEA86` `#DBBDEB` `#39B294` `#73BA5A`
- Font stack: `"Segoe UI", "Segoe WP", Segoe, device-segoe, Tahoma, Helvetica, Arial, sans-serif`
- Type scale (pt): 37.5 / 30 / 22.5 / 18 / 15 / 13.5 / 12 / 10.5 / 9

**Two things to hold in mind when the agent uses these:** (1) The guide is **light-only** —
the agent must *derive* an accessible dark palette (teal lightened for dark surfaces, etc.).
(2) Several sentiment colours are saturated/retro and **fail text contrast on white**
(`#9F9700` mustard, `#EB6965` coral) — they're fine as fills/pills but need darkened
text variants. The prompts tell the agent to solve both.

### How to drive the Design agent

1. **Everything the agent needs is inline in the prompts** — the BC palette values are in
   Prompt 0, and the app's current token contract (to reuse token *names* for a cheap port)
   is embedded at the end of Prompt 1. You don't need to attach any files; just paste the
   prompts. (The BC guide URL is included for reference if the agent wants the source.)
2. **Optionally attach screenshots** of the current app (Home, a generator, the Object
   Explorer source viewer, the Translator grid) so the agent sees the real densities it
   must support. Not required — the prompts describe each layout.
3. **Run the prompts in order.** Each produces a self-contained HTML page (or small set)
   that renders **both themes side by side**. P1 (tokens) is the foundation every later
   prompt imports — have the agent emit `tokens.css` in P1 and `@import` / inline it after.
4. **Review each stage against the pixels** before moving on. When you hand a stage to me
   (or another Claude Code session) to port, I'll follow the "translate, don't
   transliterate" rules in `CLAUDE.md`.

### The port contract (why the prompts are shaped this way)

Because the output becomes Blazor, the prompts require the agent to:
- Express **all** colour/spacing/type/radius/shadow/motion as **CSS custom properties**
  in one `:root` layer, with **semantic names** (`--primary`, `--surface`, `--danger`),
  reusing our current token names where they already fit (`--surface`, `--ink`, `--border`,
  `--r`, `--shadow`, `--st-*`). Add `--primary`/`--accent` aliases so components reference
  *intent*, not hue (`--blue` → `--primary`).
- Build components as **plain classes with BEM-ish modifiers** (`.btn--primary`,
  `.status-pill--warn`, `.data-table`) — matching conventions already in the codebase so
  CSS ports near-verbatim into `.razor.css`.
- Show **every state** (default / hover / focus-visible / active / disabled / loading /
  error / empty) — these are what drift today.
- Stay **self-contained and vanilla** (no CDN, no framework, inline or local assets).
- Assume **Lucide** icons (we vendor them) — use simple inline SVG placeholders named for
  the Lucide icon, don't invent an icon family.

---

## Part 2 — The prompts

> Prepend **Prompt 0** once (it's the shared brief). Then run **P1 → P7** in order.

---

### Prompt 0 — Shared brief (paste once, or at the top of each turn)

```
You are designing a cohesive, production-grade design system for "AL Dev Toolbox," an
internal web tool used by Microsoft Dynamics 365 Business Central (BC) developers and
consultants. It generates AL/BC project workspaces and extensions, and bundles ~11 tools:
a workspace/extension generator, a template browser, a Cookbook (AL recipe library), an
Object Explorer (IDE-like source/symbol browser), a Translator (XLIFF editor), Projects,
Pipelines, Releases, a Git-merge helper (Piper), a Compare/diff tool, and an MCP server
page — plus a deep Admin and Site-admin area (CRUD forms, dashboards, settings, audit logs).

Audience: professional BC developers and consultants. They live in Visual Studio Code and
the BC web client all day, so the tool should feel native to that world — precise,
information-dense where it needs to be, unfussy.

Visual direction (decided, do not deviate):
- BRAND: Business Central teal as the PRIMARY colour. Primary = #00B7C3, deepened to
  #008089 for hover/active/pressed. Teal drives primary buttons, active/selected states,
  focus rings, and links. Neutrals are a cool slate (BC secondary is #505C6D).
- TYPOGRAPHY: Segoe UI native system stack for UI text —
  "Segoe UI","Segoe WP",Segoe,device-segoe,Tahoma,Helvetica,Arial,sans-serif —
  plus a monospace stack for code/AL/JSON (ui-monospace, "Cascadia Code", "JetBrains Mono",
  Consolas, monospace). No web-font loading.
- DENSITY: "balanced" by default (comfortable forms/lists/dashboards), with a documented
  "compact" variant for data-heavy power tools (grids, source viewer, diffs).
- THEMES: ship light AND dark, both first-class. Light is default. Drive dark via
  :root[data-theme="dark"] AND @media (prefers-color-scheme: dark) on
  :root:not([data-theme="light"]). Render both themes side by side in every deliverable.

Reference — the official BC colour guide values you must build from:
- Primary #00B7C3 · Secondary(slate) #505C6D · Teal-dark #008089 · Text #212121
- Sentiments: Favorable #35AB22, Ambiguous #9F9700, Unfavorable/Attention #EB6965,
  Subordinate #A7ADB6
- Chart set: #505C6D #008089 #00B7C3 #C9C472 #E97768 #75B5E7 #59CCB4 #75D8E7 #EEEA86
  #DBBDEB #39B294 #73BA5A
Note: this guide is LIGHT-ONLY, so you must derive an accessible DARK palette yourself.
Note: some sentiment colours (mustard #9F9700, coral #EB6965) fail text contrast on white —
use them as fills/pills but derive darker text variants. Target WCAG AA: >=4.5:1 body text,
>=3:1 large text and UI boundaries, in BOTH themes.

Engineering constraints (the output will be translated to Blazor by hand, so make that cheap):
- Express ALL design decisions as CSS custom properties in ONE :root token layer, with
  SEMANTIC names (--primary, --primary-strong, --surface, --ink, --border, --danger, --r,
  --shadow, etc.). Components must reference tokens, never raw hex.
- Build components as plain HTML + CSS classes with BEM-ish modifiers
  (.btn / .btn--primary / .btn--sm, .status-pill / .status-pill--warn, .data-table).
  No JS framework, no CDN, no build step — self-contained HTML + CSS, vanilla JS only if
  truly needed.
- Icons: assume the Lucide icon set (we vendor it). Use inline SVG placeholders labelled
  with the Lucide icon name; don't introduce another icon family.
- Show EVERY interaction state: default, hover, focus-visible, active, disabled, loading,
  error, empty. These states are exactly what drifts today, so they are the deliverable.
- Copy style for any placeholder text: use "CRONUS" (the standard BC demo company) for
  sample company/customer/workspace names, and ASCII punctuation (straight quotes, ...).

Deliverable format for each request: a single self-contained HTML file that renders the
requested surface in BOTH light and dark, with a short spec block naming the tokens and
classes introduced. Import the shared tokens.css produced in step 1.
```

---

### Prompt 1 — Foundations (tokens)

```
STEP 1 of the AL Dev Toolbox design system: the FOUNDATION token layer. Output a
self-contained "foundations.html" plus the tokens.css it embeds, rendering the full system
in both light and dark, side by side.

Produce these token groups as CSS custom properties on :root (light) with the dark
overrides in :root[data-theme="dark"] and the prefers-color-scheme media query:

1. COLOUR
   - Brand ramp from BC teal: --primary (#00B7C3), --primary-strong (#008089) for
     hover/active, --primary-weak (a light teal tint for selected-row / badge backgrounds),
     --on-primary (text/icon on primary fills). Derive dark-theme equivalents (lighten teal
     so it stays legible on dark surfaces; pick an --on-primary that passes contrast).
   - Neutral slate ramp anchored on BC secondary #505C6D: --bg, --surface, --surface-2,
     --surface-sunken, --border, --border-strong, --border-input, and an ink ramp
     --ink / --ink-2 / --ink-3 / --ink-4 (headings -> muted). Keep them cool to sit under teal.
   - Semantics with matching tint backgrounds and accessible text variants, following the
     existing --x / --x-bg pattern: --success (from Favorable #35AB22), --warning (from
     Ambiguous #9F9700 — darken for text), --danger (from Unfavorable #EB6965 — darken for
     text), --info (teal-leaning or BC blue #75B5E7). Provide --*-bg and --*-text for each.
   - The four XLIFF translation-status tokens (keep the model, re-anchor to the new palette):
     --st-untrans, --st-fuzzy, --st-trans, --st-final, each with a -bg pair.
   - A 12-colour categorical CHART palette (--chart-1..--chart-12) from the BC chart set,
     tuned for legibility on both themes; document the intended order.
   - Aliases for a cheap port: map the app's current names to the new ones so old CSS still
     resolves — e.g. --blue: var(--primary); --blue-700: var(--primary-strong);
     --good: var(--success). List every alias.

2. TYPOGRAPHY
   - --font-sans (Segoe UI stack above) and --font-mono.
   - A type scale expressed in rem, derived from BC's proportions (their pt scale is
     37.5/30/22.5/18/15/13.5/12/10.5/9) but tuned for a 14px base UI: e.g.
     --text-xs ... --text-3xl, plus --leading-tight/-normal, and weight tokens
     (--fw-regular 400, --fw-medium 500, --fw-semibold 600). Show a full type specimen.

3. SPACING & SHAPE
   - A spacing scale --space-1..--space-8 (4px base). Radius --r-sm/--r/--r-lg/--r-xl
     (keep 6/8/12/16). Elevation --shadow-xs/-sm/-/-lg for both themes (soft on light,
     deeper on dark). A --focus-ring token (2px teal outline + offset) used everywhere.

4. MOTION
   - --transition-fast / --transition (ease + duration) and a reduced-motion note.

5. DENSITY
   - Define control-height and row-height tokens for "balanced" (default) and document a
     "compact" override (e.g. a .u-compact scope that re-points --control-h/--row-h/--space)
     used later by the power tools.

Render, in foundations.html: swatch grids for every colour token (with hex + the token
name + a pass/fail contrast note against its intended text colour), the type specimen, the
spacing/radius/shadow/density specimens — all shown in light and dark. This file is the
canonical reference; keep token names stable for later steps.

--- CURRENT TOKEN CONTRACT TO REUSE ---
Below is the app's existing :root token layer (light + dark). REUSE these names wherever
they still fit (--surface, --ink*, --border*, --st-*, --good, --danger, --r*, --shadow*,
--sans/--mono, --nav-w, --top-h) and RE-POINT their values to the BC palette. Re-cast the
blue ramp to teal but keep the aliases (--blue -> --primary) so existing CSS keeps
resolving during the migration. Keep the two dark blocks (media query + data-theme) in sync.

:root {
    color-scheme: light;

    /* neutrals — cool slate, very subtle */
    --bg:            #f7f8fa;
    --surface:       #ffffff;
    --surface-2:     #fbfcfd;
    --surface-sunken:#f1f3f6;
    --border:        #e6e8ec;
    --border-strong: #d6dae1;
    --border-input:  #d2d6dd;

    --ink:           #0f172a;  /* headings */
    --ink-2:         #334155;  /* body */
    --ink-3:         #64748b;  /* secondary */
    --ink-4:         #94a3b8;  /* micro-labels / muted */

    /* brand — CURRENTLY blue; re-cast this ramp to BC teal (#00B7C3 / #008089)
       and add --primary/--primary-strong/--primary-weak/--on-primary aliases */
    --blue:          #2563eb;
    --blue-600:      #2563eb;
    --blue-700:      #1d4ed8;
    --blue-50:       #eff4ff;
    --blue-100:      #e0eaff;
    --on-blue:       #ffffff;

    /* status (XLIFF states) — semantic, consistent weight */
    --st-untrans:    #d97706;
    --st-untrans-bg: #fef3e2;
    --st-fuzzy:      #b45309;
    --st-fuzzy-bg:   #fdf0dc;
    --st-trans:      #2563eb;
    --st-trans-bg:   #eaf1ff;
    --st-final:      #15803d;
    --st-final-bg:   #e7f5ec;

    --good:          #16a34a;
    --good-bg:       #e7f6ec;

    --danger:        #b32121;
    --error-text:    #e50000;
    --info:          #2563eb;
    --shadow-color:  rgba(0, 0, 0, 0.1);

    /* shape */
    --r-sm: 6px;
    --r:    8px;
    --r-lg: 12px;
    --r-xl: 16px;

    --shadow-xs: 0 1px 2px rgba(15,23,42,.05);
    --shadow-sm: 0 1px 2px rgba(15,23,42,.06), 0 1px 1px rgba(15,23,42,.04);
    --shadow:    0 4px 14px rgba(15,23,42,.07), 0 1px 3px rgba(15,23,42,.05);
    --shadow-lg: 0 12px 34px rgba(15,23,42,.12), 0 3px 8px rgba(15,23,42,.06);

    /* --sans CURRENTLY leads with Inter; re-lead with the Segoe UI stack */
    --sans: "Inter", ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    --mono: "JetBrains Mono", ui-monospace, "SF Mono", "Cascadia Code", "Consolas", monospace;

    --nav-w: 248px;
    --top-h: 64px;

    /* code syntax accents (Cookbook recipe view) */
    --code-type: #0e7490; --code-num: #9333ea;
}

/* dark theme — applied both via prefers-color-scheme and the explicit toggle;
   keep both blocks identical. Re-cast to a derived dark BC palette. */
:root[data-theme="dark"],
:root:not([data-theme="light"]) /* @media (prefers-color-scheme: dark) */ {
    color-scheme: dark;

    --bg:            #0b0f17;
    --surface:       #11161f;
    --surface-2:     #0e131b;
    --surface-sunken:#0a0e15;
    --border:        #1f2630;
    --border-strong: #2a323d;
    --border-input:  #2c343f;

    --ink:           #f1f5f9;
    --ink-2:         #cbd5e1;
    --ink-3:         #93a1b3;
    --ink-4:         #6b7787;

    --blue:          #5b8def;   /* -> derived light teal on dark */
    --blue-600:      #5b8def;
    --blue-700:      #6f9bf2;
    --blue-50:       #16243f;
    --blue-100:      #1b2c4d;
    --on-blue:       #ffffff;

    --st-untrans:    #e0a13c;  --st-untrans-bg: #2a2114;
    --st-fuzzy:      #d4944a;  --st-fuzzy-bg:   #2c2113;
    --st-trans:      #6f9bf2;  --st-trans-bg:   #16243f;
    --st-final:      #4ec57f;  --st-final-bg:   #12281c;

    --good:          #4ec57f;  --good-bg:       #12281c;

    --danger:        #ef6464;
    --error-text:    #ff6b6b;
    --info:          #6ba3ff;
    --shadow-color:  rgba(0, 0, 0, 0.5);

    --shadow-xs: 0 1px 2px rgba(0,0,0,.4);
    --shadow-sm: 0 1px 2px rgba(0,0,0,.45);
    --shadow:    0 6px 18px rgba(0,0,0,.5);
    --shadow-lg: 0 18px 44px rgba(0,0,0,.6);

    --code-type: #67d6e8; --code-num: #c084fc;
}
```

---

### Prompt 2 — Core component library

```
STEP 2: the CORE COMPONENT LIBRARY, built entirely on tokens.css from step 1. Output
"components.html" showing every component in every state, in light and dark.

Build these, as reusable classes with BEM-ish modifiers, matching the app's existing names
where noted so the Blazor port is near-verbatim:

- Buttons: .btn (default/outline), .btn--primary (teal — the ONE primary style;
  there is only ever one primary button per page), .btn--danger, .btn--ghost, .btn--sm,
  .btn--icon, and a split-button + dropdown (our .ra / row-actions pattern: a primary action
  fused to a caret that opens a kebab menu). States: hover, focus-visible, active, disabled,
  loading (spinner + label).
- Form fields: a .field wrapper composing label + control + caption/hint + inline error
  (.field-error, keyed per field). Cover text input, textarea, select, number, checkbox,
  radio, toggle/switch, and a search input. Show default/focus/disabled/invalid. Inputs
  must read as native BC-client controls (teal focus ring). Also a horizontal "row" form
  layout and a two-column form-grid.
- Cards: a base .card (surface + border + optional header/footer), and a selectable card
  (our .module-card: click anywhere toggles; selected = teal tint + teal border with a check).
- Tabs: pill-style tabs (.pill-tab, our PillTabs) AND a sub-nav "header tabs" bar used to
  switch between related settings sub-pages.
- Status pills / badges: .status-pill with --muted/--warn/--success/--danger/--info
  variants, a "live" dot variant, and a build/delivery status pill (queued/running/
  succeeded/failed) since Pipelines/Releases lean on it heavily.
- Data table: .data-table (header, zebra-optional rows, hover, right-aligned actions
  column, sortable header affordance, a per-row kebab actions menu). Include a compact
  density variant.
- Overlays: a modal/dialog (.confirm-dialog — title, body, cancel + confirm, a danger
  confirm variant), a dropdown menu, a tooltip, and a toast/notification.
- Cross-cutting states (CRITICAL — these are the ones we keep getting wrong): a reusable
  LOADING block, an EMPTY state (icon + one-line explanation + a single primary action to
  create the first item — never a bare table), and an ERROR/retry block. Show the canonical
  loading/empty/populated triptych for a list.
- Page chrome: a page header (title + optional subtitle + breadcrumb + a right-aligned
  primary action), a section label (11px, 500, letter-spaced, muted — our .section-label),
  and a stat card / stat tile (label + big number + optional delta) for dashboards.
- Code affordances: an inline .code chip and a code block using --font-mono, plus a
  copy-to-clipboard button pattern.

For each component include a one-line "port note" naming the existing class it maps to
(e.g. "maps to .btn--primary in base.css") when one exists.
```

---

### Prompt 3 — App shell & navigation

```
STEP 3: the APP SHELL, on tokens.css + the component library. Output "shell.html" in light
and dark.

Layout: a fixed left SIDEBAR (~248px) + a top bar + a scrolling content area, as a CSS grid
that pins the sidebar and top bar and scrolls only the content (so sticky elements inside
pages work).

Sidebar:
- A brand row at top (hammer icon + "AL Dev Toolbox"), aligned to the top bar height so the
  two share one continuous top rule.
- A primary nav list of TOOLS, each an icon + label link with a clear active state (teal
  accent bar / teal text + tint). Include grouped items with an expandable sub-list (e.g.
  "Templates" -> Workspace / Extension) — show collapsed and expanded, and the active-child
  state.
- Sectioned lower groups with muted section labels and a divider: an "Admin" section and a
  "Site administration" section (these appear by role). Show the divider + label treatment.
- A pinned sidebar FOOTER: an optional storage/quota capacity bar, a row of external-link
  icons (incl. a GitHub mark), and a copyright line.

Top bar:
- Right-aligned cluster: a light/dark THEME TOGGLE (segmented light/dark/system), a user
  button showing name + org + role, and a sign-out icon button. Also show the
  not-authenticated variant (a single "Sign in" link).

Also produce: the reconnect/"connection lost" modal overlay (this is a Blazor Server app; a
dropped circuit shows a blocking reconnect banner — design the overlay + spinner + retry).

Show the shell with a representative page body slotted in, in both themes, and demonstrate a
narrow-viewport behaviour (sidebar collapses to icons or a drawer — desktop is primary,
but don't break at laptop widths).
```

---

### Prompt 4 — Standard content archetypes (launcher, list, master–detail, dashboard)

```
STEP 4: four STANDARD PAGE ARCHETYPES inside the shell, on the tokens + components. Output
"pages-standard.html" (one scrollable page showing all four, or a tabbed switcher), light
and dark.

1. TOOL LAUNCHER / HOME: a responsive grid of tool tiles (icon + title + one-line caption,
   whole tile is a link, teal hover). Include a "locked" tile variant (a lock badge for a
   tool the user must sign in / be granted to use). Header: "AL Dev Toolbox" + "Pick a tool
   to get started."

2. LIST / BROWSER (the workhorse — Templates, Cookbook, Releases, Projects, Pipelines all
   use it): a page header with a primary "New ..." action + a search/filter row, then the
   content as BOTH a data table (dense) and a card grid (browsable) — show both so we can
   pick per tool. MANDATORY: render all THREE states — loading, empty (icon + "no X yet" +
   a primary create action), and populated. Rows/cards carry a status pill and a per-row
   kebab actions menu.

3. MASTER-DETAIL (Projects/Pipelines/Releases detail): a detail page with a header
   (title + status pill + primary action + kebab), a row of key/value metadata, pill tabs
   switching sub-views, and a build/run history list where each row has a status pill, a
   short git commit ref chip, a relative timestamp, and row actions. Show a running build
   (animated/queued state) and a failed build.

4. DASHBOARD (Admin): a grid of stat tiles (counts + a "last activity" timestamp) above a
   couple of recent-activity lists, each tile linking to its section. Keep it calm — this is
   a landing page, not a control room.
```

---

### Prompt 5 — Form archetypes (generator, admin edit + audit, settings, auth)

```
STEP 5: four FORM-CENTRIC ARCHETYPES, on tokens + components + shell. Output
"pages-forms.html", light and dark.

1. GENERATOR with LIVE PREVIEW (New Workspace / New Extension — the product's signature
   screen): a two-column layout. LEFT = a sectioned form (section labels: RUNTIME TEMPLATE,
   PROJECT, CORE ID RANGE, MODULES, OPTIONS): a template <select> with a description caption,
   text/textarea fields, from/to numeric ID-range inputs, a vertical list of selectable
   module cards (checkbox cards, "+ N dependencies" caption, whole card toggles), and option
   checkboxes. RIGHT (sticky) = a live monospace FOLDER-TREE preview using Lucide folder
   icons (generated extension folders tinted teal to distinguish them from static/grouping
   folders), two small stat cards ("Extensions: N", "Dependencies: M"), and a full-width
   primary "Generate" button with a loading state. Show inline field validation errors.
   Placeholder copy uses CRONUS (e.g. name "CRONUS Customer").

2. ADMIN EDIT FORM + AUDIT HISTORY (every admin edit page): a single-column form mirroring
   an entity's fields (text, selects, JSON-ish textareas, a sortable list of sub-rows with
   add/edit/delete + drag handle, a "Deprecated" toggle), a form-actions row (Save primary,
   plus Delete with a confirm dialog, plus Restore when soft-deleted), and BELOW it an
   "Audit history" panel: a reverse-chronological list of entries (timestamp + who + action)
   that expand to a before/after JSON diff. Design the diff view (added/removed/changed line
   tints on the mono block) — it's reused by the audit log too.

3. SETTINGS with SUB-NAV (Site-admin settings / Administration): a settings surface with a
   header tab bar switching sub-pages (General, SMTP, Backups, Quotas, Tools, ...), each a
   compact form. Show a "dangerous setting" treatment and a saved/confirmation toast.

4. AUTH CARD (Login, Signup, Forgot/Reset password, Accept invite): a centred card on a
   calm full-page background — brand mark, title, fields, a single primary action, secondary
   links (sign up / forgot). Show the error state (bad credentials) and a success/"check your
   email" state. This is the first thing a new user sees; make it feel trustworthy and BC-native.
```

---

### Prompt 6 — Power-tool archetypes (data grid, source viewer, diff)

```
STEP 6: three DATA-DENSE POWER-TOOL archetypes. Use the "compact" density variant from
step 1. Output "pages-power.html", light and dark. Fidelity and legibility at high density
are the whole point here.

1. TRANSLATION GRID (Translator / XLIFF editor): a full-height, dense editable grid of
   translation units. Each row: a source string, an editable target string (inline edit,
   textarea-on-focus), a translation-status pill (untranslated / needs-review-"fuzzy" /
   translated / final — use the --st-* tokens), and per-row actions (accept, machine-
   translate, clear). Above the grid: a filter/toolbar (language picker, status filter,
   search) and progress indicators per status. Show a focused/being-edited row, a fuzzy row,
   and keyboard-affordance hints. This must stay readable with hundreds of rows — design the
   sticky header, the row rhythm, and a virtual-scroll-friendly row.

2. SOURCE / OBJECT VIEWER (Object Explorer — an IDE-like reading surface): a three-pane
   layout — LEFT a collapsible module/object tree, CENTRE a read-only CODE view with line
   numbers and AL/mono syntax tinting (types, keywords, numbers use dedicated code tokens),
   RIGHT an inspector panel (object outline: fields/procedures/triggers, and a "references"
   list linking symbols). Include a tab strip for open files, a symbol/definition hover
   affordance, and a find-references result list. Panes are resizable (show drag handles).
   This is where BC developers spend real time — it should feel as considered as VS Code,
   in our teal-on-slate palette.

3. DIFF / COMPARE (Compare tool, release/file compare, audit diff): a side-by-side (and a
   toggled inline) diff of two code/text versions, with added/removed/changed line tints
   that work in both themes, a file/change list rail, and a header showing the two versions
   being compared (commit ref chips, labels). Reuse the audit JSON-diff treatment from step 5.
```

---

### Prompt 7 — Content pages + kitchen-sink review

```
STEP 7 (final): the remaining lighter surfaces plus a consolidated review page. Output
"pages-content.html" and "kitchen-sink.html", light and dark.

Content surfaces:
- A DOCS / long-form content page (MCP docs, "What's next"): readable prose column with
  headings, code blocks, callout/note boxes (info / warning / tip), and an on-page table of
  contents. Constrain measure for readability.
- A "connect an agent" / MCP setup page: numbered step cards, each with a copyable code
  snippet (endpoint URL, config JSON) and a copy button.
- NOT-FOUND (404) and generic ERROR pages: friendly, on-brand, with a path back.

KITCHEN SINK: a single page that places every token swatch, every component, and a thumbnail
of every page archetype together, in both themes, so we can eyeball global cohesion and catch
drift in one view. Include a short written "how to extend this system" note: how to add a new
token, a new component variant, and a new page that stays cohesive — aimed at whoever builds
the next tool.

Finally, summarise the delivered system: the token contract, the component inventory with
the class name for each, and the page-archetype catalogue, as a short index we can commit
alongside the CSS.
```

---

## Part 3 — After the hand-off (for the porting session)

> The hand-off came back and the port is under way on the `design/bc-system`
> branch. **`design-migration.md` is the live plan** — measurements, the open
> decisions, and the PR order. `handoff/` holds the imported files. Read those
> two before the sketch below, which is the original guess at landing order.

When these come back as HTML/CSS, porting to Blazor follows `CLAUDE.md` →
"Implementing a Claude Design handoff": translate structure into our components, port the
component CSS near-verbatim onto the tokens, diff against the rendered prototype cell by
cell, and treat any data-driven omission as a flag. Suggested landing order:

1. `tokens.css` → replace the `:root` block in `wwwroot/base.css` (keep the aliases so
   existing CSS keeps resolving during the migration).
2. Core components → a small set of shared `.razor.css` + global rules; retire the
   duplicated per-page versions as each page is migrated.
3. Shell → `MainLayout` / `NavMenu` / `ThemeToggle` / `ReconnectModal`.
4. Archetypes, one tool at a time, deleting that tool's bespoke slice of `tools.css` as it
   moves onto the shared components. Track the `tools.css` line count down as the health metric.

Migrate tool-by-tool, not big-bang — each PR is one tool moved onto the system, verifiable
in isolation, with the old classes deleted in the same PR so the two never drift again.
