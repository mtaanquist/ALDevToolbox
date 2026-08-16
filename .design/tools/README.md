# Migration audit tools

These live in the repo rather than `scratch/` because `.design/design-migration.md`
tells you to run them, and `/scratch/` is gitignored — the doc used to point at
files that no session but the one that wrote them could see.

They exist because of one lesson from the PR 8 audit: **screenshot comparison
cannot find a uniformly wrong value.** A card with a 3px radius and a soft drop
shadow looks fine next to our own earlier screenshots; it only reads as wrong
against the hand-off, or against the number the token says. Everything below
measures rather than looks.

## Static — no app needed

| | |
| --- | --- |
| `collisions.py` | Which design-layer declarations a later legacy sheet beats. Parses all nine sheets in `App.razor` load order. Skips `.page`-gated rules — that is the bridge, which is the fix, not a collision. |
| `dead-css.py` | Rules a sheet still defines that nothing applies any more. A class counts as live if it appears in any `.razor`, `.cs`, `.js` or scoped `.css` — a lot of `tools.css` is applied by CodeMirror, not by markup. |
| `audit-pr8.py` | The mirror: classes the markup applies that no sheet defines. Takes a git base (default the PR 8 range). |
| `retire-css.py` | Delete dead CSS **by rule, never by range.** Written after "delete from rule A to rule B" removed 896 unrelated lines from `tools.css` because the two markers were nowhere near each other. Walks brace by brace, drops a rule only when every class in its selector is dead, and refuses to write if braces end unbalanced. |

## Rendered — headless browser, no app needed

| | |
| --- | --- |
| `cascade-probe.mjs` | Asks a real browser what a component actually computes to, instead of guessing at specificity. Wrap markup in `.page` or you measure an unmigrated page by accident. |
| `measure-handoff.mjs` | Renders `handoff/ComponentsPanel.dc.html` under the hand-off's own sheets. This is what the design project sees. |
| `compare-to-handoff.mjs` | Same markup, two stacks — hand-off vs ours. The one to reach for when asking "how far off are we, and in what". |

## Live — needs the app running

| | |
| --- | --- |
| `verify-root-font.mjs` | Every `--text-*` token against its documented pixel size. Caught #544 (the whole scale at 87.5%). |
| `run-together.mjs` | The Razor swallowed-space bug, measured geometrically. **Exports `selfTest`, which plants the bug and asserts the detector fires — call it.** The first version of this returned clean on a page that definitely had the bug. |
| `sweep-stretch.mjs` | Walks every route asserting nothing collapsed or overflowed. |
| `probe-root-fix.mjs` | Blast radius of a proposed global change: computed styles per element, before and after, per route. |

## Rough edges

The `.mjs` files hardcode two absolute paths — the Playwright browser binary and
a `playwright-core` install borrowed from a sibling checkout — plus
`http://localhost:5246` and the local bootstrap credentials. Fine on the machine
they were written on, a two-line edit anywhere else. See
[[local-verify-setup]] equivalents in `design-migration.md` for how the app is
started.
