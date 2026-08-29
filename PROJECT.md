# PROJECT.md

The map of the project: where things live, how the design docs and the design
handoff work, the domain-specific maintenance guides, and how releases are cut.
`CLAUDE.md` is the companion file about *how we build* — principles, fences,
conventions — and is read every session; this file is read when a task touches
one of its sections.

## Where things live

App folders are relative to `ALDevToolbox/`.

| Folder                       | What goes there                                                              |
|------------------------------|------------------------------------------------------------------------------|
| `Components/Pages/`          | Routable pages (one `.razor` per route).                                     |
| `Components/Layout/`         | Shell layout, sidebar, top bar, reconnect modal.                             |
| `Components/Shared/`         | Reusable components (`SettingRow`, `AuthCard`, `ConfirmDialog`, `DependencyPicker`, `AuditHistoryPanel`, ...). Check here before building a new control. |
| `Endpoints/`                 | Minimal-API endpoint groups (`AccountEndpoints`, `GenerationEndpoints`, `SiteAdminEndpoints`, …) registered from `Program.cs` via `Map*Endpoints()` extensions. |
| `Services/`                  | Application services (`GenerationService`, `TemplateImportService`, `TemplateService`, …). |
| `Domain/Entities/`           | EF Core entity classes (mutable, persisted).                                 |
| `Domain/ValueObjects/`       | Immutable records / JSON-mapped value objects, exceptions, plans.            |
| `Domain/Seed/`               | Tomlyn POCOs that mirror the TOML schema for the admin editor and export.   |
| `Data/`                      | `AppDbContext`, design-time factory, migrations.                             |
| `Data/Configurations/`       | Per-entity `IEntityTypeConfiguration<T>` classes (one file per entity).      |
| `Resources/`                 | Embedded static assets (ruleset, `.gitignore` template).                     |
| `wwwroot/`                   | Global CSS (the token/archetype sheets - byte-locked, see the handoff section - plus `app.css`), favicon. |

Test folders are relative to `ALDevToolbox.Tests/`.

| Folder            | What goes there                                                          |
|-------------------|--------------------------------------------------------------------------|
| `Builders/`       | Entity / plan builders pre-populated with sane defaults.                 |
| `Infrastructure/` | Reusable test plumbing (currently `TestDb` — Testcontainers / service-container Postgres fixture). |
| `Generation/`     | `GenerationService` tests.                                               |
| `Audit/`          | `AuditInterceptor` tests.                                                |
| `Toml/`           | `TemplateTomlMapper` tests.                                              |
| `Validation/`     | `PlanValidationException` field-key tests.                               |

When you add a new file, match the folder. Resist creating top-level folders — the layered split is intentional. Test patterns are documented in `ALDevToolbox.Tests/README.md`; new service tests should follow them.

## Working with the design docs

`.design/` is the spec. Treat it as the contract:

- `architecture.md` — stack and layering decisions, request flow.
- `domain-model.md` — every table, column, validation rule.
- `generation-engine.md` — what the ZIP must look like and how to build it.
- `templates-and-seeding.md` — TOML schema and the seed contract.
- `auth-and-audit.md` — how the password gate and audit interceptor work.
- `teams-and-visibility.md` — teams, their managers, and the per-project visibility model they grant.
- `ui-design.md` — page layout, copy, components to factor out.
- `bcquality.md` — the mirrored BCQuality knowledge base: ingest, schema, refresh policy, and the two MCP tools over it.
- `completed-milestones.md` — the record of what each shipped milestone added (M1–M21).
- `roadmap.md` — uncommitted forward-looking ideas (successor to the old `milestones.md` plan).

When implementing a milestone:

1. Re-read the relevant design docs first.
2. If the design says something the code can't easily satisfy, write the question into the PR description and pause for input rather than improvising.
3. If a design choice has aged badly, update the design doc in the same PR as the code change — don't leave the doc claiming something the code no longer does.

### Implementing a Claude Design handoff

