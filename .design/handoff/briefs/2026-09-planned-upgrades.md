# Planned upgrades: a header with lines, a picker, and an archive (#984)

Requested by the maintainer on 2026-09-25 (repository issue #984), agreed on 2026-09-26.
Three prompts for the design project, meant to be given one at a time in this order: each
sheet builds on the previous one. The words are the project's: **Upgrade** for the batch,
**Solution** for the customer, **environment** for a line. `PageUpgrades.dc.html`
(archetype 15, the flat fleet table) is the page all three change; keep its command bar,
row menu and state column as they are, because the app already ports them.
`briefs/assets/upgrades-current.png` is the page as the app renders it today.

Named user throughout: an upgrade team member who has agreed the same evening slot with
eight customers, wants to start them all at 20:00, and the next morning wants a list of
exactly those eight with a tick beside each one they have checked. With fifty or more
environments in flight, the flat list cannot say which rows belong to today's sweep.

---

## Prompt 1: the Upgrades list, with its Archive and Fleet views

> The Upgrades page (`PageUpgrades.dc.html`, archetype 15) becomes the home of *planned
> upgrades*, and the flat fleet table it holds today moves to a second view of the same
> page. Redraw `PageUpgrades.dc.html` as a list page (`PageList.dc.html` construction)
> with three views in its command bar, the way the Environments list has its Deleted view:
>
> - **Open** (default): every upgrade that is not done.
> - **Archive**: done upgrades, read-only, searchable by name and target version. Always
>   present, since it is the record.
> - **Fleet**: the existing flat table of every environment, unchanged, for ad hoc work and
>   as the source the picker draws from. This view keeps the row menu and the three
>   selection commands (Move dates, Start update..., Change the next version...) and gains
>   a fourth selection command, **Add to upgrade...** (Prompt 3 draws its dialog).
>
> Route: `/upgrades`. Title "Upgrades". Subtitle changes with the numbers ("2 open, 11
> environments running tonight." / "Nothing planned."). Head action: primary **New
> upgrade**, opening a small dialog with Name (placeholder "28.5 in November 2026"), Target
> version (the version picker the fleet table already uses), Planned slot (optional date
> and time), Note. This is the only primary button on the page.
>
> An upgrade is a named wave: a name, a target version, an optional planned slot, a note,
> who made it and when, and a status derived from its lines: **Planned** (nothing sent),
> **In progress** (something booked or running), **Updated** (every line updated or
> failed, checks outstanding), **Done** (closed by a maintainer).
>
> Columns of the Open view: Upgrade (name, then "to 28.5" and the planned slot in the
> sub-line), Status (state pill), Environments (a count and the by-state breakdown "3
> running, 4 updated, 1 failed"), Checked ("2 of 8", with a small progress bar), Planned
> (the slot as a date, relative when near), Created by. The breakdown is what the morning
> after reads from the list without opening each upgrade, so give it room. The row links
> to the upgrade page. Row menu: Open, Mark done..., Delete (danger, only while nothing
> has been sent).
>
> Archive view columns: Upgrade, Target, Environments (with failed count if any), Closed
> (when, by whom), Created by. Row menu: Open, Reopen.
>
> Draw desktop and phone, in loading, empty and populated states for the Open view, and
> populated for Archive. The Open view's empty state says nothing is planned yet and holds
> the **New upgrade** button; the Archive's says no upgrade has been marked done yet. Add a
> `PageUpgradesList` section beside the existing sheet if you would rather keep the fleet
> table's sheet as it is and draw the list separately; either way, say which file owns
> what.

---

## Prompt 2: the upgrade page

> Draw `PageUpgrade.dc.html`: one planned upgrade at `/upgrades/{id}`, on the entity-detail
> archetype (`PageDetail.dc.html`: crumbs above the head, the state pill beside the name,
> the facts in a meta row, then the body). The same user as the list page, now working
> through tonight's wave and checking it tomorrow morning.
>
> **Head.** Crumbs "Upgrades > 28.5 in November 2026". Title is the name; the state pill
> (Planned / In progress / Updated / Done) sits beside it. Meta row: Target 28.5, Planned
> Thu 12 Nov 20:00, 8 environments, "5 of 8 updated, 3 checked", Created by Mads on 3 Nov,
> and, on a done upgrade, Closed by and when.
>
> **Head actions, a ribbon.** One primary that changes with the state, three outline
> actions that run over every line, and an overflow menu:
>
> | State | Primary |
> | --- | --- |
> | No lines yet | Add environments... |
> | Planned, lines present | Start update... |
> | Updated, or every line checked | Mark done... |
> | Done | none; the ribbon is read-only apart from Reopen in the overflow |
>
> Outline: **Add environments...** (when not primary), **Move dates...**, **Start
> update...**, **Change the next version...**. These are the fleet table's three actions
> run over all lines, with the same preview, typed confirmation and per-row result list as
> the fleet's dialogs, pre-filled from the header (its target version, its planned slot).
> Overflow: Edit details, Mark done..., Reopen, New upgrade from the leftovers, Delete.
> Draw the Mark done confirm for the case where lines are still unchecked ("3 of 8 are not
> checked yet. Mark done anyway?").
>
> **Body: the lines.** A table of the environments on the upgrade. This is the check list
> the morning-after user works from, so it is an editable power-user grid, not a read-only
> list. Columns:
>
> - Environment: solution name, then environment name and type (Production / Sandbox) in
>   the sub-line, linking to the environment's own page.
> - Now: the live mirror state (current version, the booked or running update) as the
>   fleet table shows it.
> - State: one derived word as a pill: Planned, Date moved, Booked, Running, Updated,
>   Failed, Checked. Choose tones so Failed and Checked read at a glance from across the
>   table.
> - Last action: what was last done from this upgrade and when ("Update booked for 20:00,
>   by Mads, 2 h ago").
> - Assignee: an avatar-and-name cell, blank by default, set from a small picker in the
>   cell.
> - Checked: a checkbox in the cell; once ticked, who and when beneath it, and a short note
>   inline ("posting OK, reports OK"). Ticking stamps the current user.
> - Contact: the solution's customer contact (name and phone), since a check often ends in
>   a call. Reuse the contact rendering from the Solutions list rail.
>
> Above the table a small command bar: search, a **Mine** toggle (lines assigned to me),
> a state filter, and a selection that allows the row menu's actions on a subset (Move
> dates, Start update, Change the next version, Assign to..., Remove from upgrade, the last
> only while nothing has been sent for that line). On a done upgrade the table is
> read-only: no checkboxes, no menu, no selection, so the archive shows exactly what the
> wave ran over.
>
> Draw desktop and phone, in loading, not found, empty (a fresh upgrade with no lines:
> "No environments yet" with the Add environments primary), populated-open (mixed states,
> one failed, three checked) and populated-done. On the phone, decide which columns
> collapse into the Environment cell's sub-line; State and Checked must survive.

---

## Prompt 3: the environment picker, "Add to upgrade", and the Dashboard tile

> Three smaller pieces that complete the tool.
>
> **The environment picker.** A dialog opened from **Add environments...** on the upgrade
> page. It is the Fleet view's table inside a large dialog: the same search and filters as
> the Environments list (solution, short name, environment name, type, view), a checkbox
> column, and a footer with "4 selected", a **Show selected** toggle, Cancel and the
> primary **Add 4 environments**. The selection must survive searching and filtering: tick
> three, search for a fourth, tick it, and all four stay ticked. Environments already on
> this upgrade are ticked and disabled; environments on *another* open upgrade are
> disabled and say which one in the sub-line ("On 28.5 in October"), since an environment
> can be on only one open upgrade at a time. Two fill buttons above the table start a
> selection in one click: **Production not yet on 28.5** and **Sandboxes not yet on 28.5**
> (the version comes from the header). Draw the dialog empty-selection, mid-selection with
> a search active, and with Show selected on. Phone: the dialog goes full-screen.
>
> **Add to upgrade...** A selection command on the Fleet view (Prompt 1). Its dialog is
> the reverse of the picker: the ticked environments are listed, and the user picks an open
> upgrade from a list (name, target, planned slot, line count) or chooses **New upgrade**,
> which asks for the name and target inline. Environments already on another open upgrade
> are listed with a warning and left out of the count on the primary button ("Add 6 of 8").
>
> **Dashboard tile.** On `PageDashboard.dc.html`, one cue tile **Open upgrades**: the count
> of open upgrades, foot "11 running, 1 failed" in the danger tone when failed is above
> zero, drilling to `/upgrades`. Nothing else on the dashboard changes.
>
> **Command palette.** No sheet needed: upgrades appear as a kind in the existing palette
> results (name, target, state pill), the way solutions and environments already do. Note
> it in the palette's section of `Components.dc.html` if that section lists the kinds.

---

## What a design pass should settle

- Whether the derived line state and the live mirror state are two columns or one cell
  with two lines; the brief above draws two, since the state word is what the checker
  scans for.
- Whether a check is one tick or a short fixed list (signed in, posted, printed). Start
  with one tick and a note; leave room for a list.
- The by-state breakdown on the list row: words, or small tone-coloured counts.
- The lines table on a done upgrade: dimmed, or plainly read-only with the controls gone.

## Later wishes, out of scope now, leave room

Notifications to customers before and after their upgrade (the contact column is the
seed); export of the check list; booking a whole upgrade for a slot straight from the
header. Do not draw any of these.
