# Customer information on a Solution

Status: **shipped.** Slice 1 (hosting and the basics, #859) and slice 2 (getting in,
contacts, who knows the customer, integrations, #860), slice 3 (modules, #861) and slice 4
(the Solutions list's summary) are built. Each section is labelled with its
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
| `bc_version` | What they run, as people say it: "BC 25.3", "NAV 2018 CU12". Free text - it spans fifteen years of version schemes and is for reading, not comparing. Hidden while Business Central reports the version (below). |
| `license_type` | `Purchased`, `Leased`, `Cloud`. |
| `user_experience` | `Essential`, `Premium`. |
| `client_url` | Where a person opens the client. Hidden while Business Central reports the address (below). |
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

**Business Central's version and address win when it has told us them** (#907). Typing a
version for a customer whose tenant we are connected to is a second copy that goes stale
at the next update. So a Solution shows the version and the address of its production
environment, as the mirror last read them (`oe_project_environments.version` and
`web_client_login_url`, rewritten by every refresh), when all of these hold:

- it is online (`hosting_type` `MicrosoftCloud` or null);
- its connection is configured - a tenant id and a registration with a stored secret, its
  own or the organisation's. The same test the nightly refresh uses to pick what to sweep
  (`ProjectConnectionService.ConfiguredProjects`); it asks whether a secret is stored and
  never decrypts one;
- it has a **Production** environment that is still there (not missing, not soft-deleted)
  and has reported a version or an address. Among several, the first by name.

Only Production counts: a sandbox's version is not what support means by "the customer's
version", so a sandbox-only tenant keeps what was typed. The address is the login URL
Microsoft returns for the environment, not a vanity address a customer may also use.

Otherwise - on-premises, no connection yet, no production environment yet - the typed
values show, exactly as before. The typed columns are **kept, not blanked**, while
Business Central's win: if the connection is later removed they come back rather than the
field going empty, and the save leaves both alone in that state, so a form opened before
the connection was made cannot overwrite them.

One resolution (`ProjectCustomerInfoService.ReadProductionFactsAsync`) serves the Customer
tab, the Solutions list's column and rail, and the palette's Solution subtitle, so they
cannot disagree; a list reads it for all its rows in one query. In the editor the two
inputs give way to read-only rows - the value, and "From the Production environment,
read 3 hours ago.", with a link to the Business Central tab where it can be refreshed -
which are the explanation; there is no caption about the mechanism.

### The Customer tab (slices 1-3)

One new tab on the Solution page, **Customer**, shown to everyone who can see the
Solution. It is the first tab and the one an existing Solution opens on, because it is
what most people opening a Solution come for; a Solution being created has no customer
yet and opens on General. It is a *read* view with an Edit
action, not a settings form: the named reader outnumbers the named editor many times
over, and a page of inputs is a poor way to read a phone number.

Reading follows the Solution's visibility, unchanged: whoever can see the Solution sees
all of this. **Editing was once deliberately wider than managing the Solution** (maintainer's
decision, 2026-09-21): the people who learn that a contact has changed are the ones
answering the phone, not the Solution's owner, so anyone who could see a **Public** Solution
could correct it, through a `ProjectAccess.CanEditCustomerInfoAsync` that widened the
ordinary rule.

That method is gone, and nothing about who may edit has changed. Managing a **Public**
Solution is now everyone in the organisation (see `teams-and-visibility.md`, "Public is
open both ways"), which is exactly the set the wider rule reached for, so the two rules
became one set and the pass-through was removed rather than left to imply a distinction it
no longer made. `EnsureCanManageAsync` is what the three call sites use. A **Read-only**
Solution still keeps its word and is edited by its teams only; a **Private** one is only
ever seen by them. One field is excepted: **Hosted by** decides which tabs
the Solution has, so changing it stays with the people who manage the Solution, and the
editor says so beside the locked field. None of these edits is in the audit trail - a
contact corrected or a version typed in is too frequent and too small to be worth the noise
(same decision); the connection and team grants remain what the trail is for.

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

### The Solutions list and its customer info (slice 4)

The list stays narrow - Solution, hosted by, BC version, latest build, owner - and the rest
follows the chosen row in a rail on the right, as the factboxes did in the AL prototype
this model came from. It is the support consultant's view: on a call they are scanning
customers, not editing one, and the answer should not be a page load away.

- Each row has **Customer info** beside **Open**. It is a link, not a click handler:
  `/solutions?selected={id}`, keeping whatever search and module filter are in the address.
  The page therefore stays the plain GET page it was - no circuit, nothing to reconnect -
  and a list with one customer's summary open is an address that can be sent to a
  colleague. The Solution's name in the row still opens the Solution.
- The rail shows the name with **Open solution** and a close link, then hosted by,
  version and the Business Central address; **Getting in**; **Contacts** with `tel:` and
  `mailto:` links; **Modules**; **Who knows this customer**. It summarises and links; it
  never edits. The Customer tab is the only place any of it is changed.
- It is read only for the selected Solution, through the same view-gated reads the
  Customer tab uses. Only a row that is on screen and open to the viewer can be selected:
  a locked row's id in the address is ignored, not answered.
- `ListPage` gained a `Rail` slot for it, which is the detail frames' `.detail-body`
  reference rail beside a list: sticky, 280px, under the list below the width where those
  collapse. A page passes it per render, so no column is reserved while nothing is
  selected. Everything in the rail wraps in full - a phone number cut off with an ellipsis
  is no use on a call.

The Repositories column gave way to Hosted by and BC version; the count is on the Solution.
The build's own version reads "Built for BC 26.0" so that two bare versions never sit side
by side - the customer's is the one that gets read out on a call.
There is no sheet for a list with a rail, so this needs a design pass upstream.

## Deliberately out of scope

- **No import.** The existing list is entered by hand (maintainer's decision), which is
  why the editors have to be quick and why every field is optional.
- **No secrets.** No passwords, no encrypted notes, no reveal-on-click.
- **No helpdesk or Azure DevOps links.** They were in the AL prototype; not wanted.
- **No comments thread.** Notes are one text field; history is the audit log.
- **No MCP surface** in these slices. Contacts are personal data; handing them to an
  agent is a conversation of its own.