A Claude Design handoff (the prototype HTML/CSS/JS a `.design/*.md` points at — e.g. the screens named in `artifacts.md`) is a **visual spec to translate, not a codebase to port**. The prototype is vanilla JS building DOM with its own self-contained CSS; recreate it as idiomatic Blazor — but be *faithful to the pixels* while you re-express the *implementation*. The failure mode is letting "idiomatic Blazor / adapt to our data" become an excuse to silently drop visual detail that was right there in the handoff.

- **Translate, don't transliterate.** Re-express structure through our components and conventions (`BuildStatusPill`, `.ra__menu` kebabs, `.btn--*`, scoped CSS), but treat the prototype's visual details — what's in each cell, the styled controls, per-row affordances, spacing — as the spec to preserve, not to re-derive.
- **Port the prototype's component CSS near-verbatim onto our tokens.** Its rules are usually good; bring them over swapping its private system for ours (`--bad`→`--danger`, `.btn.sm`→`.btn--sm`) rather than re-writing thinner versions. Drop only its canvas scaffolding (`.frame`, the side-by-side light/dark frames) — the app already themes via `data-theme`. Note there is no global `.input` class: a styled input/select needs the scoped rules (see `.cb-search .input`), not a bare class.
- **Diff against the *rendered* prototype early, cell by cell.** Screenshot your page next to the handoff's screenshots and ask "what's in their cell that's missing in mine?" — checking only that yours looks internally cohesive is how details slip.
- **Every data-driven omission is a flag, not a default.** "Prototype shows X, our DTO lacks X" → wire it through or call it out in the PR; never silently drop it.
- Reuse the existing token system and components; don't stand up a parallel one just because the prototype ships its own.
- **The ported sheets are byte-locked.** `StylesheetLoadOrderTests` asserts that `wwwroot/tokens.css`, `components.css`, `shell.css` and the `pages-*.css` family match their `.design/handoff/` copies byte for byte. Never edit just one copy — the test fails, and worse, silence means drift. The durable path for a new rule is: push it to the Claude Design project (the id is in `.design/handoff/README.md`; use DesignSync), then land the identical change in both checked-in copies. Patching both copies locally is an acceptable stopgap inside one PR, but say so in the PR body so the upstream push isn't forgotten.

## Local development environment

- **SDK:** `global.json` pins .NET 10.0.300, installed via asdf — run `export PATH="$HOME/.asdf/shims:$PATH"` before any `dotnet` command, or the pin fails against the bare-PATH SDK.
- **Lock files:** building with a mismatched local SDK rewrites `packages.lock.json` with different transitive pins. Revert that churn before committing — it is noise, not a dependency change.
- **Tests:** `dotnet test` spins up PostgreSQL via Testcontainers when Docker is running (`docker info` to check). The 8 backup tests need `pg_dump`/`pg_restore` on the host at the server's major (PG18) and skip otherwise.
- **Running the app:** `dotnet run --project ALDevToolbox/ALDevToolbox.csproj` binds http://localhost:5246 (launchSettings wins over `ASPNETCORE_URLS`); point `ConnectionStrings__DefaultConnection` at a local Postgres and set `BOOTSTRAP_ADMIN_EMAIL`/`BOOTSTRAP_ADMIN_PASSWORD` on a fresh database. Screenshot verification uses Playwright.

## Keeping the AL reference extractor's allow-lists current

The Object Explorer's reference extractor (`Services/Al/AlReferenceExtractor.cs`) reports an Unresolved count after each Phase-2 import. New BC releases occasionally ship new built-in methods, scalar types, runtime APIs, or platform virtual tables that need to land in our allow-lists to keep that number trustworthy. Two files cover the surface:

- **`Services/Al/AlBuiltinMethods.cs`** — every category of "built-in name we expect to skip" (method sets per receiver kind, scalar types, system functions, statement keywords, DSL keywords, static-receiver names). The class-level doc-comment has a labelled `EXTENDING WHEN MICROSOFT ADDS NEW METHODS / TYPES` checklist mapping each kind of addition to the right `HashSet`.
- **`Services/ObjectExplorer/ReleaseImportService.cs`** — `PlatformVirtualTables` (the named id → name map for the `2000000001..2000000999` runtime tables) and `FoundationalAppNames` (Microsoft umbrella apps every extension implicitly depends on). Both have `EXTENDING` notes at their definition.

