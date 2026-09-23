# Deployments, not releases: a vocabulary change for the pipeline sheets

Decided by the maintainer on 2026-09-23, before the feature is in use at their company, so the
first version people learn carries the final name. This is a wording change only; the sheets'
structure, states and file names stay.

"Release" meant four things in the product. Two stay, because they are other people's terms: an
Object Explorer **release** (a Business Central version ingested for browsing) and a **GitHub
release** (an artifact source). The other two are renamed:

| Was | Now |
|---|---|
| Release pipeline | **Deployment pipeline** |
| Release (one run), "Release 3", "Releases, newest first" | **Deployment**, "Deployment 3", "Deployments, newest first" |
| Release (primary button), "Release again", "Schedule release", "Release now" | **Deploy**, "Deploy again", "Schedule deployment", "Deploy now" |
| "Release to {customer} - {env}", "Release build #N again?" | "Deploy to ...", "Deploy build #N again?" |
| Nav: Releases | Nav: **Pipelines** parent with **Builds** and **Deployments** children (like Solutions with Environments and Upgrades) |
| "All releases" (head button) | "All deployments" |
| "Waiting for approval ... a release was prepared" | "... a deployment was prepared" |
| "N releases waiting for approval" | "N deployments waiting for approval" |
| State words (Scheduled, Deployed, Failed, Handed to Business Central, ...) | unchanged |
| "When installs run", "Delivery window", "Schema sync" | unchanged |

Sheets to update in place: `PageReleasePipelines.dc.html` / `ReleasePipelinesBody.dc.html`
(page title "Deployment pipelines", subtitle, empty states, band's "Open deployment" or "View
progress", column "Deployment" for what was "Delivery"), `PageReleasePipeline.dc.html` /
`ReleasePipelineBody.dc.html` (crumbs "Deployments > {pipeline}", card title "Deployments",
row "Deployment 49"), `DeliveryRowPanel.dc.html` (row titles, the dialog), and `ShellFrame.dc.html`
(the Deliver group: a Pipelines parent with Builds and Deployments). Keep the file names; the
app's handoff folder mirrors them by name.

A Pipelines dashboard sheet (`PagePipelines.dc.html`, on the dashboard archetype) is a separate
request, issue #955 in the repo; do not draw it from this brief.
