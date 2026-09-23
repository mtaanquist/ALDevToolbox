# Brief: the command palette (2026-09)

For the Claude Design project **AL Dev Toolbox Design System**
(`63d872d4-c751-4420-b910-cb7eec63e4c3`). Companion to
`briefs/2026-09-shipped-without-a-sheet.md`, and written for the same reason:
the palette shipped with no sheet, so this is what it looks like today, drawn
in the tokens it already uses, so a design pass can settle it rather than
reconstruct it.

The behaviour is specified in the app repo's `.design/command-palette.md`.
Nothing below asks for that to change; it asks for a sheet.

## What it is

One overlay, opened with Ctrl+K (Cmd+K on macOS) or from a new control in the
top bar. It has one input and one list. Typing narrows the list; Enter opens
the highlighted row and navigates. **It is navigation only - no row ever writes
anything**, which is why nothing in it is destructive, confirmable, or primary.

It is *not* a modal task dialog, and it should not read as one: no title bar,
no footer buttons, no primary action. The closest thing in the system today is
`.modal-layer` + `.modal-backdrop`, which it borrows for the scrim.

## The control in the top bar (new, no counterpart in `shell.css`)

Lives in `.app__top-lead`, after the drawer toggle. Styled in the app's
`MainLayout.razor.css` because `shell.css` is byte-locked.

```
<button class="cmdp-open">
  <span class="cmdp-open__icon">    search glyph, --ink-4
  <span class="cmdp-open__label">   "Search or jump to..."   --ink-3
  <span class="cmdp-open__keys">    <kbd class="kbd">Ctrl</kbd><kbd class="kbd">K</kbd>
  <span class="u-sr-only">          ", Ctrl K"  (the accessible name's second half)
```

- Drawn as the search field it stands in for: `--input-bg`, 1px
  `--input-border`, `--r-control`, `--control-h`, `width: min(320px, 100%)`.
  Hover takes `--surface-sunken` + `--input-border-hover`.
- The key caps are `.kbd` from `pages-power.css`, pushed right with
  `margin-left: auto`.
- The modifier is rewritten to `Cmd` by script on a Mac; the server cannot know.
- **Responsive**, against `.shell-root`'s container queries:
  - `<= 1080px` (rail / tablet): keys hidden, width auto.
  - `<= 700px` (phone): magnifier only, `--control-h` square, transparent
    border. The label is *clipped* rather than removed so the button keeps its
    name. **At this width it is the only way into the palette** - there is no
    hotkey - so it must stay unmistakably tappable.

**For the design pass:** the system has no "search entry" control. This one was
invented. Decide whether it belongs in `components.css` as a real component
(a search-affordance button) and what it does at each shell step.

## The overlay

```
.cmdp-layer  (= .modal-layer + align-items: start; padding-top: 12vh)
  .modal-backdrop
  .cmdp                     panel: --surface, 1px --border-strong, --r,
                            --shadow-lg, width min(620px, 100%),
                            max-height min(560px, 70vh)
    .cmdp__head             flex row, --space-3 gap, 0 --space-4, 1px --border bottom
      .cmdp__head-icon      search glyph 17px, --ink-4
      .cmdp__input          borderless, --control-h-lg, --text-md, --ink;
                            placeholder --ink-3
      .cmdp__close          button; .cmdp__close-key (a .kbd reading "Esc") on
                            a keyboard, .cmdp__close-x (an X glyph) below 560px
    .cmdp__list             scrolls, --space-2 0
    #cmdp-live              visually hidden; the list's spoken half
    .cmdp__foot             --surface-2, 1px --border top, --text-2xs, --ink-3
```

The panel sits high (12vh) rather than centred: the list grows downwards into
empty space instead of pushing the input around.

**Focus.** The input is focused on open and suppresses its own ring; the ring is
drawn on the panel instead (`.cmdp:has(.cmdp__input:focus-visible)` →
`--focus-ring`). Tab moves to Close, which takes the same ring, and back. That
pair is the whole focus order.

## The result row

```
.cmdp-row                   flex, --space-3 gap, --space-2 --space-4,
                            2px transparent left border, --ink, no underline
  .cmdp-row__icon           16px, --ink-3
  .cmdp-row__text           baseline row, --space-2 gap
    .cmdp-row__title        --text-base, --ink, ellipsised
    .cmdp-row__sub          --text-xs, --ink-3, ellipsised
```

- Title and subtitle are on **one baseline row** above 560px and stack below it.
- **Selected** (`[aria-selected="true"]`): `--primary-weak` fill, left border
  `--primary`, icon `--primary-ink`. Exactly one row is ever lit - the pointer
  *moves* the selection rather than drawing a hover state of its own, so the row
  under the mouse and the row Enter would open are never two different rows.
  There is deliberately no `:hover` style.
- Rows are real `<a>` elements (middle-click and Ctrl+click work) with
  `role="option"` and `tabindex="-1"`.
- Six row variants exist, differing only in the icon: solution
  (`folder-git-2`), environment (`server`), release (`send`), recipe
  (`square-code`), goto (`arrow-right`), default (`box`).

**For the design pass, one measured problem:** the selection keyline is
`--primary`, which is **2.45:1 against `--surface` in light** (8.59:1 in dark).
WCAG 1.4.11 wants 3:1 for a state indicator. This is *not* a palette bug - it is
the system's own selection keyline, the same one in `.nav-item.is-active::before`,
`.data-table tr.is-selected` and `.run-row.is-selected` - so it was left alone
rather than diverged from in one sheet. It wants deciding once, upstream, for
all of them. The fill behind it (`--primary-weak`, 1.11:1) cannot carry the
state on its own.

## The group header