`AlReferenceExtractor.IsPlatformVirtualTableId` is the range-check safety net for the platform-table ids — even if a numeric id isn't named, the diagnostic silences. Add to the named list when the symbol package resolves the id to a name (so `Record Field`-style chains work), not just to silence noise.

When new noise patterns appear in the Phase-2 sample log, prefer extending one of these allow-lists over adding bespoke code paths to the walker. The diagnostic itself (`AlReferenceExtractor.CaptureUnresolved`) is intentionally cheap and structured so operators can grep the log by `Reason=` and trace each new bucket back to a list above.

The legacy **C/AL TXT** ingest path (`Services/Cal/`) has its own parallel allow-list — **`Services/Cal/CalBuiltinMethods.cs`** — because classic C/AL's runtime surface and casing differ from AL (uppercase `SETRANGE`/`FINDFIRST`, `FIND('-')`, the `DATABASE::`/`CODEUNIT::` static receivers). Its class-level doc-comment carries the same `EXTENDING WHEN A NEW C/AL RELEASE ADDS NAMES` checklist mapping each kind of addition to the right `HashSet` (`ReceiverMethods`, `BareFunctions`, `FieldNameTakingMethods`, `StaticReceivers`, `Keywords`). `CalReferenceExtractor` counts unresolved receivers the same way; extend this list — not the walker — when a real C/AL export surfaces a new built-in as noise.

## Keeping MCP parity with the web UI

The MCP server (`Services/Mcp/Tools/*Tools.cs`) is a parallel front-end on the same services the Blazor pages use — agents reach the Object Explorer (and friends) through these tools. When you add a feature that's user-visible in the web UI — a new reference kind, an outline section, a derived relationship, a filter — check whether it should also show up through MCP, and wire it through in the same PR. Two patterns matter:

- **Service-level features come for free.** If the new behaviour lives behind an existing service method (e.g. `FindReferencesAsync` matching a new `reference_kind`), the matching MCP tool usually picks it up automatically. Verify it actually reaches the MCP path — the tool may call a sibling method that doesn't see the new bucket.
- **New DTOs and query paths need plumbing.** When a feature lands a new field on a DTO (e.g. `ObjectOutline.ImplementedBy`) or a separate query method (`FindReferencesForSymbolAsync` vs `FindReferencesAsync`), the MCP tool has to be updated to populate the field or route to the right query. Otherwise the web UI shows the relationship and MCP agents stay blind to it.

