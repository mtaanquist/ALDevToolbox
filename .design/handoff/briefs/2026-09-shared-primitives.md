# Brief: the shared primitives, and what they decided (2026-09)

For the Claude Design project **AL Dev Toolbox Design System**
(`63d872d4-c751-4420-b910-cb7eec63e4c3`). Companion to
`briefs/2026-09-port-corrections.md`.

The app now renders five of the sheet's small pieces through one component
each instead of hand-writing them on every page (app repo issue #842). A
component has to decide things the sheet leaves to the author - which glyph,
which ARIA role, whether the dot is there - and this records those decisions
so the sheet can confirm or overrule them. None of the CSS changed; the
`.design/handoff/` copies are still byte-identical to this project.

## Crumb trail (`.crumbs`)

A list of steps, each a label with an optional link; the last is never linked.
The chevron between steps is `chevron-right` at **12px** - pages had it at 12
and 14 - and the sheet shows neither size explicitly. Trails that depend on
data still loading stay hand-written and are listed by name in a test.

## Alert (`.alert`)

Tone picks the glyph and the role, once:

| Tone | Glyph | Role |
| --- | --- | --- |
| Danger | `circle-alert` (16px) | `alert` |
| Warn | `triangle-alert` | `status` |
| Success | `circle-check` | `status` |
| Info | `info` | none |
| Neutral | none | none |

Before this, ten Danger alerts carried the warning triangle. A `Block`
variant renders as a `div` for alerts that hold more than a sentence.

## Status pill (`.status-pill`)

Takes the sheet's own tone words (muted, success, warn, danger, info, queued,
running, live, and the translation states). **The dot is always in the
markup**; the sheet's CSS shows it only on `live` and `running`, where it
pulses. Pages that wrote the pill by hand left the dot out about half the
time, which only showed once a computed tone happened to be running.

## Select (`.select-wrap`)

Wraps a `.select` and always draws the `chevron-down` caret at 15px. `.select`
sets `appearance: none`, so a forgotten caret left a select that did not look
like one; the wrapper makes that impossible.

## Inline error line (`.field-error`)

One component for both a field-keyed validation message and a free-standing
error ("Couldn't save", a read that failed). Glyph is the sheet's
**`triangle-alert` at 13px**, role is always `alert`. Twenty-eight
hand-written lines were split fifteen circle, thirteen triangle, and ten had
no role. Rendered today (Administration - Business Central, invalid client ID):

![field-error as rendered](assets/field-error-current.png)

Note the asymmetry with Alert: a Danger *alert* carries the circle, a
*field error* carries the triangle. That follows the sheet
(`ComponentsPanel.dc.html` draws them that way) and the components keep it;
if the sheet would rather one glyph mean "error" everywhere, that is a
one-line change in each component.

## What a design pass should settle

- The crumb chevron size (12px is now the only one).
- Whether Danger alerts and field errors should share a glyph.
- The freshness strip on `/environments` and `/environments/{id}` was left
  hand-written: the two share four lines of shell around bodies that differ
  entirely (one sentence and a Refresh button on the list; up to four dated
  sentences and no button on the detail). If the sheet wants one shape, that
  is the place to draw it.
