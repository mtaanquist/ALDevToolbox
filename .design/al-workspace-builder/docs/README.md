# AL Workspace Builder — design documentation

This is an internal Blazor Server tool that generates Microsoft Dynamics 365 Business Central (AL) workspace skeletons from runtime-specific templates. It replaces a small static HTML/JS tool that did the same thing in a more limited way.

## What it does

Two main flows:

1. **New workspace** — generate a multi-extension workspace (a Core extension plus selected modules like Document Capture, Payment Management, etc.) as a downloadable ZIP. This is the primary use case.
2. **New extension** — generate a single standalone extension that the user adds into an existing workspace themselves. Used when a project needs something custom that wasn't ticked at workspace creation time, or when adding a new module to an older workspace.

Templates (folder layouts, default `app.json` values, supported runtime) and the module catalogue are stored in SQLite and editable through a password-gated admin UI inside the app. The tool runs as a single Docker container with one mounted volume for the database.

## Audience for these docs

These docs are written for Claude Code (or a developer) implementing the project from scratch. They specify the design at the level of "what the parts are and how they fit together," not line-by-line code. Implementation choices below that level — naming, exact folder layout, library minor versions, formatting — are at the implementer's discretion.

## How to read

Suggested order:

1. `architecture.md` — tech stack, layers, deployment shape.
2. `domain-model.md` — the SQLite schema and entity relationships.
3. `templates-and-seeding.md` — the TOML schema for seed data and how it's imported.
4. `generation-engine.md` — what the generator actually produces given a template and a project plan.
5. `ui-design.md` — pages, components, and layout.
6. `auth-and-audit.md` — the password gate and the audit log.
7. `deployment.md` — Docker, volumes, environment.
8. `milestones.md` — suggested build order.

## Companion files

`Templates.seed/` (sibling to `docs/`) contains the initial TOML files that are imported into the SQLite database on first run. These are the source of truth for the *initial state* of templates and the module catalogue, but **not** the runtime data store — once imported, edits happen in SQLite via the admin UI.

## Tech stack at a glance

- Blazor Server, .NET 9 (or whatever current LTS is at implementation time)
- EF Core with the SQLite provider
- Tomlyn for parsing seed TOML files at first-run import only
- A small JS library for client-side ZIP creation is **not** needed — the original tool used JSZip in the browser, but the Blazor Server version generates the ZIP server-side and streams it down. Use `System.IO.Compression.ZipArchive`.
- No SPA framework, no separate frontend build step.

## Known constraints

- The existing static tool at `index.html` / `script.js` is the prior art. The TOML files in `Templates.seed/modules/` were derived from the dependency lists hardcoded in `script.js`'s `getModuleDependencies` function. If anything is missing or wrong there, the static tool is the authoritative source.
- The Core extension folder is *always* called `Core` in the generated workspace. The extension name in `app.json` is `"{{workspaceName}} Core"` — for example "Acme Customer Core".
- Core's ID range is always 90000–90999 and module ID ranges start at 91000 with 200 IDs per module by default. These are defaults, not hardcoded — they're fields in the runtime template TOML, but reviewers should leave them alone unless there's a specific reason.
