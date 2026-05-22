# Cookbook

Per-organisation library of reusable AL **recipes** — what used to be called
"Snippets". The rename matters because recipes now span more than one file
and a folder structure: the original Snippets feature was capped to a flat
list of files, which forced authors to either lose the folder layout or skip
larger artefacts.

## Entities

- `Recipe` — one row per published recipe. Columns mirror the pre-rename
  `Snippet` shape (title, description, keywords, deprecated, instructions,
  minimum-application-version FK, soft-delete) **plus** a `Type` int
  discriminator.
- `RecipeFile` — one row per file inside a recipe. Carries a
  `RelativePath` text column for the folder it lives in (empty = root) and
  a flat `FileName` (no slashes). The ZIP download joins `RelativePath`
  and `FileName` with `/` so `ZipArchive` materialises folders
  automatically.
- `RecipeSuggestion` / `RecipeSuggestionFile` — same shape, separate
  tables, for the user-suggested-draft → admin-approval workflow.

The pre-rename tables (`snippets`, `snippet_files`, etc.) were renamed in
migration `20260618000000_RenameSnippetsToCookbook`. Existing rows surface
as `Type = Snippet` with an empty `RelativePath`, identical to their
pre-rename behaviour. URL redirects in `Program.cs` keep old `/snippets`
links working.

## Recipe types

`Domain/ValueObjects/RecipeType.cs`:

- **`Snippet`** — a small pattern, typically one or two files. Use for
  self-contained fragments: a single event subscriber, one
  `tableextension`, a focused helper codeunit.
- **`Pattern`** — a few related files that together solve one problem.
  Examples: an event subscriber plus the page/table it modifies; a setup
  table + page + install codeunit. Files may live under a folder structure.
- **`Module`** — a near-complete feature spanning several files and
  namespaces under one top-level namespace. Bigger than a Pattern; smaller
  than a full BC app.

The type is a chip-row filter on `/cookbook` and a badge on each card. It
is **not** part of the search expression — `RecipeService.SearchAsync`
ignores it. Type filtering happens client-side over the search result.

## Folder layout in files

`RelativePath` validation, enforced by `RecipeService.ValidateFiles`:

- Empty allowed (root).
- Split on `/`; each segment matches
  `^[A-Za-z0-9._-][A-Za-z0-9._ -]*$` — no `..`, no `.`, no empty segments,
  no control characters.
- No leading or trailing `/` (normalised away before validation fires).
- Max 8 segments, max 260 characters total.
- `(RelativePath, FileName)` must be unique within a recipe (case-insensitive).

`Components/Shared/RecipeFileEditor.razor` exposes Folder + File-name +
Content inputs per row; the user-facing `RecipeDetail` page renders a
flat list showing `RelativePath/FileName` on each file.

## MCP surface

Tools live in `Services/Mcp/Tools/CookbookTools.cs`:

- `search_recipes(query, includeDeprecated?)` — fuzzy ILIKE over title /
  description / keywords. Returns a `RecipeSummary` per match including
  the type string.
- `get_recipe(id)` — returns the full payload; each file's `Path` already
  joins `RelativePath` and `FileName`.
- `get_cookbook_guidance()` — returns the org's authored Markdown
  conventions (from `organization_settings.cookbook_guidance`) plus a
  built-in dictionary describing what each `RecipeType` means. **The
  `suggest_recipe` tool's description explicitly tells agents to call
  this first.** Built-in type descriptions live in code so an empty
  org-level guidance still steers the agent.
- `suggest_recipe(input)` — submits to the admin queue. Agents pass
  `Type` as a string (`"Snippet"` / `"Pattern"` / `"Module"`); each file
  carries its own `RelativePath`.
- `update_recipe_suggestion(input)` — edit a pending suggestion.

There is no enforced "call guidance first" gate — agents pick tools by
description, and the description language is the steering mechanism. If
this proves insufficient in practice the medium-strength option is a
short-lived token returned by `get_cookbook_guidance` that
`suggest_recipe` must echo back; we have not added that yet because the
description-level hint is the standard MCP idiom (see also
`list_templates` / `list_modules` cross-references in `WorkspaceTools`).

## Org-level authoring guidance

`OrganizationSettings.CookbookGuidance` is a Markdown column edited from
the **Cookbook authoring guidance** section on `/admin/cookbook`. 10,000
character cap, persisted by
`OrganizationConfigService.SaveCookbookGuidanceAsync`, read by
`get_cookbook_guidance`. Encourage organisations to document naming
conventions, prefixes, preferred event-subscriber style — anything an
agent should know before drafting a recipe.

## URLs at a glance

| Old (still works via 301) | New |
|---|---|
| `/snippets` | `/cookbook` |
| `/snippets/{id}` | `/cookbook/{id}` |
| `/snippets/suggest` | `/cookbook/suggest` |
| `/admin/snippets` | `/admin/cookbook` |
| `/admin/snippets/new` | `/admin/cookbook/new` |
| `/admin/snippets/{id}` | `/admin/cookbook/{id}` |
| `/admin/snippets/suggestions` | `/admin/cookbook/suggestions` |
| `/api/snippets/{id}/download` | `/api/cookbook/{id}/download` |

The MCP tool names also changed (`search_snippets` → `search_recipes`
etc.) — no aliases are kept because agents discover tools dynamically and
two tool names with overlapping responsibilities would dilute the
"call get_cookbook_guidance first" steering.