```
.cmdp__group-wrap  role="group" aria-label="<the label>"   (structural only)
  .cmdp__group     --text-2xs, --fw-semibold, .04em, uppercase, --ink-3,
                   --space-2 --space-4 --space-1
```

Groups are fixed in order - **Best match, Solutions, Environments, Releases,
Recipes, Recent, Go to** - so rows do not shuffle against each other while
someone types. "Best match" is a group of one, lifted above everything.

The heading was `--ink-4` and is now `--ink-3`: at 11px uppercase it is small
text, and `--ink-4` measures 3.89:1 (light) / 4.23:1 (dark), under 4.5:1.
`--ink-3` is 5.85:1 / 6.57:1. Same change in `.cmdp__foot` (3.75 / 3.97 →
5.64 / 6.16) and on the placeholder.

**For the design pass:** the wrapper exists for semantics, not layout. If the
system gives groups a real treatment (a rule, an inset, a sticky header while
scrolling), that is the place.

## The states

The list pane always shows exactly one of these, never two:

1. **Empty query** - Recent (when there is one; not built yet) then the whole
   "Go to" list, uncapped. This is the app's page index, and it is meant to be
   browsable.
2. **Results** - the groups above, five rows per group, eight when only one
   group has any.
3. **No results** - `.cmdp__state` block: `.cmdp__state-title` reads
   *No search results for "..."*, `.cmdp__state-text` reads *Check the spelling,
   or go to a page instead.* One "Go to" row - the closest page - is still
   offered underneath, which is why the sentence says "no search results"
   rather than "nothing matches".
4. **Unavailable** - same block: *Search is not available right now.* /
   *You can still go to any page below. Try searching again in a moment.* The
   "Go to" list is intact, because it was rendered server-side and never came
   off the wire. States 3 and 4 never stack.

```
.cmdp__state        --space-4 --space-4 --space-3
.cmdp__state-title  --text-base, --ink
.cmdp__state-text   --text-sm, --ink-3
```

**For the design pass:** these are the system's only "empty state inside an
overlay". `EmptyState` is a page primitive and is far too tall for a 560px
panel, which is why this is bespoke. Decide whether it should be a small
variant of `EmptyState` instead.

## The foot

```
.cmdp__foot
  .kbd-hint  ↑ ↓ "to move"      .kbd + .kbd-hint__label  (pages-power.css)
  .kbd-hint  Enter "to open"
  .kbd-hint  Esc "to close"
  .cmdp__foot-link  "How search works" -> /docs/search, --primary-ink,
                    margin-left: auto
```

Hidden entirely below 560px, where there is no keyboard to hint at - which is
also why the docs page is in the "Go to" list and not only here.

At most one non-hint item in the foot, by rule.

## Light and dark

Everything is a token, so both themes come out of the same rules. The two
places worth drawing in both: the selected row (`--primary-weak` reads as a
tinted band in light, a deep teal in dark) and the panel's focus ring
(`--focus-color` is `#008089` in light and `#5AD8E2` in dark, so the ring is
noticeably brighter on dark).

One inherited correction applies here too: `.modal-backdrop` in `components.css`
mixes from `--ink`, which flips with the theme, so the dark scrim comes out
nearly white. The app overrides it in `app.css`. The palette's scrim is that
same backdrop, so a sheet drawn from the unfixed `components.css` will show the
wrong scrim in dark.

## Phone (<= 560px)

```
.cmdp-layer  padding --space-3, padding-top --space-5
.cmdp        max-height 80vh
.cmdp__foot  display: none
.cmdp__close-key hidden; .cmdp__close-x shown
.cmdp-row__text  stacks (column, gap 0)
```

The palette is effectively the screen: the scrim has no room to say "something
is behind this", and the on-screen keyboard takes the bottom half. The X is the
way out, because there is no Esc key.

## What a design pass should settle

1. A real **search-entry control** for the top bar, with its own responsive
   steps - or a ruling that this one is right as drawn.
2. The **selection keyline contrast** (`--primary`, 2.45:1 in light), for the
   whole system rather than for the palette.
3. Whether a **group header** gets a treatment of its own, and whether it
   should stick while the list scrolls.
4. Whether the two **overlay empty states** become a small `EmptyState`
   variant.
5. The **panel focus ring**: drawing the ring on the panel because the input is
   borderless is a pattern the system does not otherwise have.
6. Whether **Recent** (not built yet - app repo issue #887) gets anything other
   than a group header, e.g. a per-row affordance to forget one.
7. **Whether a scrolling list says so.** With nothing typed the list is ~1450px
   tall in a 483px pane, and the last visible row ends flush against the foot on
   a desktop with overlay scrollbars. A fresh-eyes review read that as "the
   palette only covers some of the pages". A half-clipped last row or a fade at
   the bottom edge would fix it, but both are new visual language and belong
   here rather than in one app sheet.
8. **Whether a matched row can show *why* it is there.** Typing `te` returns
   "Defaults", "Always-included files" and "Workspace settings" - all matched on
   their grey subtitle ("Templates"), none on the title, so the list reads as
   guessing. Emphasising the matched characters would answer it. It is left out
   for now because the script deliberately writes no HTML and only fills
   `textContent`, so highlighting needs a row treatment the system designs
   rather than a string the script builds.

## One naming question left open

A fresh-eyes review made the case that **"Go to" is the weak group label**: it
is a verb phrase in a column of nouns (Solutions, Environments, Releases,
Recipes), and the palette's own docs page had to translate it. "Pages" was
proposed. It was **not** changed - "Go to" is a decision recorded in the app's
`.design/command-palette.md`, made with the maintainer, and renaming a decided
label out of a review is not this issue's job. It is written down here because
if the design pass agrees, this is the moment to change it in both places at
once.
