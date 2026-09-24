# Pipelines dashboard: a new sheet on the dashboard archetype

Requested by the maintainer on 2026-09-23 (repository issue #955), after the pipeline
vocabulary change in `briefs/2026-09-deployments-vocabulary.md`. Apply that brief first if it
is still open; this sheet uses its words throughout (Builds, Deployments, Deploy).

## What it is

The Deliver group in the sidebar now has a **Pipelines** parent with two children, **Builds**
and **Deployments**. Today the parent is only an expander. This sheet gives it a page of its
own, `/pipelines`, shaped like the Admin dashboard (`PageDashboard.dc.html`: cue tiles in a
`cue-grid`, then `dash-cols` with "Needs attention" and "Recent activity" cards) but about
builds and deployments only. Nothing else on the page: no settings, no charts.

Named user: an ops engineer opening the workbench in the morning who wants one page that says
what built, what shipped, what failed and what is waiting, before drilling into either list.

## Files

- `PagePipelines.dc.html`: the page inside the shell, desktop and phone, in the three states
  below. Same construction as `PageDashboard.dc.html`.
- Add its sections to `Components.dc.html` only if you draw a component the dashboard sheet
  does not already have; the tiles, the activity rows and the attention rows exist there.
- `ShellFrame.dc.html`: the Pipelines parent becomes a link to `/pipelines`, selected when
  the page is open, with its children still listed under it. No other nav change.

## Page head

Title "Pipelines". Subtitle: one sentence that changes with the numbers, like the Admin
dashboard's ("3 builds and 1 deployment today, 2 waiting for approval." / "Quiet since
yesterday."). Head actions: outline "New build pipeline" and outline "New deployment
pipeline". Neither is primary on the populated page; the primary button belongs to the
empty state.

## Tiles (cue-grid, each a link into the list it names)

1. **Build pipelines** - count; foot: "Last run 2 h ago"; drill: "All builds".
2. **Builds this week** - count; foot: "Last one 2 h ago on main".
3. **Failed builds** - count; danger tone (`cue--attention`) when above zero, foot "Needs
   attention"; quiet tone and foot "None" at zero.
4. **Deployment pipelines** - count; foot: "Last deployment yesterday".
5. **Shipping now** - count of deployments running this moment; foot "App 2 of 3 to CRONUS
   - Production" while one runs, "Nothing shipping" otherwise.
6. **Failed deployments** - count; danger tone above zero, like tile 3.
7. **Waiting for approval** - count of prepared deployments; warning tone above zero, foot
   "Oldest 3 d"; quiet at zero.

Seven tiles do not fill a row evenly: lay them out as the grid wraps at desktop, and check
the phone column reads in this order.

## Needs attention (card, left column)

A list with keyline tones, newest first, each row a link:

- Failed build: "Build #118 failed on CRONUS Base - main", relative time, danger.
- Failed deployment: "Deployment to CRONUS - Production failed on CRONUS Sales", danger.
- Waiting for approval: "Build #119 is ready to deploy to CRONUS - Sandbox", warning,
  with an inline outline "Review" button that opens the deployment pipeline page.
- Environment gone: "CRONUS - Sandbox is being deleted. Its deployment pipeline refuses
  deployments until it points at another environment", warning. Nothing "pauses"; the
  pipeline refuses, so keep that verb.
- Secret expiring: "The Business Central secret for CRONUS expires in 6 days", warning.

Empty: quiet empty state "Nothing is waiting on you", as on the Admin dashboard.

## Recent activity (card, right column)

One merged timeline of builds and deployments, ten rows, newest first, avatar or initials
of the person (or "Agent" glyph when an MCP agent did it, "Auto" when a build was started
by a push):

- "K. Jensen started build #119 on CRONUS Base - main" - 5 min ago
- "Build #118 finished - 3 apps" - 2 h ago
- "M. Sorensen deployed build #118 to CRONUS - Production" - yesterday
- "A. Nielsen approved the deployment of build #117 to CRONUS - Sandbox" - 2 d ago
- "Deployment to Contoso - Sandbox failed on Contoso Base" - 2 d ago, warning row

Each row links to the build or to the deployment pipeline page. Card head buttons: outline
"View all builds" / "View all deployments".

## States

- **Populated**, as above.
- **Empty** (no pipelines of either kind): one `EmptyState` in place of the tiles and
  cards, title "No pipelines yet", body "A build pipeline compiles a solution's
  repositories on every push. A deployment pipeline installs a build on a Business
  Central environment.", primary button "New build pipeline", secondary outline "New
  deployment pipeline".
- **Half empty**: build pipelines exist but no deployment pipeline. Tiles 4 to 7 show zero
  with foot "No deployment pipeline yet"; the attention card carries a quiet row
  "Deployments are not set up. Create a deployment pipeline to install builds
  automatically." linking to the deployments list.
- **Loading**: the `LoadingBlock` primitive where the tiles go.

## Facts the product can and cannot supply

Everything above is stored today: builds with status, branch, commit and who started them;
deployments with status, per-app progress ("app 2 of 3"), who scheduled or approved them,
and the failed app's name; environment health from the mirror; secret expiry on the
solution's connection. Do not draw: durations of builds (not recorded), byte progress of
uploads, anything about Business Central update windows, and any promise like "nothing is
broken for users". Times are relative with the exact time in a tooltip; a follow-up in the
app will show them in the organisation's time zone (issue #942), so do not write "UTC" or
"local time" anywhere on the sheet.

## Port notes, after the sheet came back (2026-09-24)

`PagePipelines.dc.html` and `PipelinesBody.dc.html` follow the brief; the shell now marks the
Pipelines parent as a link. Three sentences on the sheet state things the product does not do,
and the port adapts them without changing the layout:

1. Empty state: "A build pipeline compiles a solution's repositories on every push." Builds run
   when a person presses Build, when an agent asks, or for a pull request. Port as: "A build
   pipeline compiles a solution's repositories when you press Build, or for each pull request."
2. The "Auto" avatar, "Started by a push". The only automatic build is the pull-request build,
   so the avatar reads "PR" with the title "Started by a pull request". Keep the sunken style.
3. Half-empty attention row: "Create a deployment pipeline to install builds automatically."
   Nothing installs by itself; a person deploys or approves. Port as "... to install builds on
   its environments."

Everything else on the sheet is stored data. The warning cue and the two avatars in
`Components.dc.html` need no new CSS: the cue's custom properties and the inline styles are
tokens already in `components.css`.
