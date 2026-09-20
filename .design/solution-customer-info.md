# Customer information on a Solution

Status: **part shipped.** Slice 1 (hosting and the basics, #859) and slice 2 (getting in,
contacts, who knows the customer, integrations, #860) and slice 3 (modules) are built.
The Solutions list's side panel (slice 4) is planned. Each section is labelled with its
slice.

## Why

The support team keeps a spreadsheet-style list with one row per customer: which
Business Central (or NAV) version they run, where it is hosted, how to get in, who to
call, and which third-party modules they have at which version. It answers the first
five minutes of every support call, and it is kept by hand in a tool that knows nothing
about the customer's tenant.

A Solution is already our record of a customer engagement, and for a customer on
Business Central online it already knows the tenant, the environments and what is
installed in them. The list belongs on it.

About half the customers are **not** on Business Central online. They run on their own
hardware, in our hosting, or at a hosting partner. Nothing we have built for delivery
works for them - there is no admin API to call - but everything support needs to look up
applies to them just as much. So a Solution has to be able to exist, and be useful,
without a Business Central connection.

### Named users

- **A support consultant taking a call** from a customer they have never worked with.
  Needs, in under a minute: what version, where it runs, how to get in, who their IT
  supplier is, whether they have the module the caller is asking about. Reads; rarely
  edits. Knows Business Central; knows nothing about this codebase.
- **The consultant who owns the customer**, keeping that information true: a new
  contact, a module upgraded, a move to the cloud. Edits a field or two at a time, a few
  times a year per customer - and, at the start, types in the customers they own, since
  there is no import.

## What a Solution gains

Visible copy says Solution; the code and the tables say Project (see CLAUDE.md).

### Hosting and the basics (slice 1)

New nullable columns on `oe_projects`. All optional: a Solution created from New
Workspace must keep working with none of them set.

| Column | Meaning |
| --- | --- |
| `hosting_type` | `MicrosoftCloud`, `OurCloud`, `HostingPartner`, `CustomerHardware`. Text, like the other enums on this table. Null means "not said yet". |
| `bc_version` | What they run, as people say it: "BC 25.3", "NAV 2018 CU12". Free text - it spans fifteen years of version schemes and is for reading, not comparing. |
| `license_type` | `Purchased`, `Leased`, `Cloud`. |
| `user_experience` | `Essential`, `Premium`. |
| `client_url` | Where a person opens the client. |
| `voice_account_number` | Microsoft's **Voice account number** - the customer's account for on-premises licence registration, printed as "Voice ID" in a licence file. Not the partner's MPN id. Kept on every Solution: most customers who have moved online still have one, and it matters for history. Plain text, no link. |

**On-premises is derived, not stored.** A Solution is *online* when `hosting_type` is
`MicrosoftCloud` or null, and *on-premises* otherwise. One field, so the two cannot
disagree. Null counts as online because every Solution that exists today is, and must
not lose its tabs on the day this ships.

**What on-premises turns off.** The Business Central tab, release pipelines, and the
Solution's rows on Environments and Upgrades - the surfaces that call the admin API.
General, Repositories, Customer (below) and Access stay; so do pipelines that only build.
Setting an on-premises hosting type is refused while Business Central online reports
environments for the Solution, rather than silently hiding live ones.

**The tenant id is not the test.** It is the customer's Microsoft tenant, and most
on-premises customers have one too. It stays the one `bc_tenant_id` column: the Business
Central tab owns it for an online Solution (changing it there resets the connection), and
the Customer tab edits it for an on-premises one, where that tab is gone.

### The Customer tab (slices 1-3)

One new tab on the Solution page, **Customer**, shown to everyone who can see the
Solution. It is the first tab and the one an existing Solution opens on, because it is
what most people opening a Solution come for; a Solution being created has no customer
yet and opens on General. It is a *read* view with an Edit
action, not a settings form: the named reader outnumbers the named editor many times
over, and a page of inputs is a poor way to read a phone number.

Reading follows the Solution's visibility, unchanged: whoever can see the Solution sees
all of this. Editing is for people who manage it (owner, org Admin, a member of an
assigned team) - the gate the rest of the page has.

Sections, top to bottom:

1. **Basics** (slice 1) - the fields above, plus the tenant id when there is one.
2. **Getting in** (slice 2) - `access_description` and `hosting_notes`, plain multi-line
   text. The editor carries a caption: *Don't put passwords here - say where the
   password is kept.* This is ordinary Solution data, deliberately **not** encrypted:
   it is prose about VPNs and jump hosts, and encrypting it would promise a protection
   that the text itself would undo the first time someone pasted a password into it.
3. **Contacts** (slice 2) - `oe_project_contacts`: name, company, email, phone, and a
   type (`Customer`, `HostingPartner`, `MicrosoftPartner`, `Internal`; on screen "At the
   customer", "At their hosting or IT partner" - which is where the old list's IT supplier
   goes - "At another Microsoft partner", "Here with us"). Each add form has **Save and
   add another**, because with no import somebody types these in a few hundred times. A short list,
   one row each, edited in place with an **Add contact** button - not a grid.
4. **Modules** (slice 3) - see below.
5. **Who knows this customer** (slice 2) - `oe_project_people`: one of our users, a role
   (`Consultant`, `Developer`, `ProjectLeader`, `Architect`) and free-text areas
   ("finance, warehouse"). Separate from Teams on purpose: a team says who *may* change
   the Solution, this says who to *ask*.
6. **Integrations** (slice 2) - `oe_project_integrations`: a name and a direction
   (`Inbound`, `Outbound`, `Both`).
7. **Good to know** (slice 2) - `knowledge_notes`, plain text, for what does not fit
   above. Shown and edited with Getting in: the same person writes all three at once.

Contacts, people and integrations landed together as one slice rather than two: they are
the same pattern three times (a short list, one editor open at a time, an Add button, a
confirm on Remove), styled once in `app.css` as `.cust-list`.

**One read for the tab.** The sections are sibling components on one circuit, so they
share a `DbContext`, which allows one operation at a time. `GetAllAsync` reads the whole
tab in sequence and each section is handed its part; a section reads for itself only
after its own write. Letting five sections each load on first render is how "a second
operation was started" happens (#741).

**First run is one empty state, not five.** Until anything has been entered the tab shows
only "Nothing about this customer yet" and its one button; the sections arrive with the
first thing anyone saves.

### Modules (slice 3)

`customer_modules` is an organisation-wide list of the third-party modules support cares
about: name, publisher, and the Business Central app id when it is an app. It is kept at
`/admin/customer-modules` (Admin and Editor, like the other content pages; "Customer
modules" in the nav, because "Modules" there already means template modules). The named
user knows the add-ons by name and has never seen an app id, so the usual way to add one
is **Add from installed apps**: everything Business Central has reported anywhere in the
organisation that is not in the catalogue yet, Microsoft's own apps left out, the most
widespread first. **Add by hand** is for an old NAV add-on that is not an app.

Which modules a Solution has comes from one of two places, never both:

- **On-premises Solutions** - typed in. `oe_project_modules`: a catalogue module, a
  version, a note; one row per module. Manage-gated.
- **Online Solutions** - read, not typed, and the service refuses a typed row. The list is
  the catalogue matched against `oe_environment_apps` for the Solution's production
  environment (a sandbox only when there is no production): by app id, or by name and
  publisher for a catalogue entry without one. The card says which environment and how
  old the reading is, and links to the environment's Apps tab for everything else.

**Why a mirror, not the live read.** The installed-apps read is made with the customer's
credentials and is manage-gated, so the support consultant this tab is for could not
trigger it. `oe_environment_apps` holds what Business Central last reported per
environment and is readable by anyone who can see the Solution. It is brought in line, in
place, by `ProjectConnectionService.MirrorInstalledAppsAsync` from two callers: the
environment refresh (so the nightly sweep keeps the whole fleet current) and a live panel
read (so opening an environment's Apps tab freshens it at once). An answer with no apps in
it is treated as a failed read and leaves the last good mirror alone - an environment
always has the base application.

An empty Modules card says *why* it is empty, because the four reasons need four
different next steps: no catalogue yet, no environment read yet, installed apps not read
yet, or genuinely none of the catalogue's modules installed.

The Solutions list has a **Has {module}** filter, shown once there is a catalogue. It is
part of the list's GET form, so a filtered list is a shareable address; it counts a module
either way (typed in, or installed in any current environment).

With modules in, **Customer is the first tab and the one an existing Solution opens on.**
Links that mean another tab say so (`?tab=repositories`, `pipelines`, `bc`, `general`,
`access`).

### The Solutions list and its side panel (slice 4)

The list stays narrow - Solution, hosting, version, owner, latest build - and the rest
follows the selected row in a panel on the right, as the factboxes did in the AL
prototype this model comes from. It is the support consultant's view: on a call they are
scanning customers, not editing one, and the answer should not be a page load away.

- Selecting a row (a click anywhere on it that is not a link, or the arrow keys) fills the
  panel: **Getting in**, **Contacts**, **Modules**, **Who knows this customer**, with
  **Open solution** at the top. The Solution's name in the row stays an ordinary link.
- The panel reads on selection, in one query; the list does not carry the data for every
  row.
- It is the design system's 280px reference rail (the one the settings and detail frames
  have), on a list page. Below the width where those rails collapse it drops under the
  list rather than squeezing it.
- It summarises and links; it never edits. The Customer tab is the only place customer
  information is changed.

`ListPage` has no rail slot today. It gets one in this slice rather than the page writing
round the frame, and the pattern needs a design pass upstream - there is no sheet for it.

## Deliberately out of scope

- **No import.** The existing list is entered by hand (maintainer's decision), which is
  why the editors have to be quick and why every field is optional.
- **No secrets.** No passwords, no encrypted notes, no reveal-on-click.
- **No helpdesk or Azure DevOps links.** They were in the AL prototype; not wanted.
- **No comments thread.** Notes are one text field; history is the audit log.
- **No MCP surface** in these slices. Contacts are personal data; handing them to an
  agent is a conversation of its own.

## Open questions

- Should a plain `User` who can see a Solution be able to *edit* its customer
  information? Support are the people who learn a contact has changed. The current
  answer is no (manage gate); revisit once it is in use.