- **A new gate is a feature too.** MCP tools resolve ids through their own private
  `Resolve*Async` helpers, which query the DbSets directly rather than going through
  the gated services the pages use — so an access rule added to a service does *not*
  reach them for free. Each tool class has one such choke point (`ArtifactsTools`'s
  `ResolveProjectAsync`/`ResolveReadyBuildAsync`, `DeliveryTools`'s
  `EnsureReleasePipelineExistsAsync`, `ObjectExplorerTools`'s `ResolveReleaseAsync`);
  put the check there rather than in each tool, then check the tools that *bypass*
  the choke point (a `symbolId` argument that skips release resolution) and gate
  those explicitly. A denied read answers the tool's existing "not found" message,
  never a distinct refusal — see the project-visibility fence in
  `.design/teams-and-visibility.md` for the worked example.

Skip the MCP path only when it genuinely doesn't apply — pure UI affordances (resizers, badge styling, keyboard shortcuts), authoring flows that already have a dedicated MCP tool, or per-org admin pages that aren't part of the AL-reading surface. When in doubt, expose it through MCP; agents tend to want the same answers humans do.

## Releases and image publishing

Releases are cut by pushing a git tag; `.github/workflows/release.yml` builds the Dockerfile, pushes `ghcr.io/mtaanquist/aldevtoolbox` to GHCR, and publishes the matching GitHub Release with auto-generated notes. There is no release on every merge — `main` stays continuously green via `build.yml`, and a release is a deliberate tag on a commit that's already passed CI.

**Version scheme — one major per shipped end-user tool.** The major number is the count of distinct tools in the sidebar's Tools section. Each new tool bumps the major; everything else (features within a tool, cross-cutting work like auth/backups/hosting, polish) is a minor or a patch. The mapping as of 9.0.0:

| Major | Tool that opened it      | Landed |
|-------|--------------------------|--------|
| 1     | Projects (Workspace + Extension generators) | the original product |
| 2     | Piper                    | #64    |
| 3     | Object Explorer          | #103   |
| 4     | MCP server               | #173   |
| 5     | Cookbook (née Snippets)  | ~#180  |
| 6     | Translator               | #295   |
| 7     | Pipelines (project builds + artifacts) | #449 |
| 8     | Diff (né Compare)        | #512   |
| 9     | — the whole-app redesign (see below) | #596 |

- **Major** — a new top-level tool ships (the next entry in the table). Don't bump major for anything short of a genuinely new tool surface. The one non-tool exception on record is v9.0.0, the whole-app redesign: every screen changed at once, and operators pinning `8` should not receive that unasked. A future change of that magnitude — every screen, or a migration operators must plan for — may take a major on the same reasoning; a big feature inside one tool still may not.
- **Minor** — a new feature, page, or capability inside an existing tool, or cross-cutting work (a new role, backup tooling, a hosting endpoint). Most releases are minor bumps.
- **Patch** — bug fixes and copy/UX tweaks with no new surface.

**Cutting a release:**

1. Make sure `main` is green (the commit you're tagging passed `build.yml`).
2. Pick the version per the scheme above. Tag and push:
   ```bash
   git tag v6.0.0
   git push origin v6.0.0
   ```
3. `release.yml` fires on the `v*.*.*` tag, builds the image, pushes the moving tags `latest`, `6`, `6.0` plus the exact `6.0.0`, and publishes the GitHub Release with generated notes. Nothing to publish by hand. Operators pin the image as loosely or tightly as they want.

**The image is stamped with its version.** `release.yml` passes the tag and the build date to the Dockerfile as the `RELEASE_VERSION` / `RELEASE_DATE` build args, which reach `dotnet publish` as the `ReleaseVersion` / `ReleaseDate` MSBuild properties and land in the assembly as metadata attributes. `Services/BuildInfo` reads them back and the sidebar footer shows "Version x.y.z" under the copyright, linking to that release's notes with the release date on hover. Builds without the args (local `dotnet run`, plain `docker build`, staging images) carry no stamp and show the copyright line alone — never a link to a release they aren't.

Never move or re-push a published tag — cut a new patch instead. The image name is derived from `github.repository`, lowercased by `docker/metadata-action`, so it always resolves to `ghcr.io/mtaanquist/aldevtoolbox` regardless of the repo's casing.

**Staging previews.** `.github/workflows/staging.yml` publishes the same image under a `staging` tag so a branch can be *run* before it merges. It pushes both `staging` (moves every run) and `staging-<sha>` (immutable, so a preview worth keeping can be pinned). Run it with `ALDEVTOOLBOX_TAG=staging docker compose up -d`.

Two triggers, available at different times. `gh workflow run staging.yml --ref <branch>` is the one to reach for — but GitHub only offers a manual run for workflows present on the **default branch**, so a workflow that only exists on a feature branch can't be dispatched at all. Until this file is on `main`, the `push:` branch list is what actually fires; add a branch there to get an auto-rebuilding staging instance, and remove it when the branch merges. Moving `staging` is not an exception to the rule above: that rule protects tags operators pin, and this one exists to move. Unlike `release.yml` it does **not** wait for `build.yml` to go green — a staging image is for looking at, so check CI yourself before trusting what you see.
