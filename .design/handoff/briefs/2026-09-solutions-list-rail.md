# Addendum to "shipped without a sheet": the Solutions list with its rail (#906)

This extends the **list-with-rail** entry in the design project's
`briefs/2026-09-shipped-without-a-sheet.md`. It is checked in here first because it was
written in a session with no `DesignSync` access; fold it into that brief next time one is
open, then delete this file. The behaviour is specified in `.design/solution-customer-info.md`
("The Solutions list and its customer info").

## What changed since the entry was written

**The row is the selector.** The per-row **Customer info** and **Open** buttons are gone.
Each row's first cell holds an empty link (`.sol-list__select`, to `?selected={id}`) whose
`::after` is absolutely positioned over the whole row (`tr.sol-list__row` is
`position: relative`). The Solution name (`.sol-list__name`) and the Last shipped date
(`.sol-list__shipped`) sit above it at `z-index: 1` and keep their own destinations - two
sibling links, never nested. The cover has no box, so its keyboard focus ring is drawn as an
inset `--focus-color` shadow on the `::after`, i.e. round the whole row. Hover is the
existing `.data-table tbody tr:hover` fill; selection is the existing `tr.is-selected` fill
and inset keyline.

**The rail is reserved.** It used to appear only while a row was selected, so the table
changed width on every click. It is now always there beside a populated list, holding an
`.empty-state` in a `.card` ("Choose a solution to see its customer info") until a row is
chosen.

**New columns.** Solution (name, then the short name in `--ink-3` at `--text-sm`), Hosted
by, BC version, Last shipped, Visibility. The table dropped `.data-table--edge` and its
state column: the list no longer shows build status. Last shipped is a date link; a
handed-off delivery (accepted by Business Central, installing in a later window) keeps the
link colour on the date, with a 12px `send` glyph before it and "installs later" after it,
both in `--ink-3`.

## What a design pass should settle

- Whether a whole-row link wants its own hover affordance beyond the row fill (a chevron
  at the end of the row, or the name underlining on row hover), since the pointer is the
  only sign today that the row does something different from the name.
- The row-focus ring: an inset `--focus-color` ring on a table row is new to the system.
- The handed-off treatment: a glyph plus a quiet trailing phrase is a first use; a sheet may
  prefer a status word or a `--bar-*` token.
- The rail's empty state at 280px: the standard `.empty-state` is sized for a page body.
- Below the width where `.detail-body` collapses, the rail drops under the list, so with a
  long list choosing a row changes nothing on screen. The address carries no fragment on
  purpose (a jump scrolls the page head away on wide screens); a sheet should say what the
  narrow layout does instead.
- The rail's first card repeats Hosted by and BC version from the row beside it, which
  pushes Getting in and Contacts - what a support call wants - further down.
