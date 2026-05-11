# Design docs

Living specification for the AL Dev Toolbox. The code in `ALDevToolbox/` is the implementation; these documents are the contract it's built against. When the code and a doc disagree, fix one of them — don't leave them out of sync.

## What's here

| File | Covers |
|------|--------|
| `architecture.md` | Stack, layers, request flow, services. |
| `domain-model.md` | Tables, columns, validation rules. |
| `generation-engine.md` | Generated ZIP layout, mustache substitution, ID-range allocation. |
| `templates-and-seeding.md` | Template TOML schema; how the system org seeds other organisations via `TemplateImportService`. |
| `auth-and-audit.md` | Email/password accounts, organisations, signup approval, audit interceptor. |
| `ui-design.md` | Page layout, copy, components in `Components/Shared/`. |
| `deployment.md` | Docker, env vars, health checks, backups. |
| `milestones.md` | Build sequence — current and Phase 4 candidates. |
| `completed-milestones.md` | What each shipped milestone added. |
| `migration-history.md` | One-line summary per EF migration. |

`template.toml` and `well-known-deps.toml` are reference samples for the seed format documented in `templates-and-seeding.md`. `al-workspace-builder/` carries reference material from the legacy in-browser tool this app replaces; everything load-bearing has migrated into the docs above.

## Contributing changes

If you change behaviour, update the relevant doc in the same PR. Don't leave a "this changed in phase X" footnote — rewrite the doc to describe the current state. The repo's `CLAUDE.md` covers conventions for new code.
