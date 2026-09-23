# Release pipelines: review of the first two sheets

Sheets reviewed: `PageReleasePipelines.dc.html` / `ReleasePipelinesBody.dc.html`,
`PageReleasePipeline.dc.html` / `ReleasePipelineBody.dc.html`, and `DeliveryRowPanel.dc.html`.
Reviewed against the code on 2026-09-23 (v11.12.1). The design is authoritative; this brief
lists the places where it states something the product cannot know or do, with the fact to
draw instead, and the places where the data does not exist yet but is cheap to start
recording, so the drawing can stay.

## Keep as drawn

- Every CSS class the three sheets use exists in the ported sheets. No new tokens, no new
  colours, nothing to port in `components.css` or `pages*.css`.
- Delivery states: scheduled, claimed, uploading, installing, deployed, failed, cancelled,
  handed to Business Central. That is the engine's exact list. Per-app states: pending,
  uploading, installing, scheduled, completed, failed, skipped. Also exact.
- The diagnostics log. The engine writes it as `HH:mm:ss  message` lines already, so the
  two-column mono log with error lines tinted is a straight port.
- Row actions: Reschedule and Cancel on a scheduled release exist; "Release again" on a
  failed one is issue #931; the "Waiting for approval" slot is issue #934. Both are drawn
  the way the issues describe them.
- The Force sync acknowledgement wording matches the pipeline editor's; the Production
  acknowledgement matches the Release dialog's.
- Environment health (Ready / Being deleted / Failed / Missing), the source kinds (build
  pipeline / GitHub release), the environment's Business Central version, and the date and
  version of the next Business Central update (for a "next major update" pipeline) are all
  stored and can fill the cells drawn for them.
- The list page's filter bar, pill tabs, search and both empty states have frames and
  primitives in the app already.
- The "Shipping now" band, the tinted row and the Delivery cell: the per-app results give
  "app 2 of 3" and a done/total progress, and the previous release's duration is stored.

## Adapt: the product does not do this

1. **Releases do not start on their own.** "On every new build", "On every new release",
   "Scheduled ... by a new build", and the empty state's "The next build from X installs
   right away" all describe automatic release on build, which does not exist and is not
   planned; #934 proposes a *prepared* release a person approves. Next release when nothing
   is scheduled: "None scheduled". The scheduled step: "by K. Jensen" or "by an agent".
   Empty state: "Nothing has been installed by this pipeline yet. Release the latest build
   to install it now."
2. **Builds have no version number.** A build is "#118", on a branch, with a commit; the
   versions belong to the apps in it. "Build 2.3.0.118 from main" becomes "Build #118 from
   main", and the versions stay on the app rows, where they are drawn already.
3. **Business Central's error is not translated.** "Business Central answered in Danish
   ... Translated for you" cannot be honoured. Keep the line "Business Central answered in
   the environment's language" and show its message as given. The structured sentence
   ("removes field 12 'Zone Priority' from table 50110") also cannot be parsed out of that
   prose reliably; what #930 will produce is a sentence from the error code
   ("Business Central refused a schema change") plus the message verbatim, plus the next
   step. Draw that.
4. **Uploads have no byte progress.** "Uploading 3 apps, 2.4 of 4.1 MB" becomes
   "Uploading app 2 of 3" with an indeterminate bar during the upload and a determinate
   apps-done bar for the release as a whole.
5. **"Delivery window 22:00-04:00" is ours, not Business Central's.** It sits in the
   "From Business Central" block, but it is the window stored on the environment by the
   person who set it up. Move it to "This pipeline" (or a third, unlabelled row), and keep
   the Business Central block for what is read live: type, health, version, and Microsoft's
   own update window if you want it there.
6. **"Nothing is broken for users" is a promise the product cannot make.** Keep the facts
   the health sentence has: the last release failed on app X, and the environment reports
   version Y of it installed. Drop the reassurance.
7. **"Showing 5 of 49 releases" and a second "All releases" inside the card.** "All
   releases" in the head goes to the list of release pipelines (the nav calls that page
   Releases). There is no separate full list of one pipeline's releases. Make the card foot
   "Show older releases" that extends the list in place, and keep the head button as is.
8. **"Paused while the environment is deleted".** Nothing pauses; releases are refused
   while the environment is being deleted. Say that.
9. **"Times are your local time."** Pages render on the server and the app's convention is
   relative times with the exact time in a tooltip, and UTC where a date is printed
   (the Upgrades page says so in its subtitle). Drop the claim or adopt the convention.
10. **Breadcrumb.** The app's trail is "Releases › {pipeline}", two crumbs, not three.

## Adapt for now, worth recording so the drawing can come back

These are drawn from data the engine does not store yet. Each is a small addition to the
delivery tables, and each is worth it; they are added to issue #929 as its scope.

- **Per-app timings** ("took 0m 48s" on an app row): results carry no timestamps. Record
  started and finished per app.
- **Version change** ("2.2.0.104 to 2.3.0.118"): the previous version is not stored. The
  run lists the installed apps before it starts, so the previous version can be recorded
  on each result at that moment.
- **The "Uploaded" step** in the step strip: the release has scheduled, claimed, started
  and finished times, but not the moment upload ended and install began. Record it, or
  draw four steps until it exists.
- **"Cancelled by K. Jensen"**: cancelling records nobody. Record who cancelled.
- **"Release 49"** as a per-pipeline number: a release has a global id. A per-pipeline
  sequence can be computed for display; no schema change.

Until those land, the port hides the cell rather than inventing a value.

## Inconsistent inside the design

- Production is `--warning-text` semibold on the list and a `status-pill--danger` on the
  detail page. The app already uses the danger tone for Production everywhere a type is
  shown; the list should follow the detail page.
- The "handed to Business Central" glyph is `--ink-2` on the detail page and `--ink-3` on
  the list.
- `DeliveryRowPanel.dc.html` carries a real customer's app and publisher names, lifted from
  the screenshot in the brief. Replace them with CRONUS / Contoso before the sheet is
  shared further.

## Port notes

- List page: `ListPage` frame with its Search, Filters and Trailing slots and the
  `PillTabs` primitive; the table is `data-table--edge`. Sorting by urgency is a fixed
  server order (shipping now, needs attention, scheduled, rest); the header sort buttons
  can wait.
- Detail page: `DetailPage` frame; the summary card is a `card` with `meta-item`s; the
  release rows are a `run-list` with an expand toggle, not a table.
- Copy buttons ("Copy log", "Copy for support") need a small `.razor.js` for the clipboard;
  that is within the rules.
- The "Shipping now" band polls on the same two-second cadence the detail page already
  uses while a release is claimed, uploading or installing.
