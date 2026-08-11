# Claude Design handoff — Business Central design system

The rendered prototype this folder mirrors lives in the Claude Design project
**AL Dev Toolbox Design System**, id `63d872d4-c751-4420-b910-cb7eec63e4c3`
(`https://claude.ai/design/p/63d872d4-c751-4420-b910-cb7eec63e4c3`). It was
produced from the brief in `../design-system-brief.md`.

`DESIGN-SYSTEM.md` is the system's own index — token contract, component
inventory, page-archetype catalogue, and the six non-negotiables. Read it first.

## What is checked in here, and what is not

Checked in — the layers we are actively porting:

| File | Owns |
| --- | --- |
| `tokens.css` | The whole token contract. **Copied verbatim to `ALDevToolbox/wwwroot/tokens.css`.** |
| `components.css` | Every reusable component. References tokens only. |
| `shell.css` | Sidebar, top bar, content column, sticky page head, responsive steps. |
| `DESIGN-SYSTEM.md` | The system index. |
| `bc-reference.md` | Notes taken from real BC client screenshots (field chrome, dialogs, lists). |

Not checked in yet — pull the layer you are actually porting, don't bulk-import:

- `pages.css` (archetypes 1-4, 8), `pages-forms.css` (5-7 + auth),
  `pages-power.css` (9-11), `pages-content.css` (12-14)
- the `*.dc.html` review sheets (the rendered spec — `KitchenSink.dc.html` is the
  all-in-one view), `support.js`, `foundations.css` (review scaffolding, never shipped)
- `screens/*.png` — the design agent's own screenshots, useful for pixel-diffing

Pull one with the `DesignSync` tool:
`DesignSync{ method: "get_file", projectId: "63d872d4-...", path: "pages-forms.css" }`
and write it here alongside the rest, in the same PR that ports it.

## Re-syncing

The copy of `tokens.css` **in this folder is pristine upstream** — exactly what
the design project holds. `ALDevToolbox/wwwroot/tokens.css` is that file plus a
short, deliberate deviation list. Keeping the two separate is what makes a
re-sync diff readable: pull the new upstream over this copy, then diff.

```
diff .design/handoff/tokens.css ALDevToolbox/wwwroot/tokens.css
```

Anything that shows up and is not on the list below is accidental drift, and
should go back to matching upstream.

**Deviation 1 — `--font-sans`.** The handoff specified the bare Segoe UI stack
and no web fonts. Segoe UI cannot be self-hosted (Microsoft's licence covers
building software for a Microsoft platform, not web embedding), and off Windows
that stack fell through to Tahoma/Helvetica rather than the platform's own UI
font. The app's stack therefore names Segoe first, then **Selawik** — Microsoft's
own OFL-1.1, metric-compatible Segoe replacement, vendored in `wwwroot/fonts/`
and declared in `wwwroot/fonts.css` — then `system-ui`. Windows resolves Segoe
locally and downloads nothing. Worth pushing back upstream so the next re-sync
doesn't reintroduce the original line.

Component and page CSS is *translated*, not copied — see CLAUDE.md,
"Implementing a Claude Design handoff". Port the prototype's rules near-verbatim
onto our tokens, but re-express structure through our Blazor components.
