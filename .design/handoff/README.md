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

Also checked in, as the PRs that ported them landed: `pages.css`,
`pages-forms.css`, `pages-power.css`, and the review sheets for the screens we
have translated so far (`PageList.dc.html`, `PageSettings.dc.html`,
`ComponentsPanel.dc.html`, `PageLauncher.dc.html`, `PageDashboard.dc.html`).

Not checked in yet — pull the layer you are actually porting, don't bulk-import:

- `pages-content.css` (archetypes 12-14)
- the remaining `*.dc.html` review sheets (`KitchenSink.dc.html` is the
  all-in-one view), `support.js`, `foundations.css` (review scaffolding, never shipped)
- `screens/*.png` — the design agent's own screenshots, useful for pixel-diffing

Pull one with the `DesignSync` tool:
`DesignSync{ method: "get_file", projectId: "63d872d4-...", path: "pages-forms.css" }`
and write it here alongside the rest, in the same PR that ports it.

## Re-syncing

The copy of `tokens.css` in this folder mirrors the design project exactly, and
`ALDevToolbox/wwwroot/tokens.css` mirrors this one. All three are byte-identical
and should stay that way — that is what makes a re-sync diff readable:

```
diff .design/handoff/tokens.css ALDevToolbox/wwwroot/tokens.css   # must be empty
```

The same holds for `components.css`, `pages-forms.css` and `pages.css`: push the
app copy upstream with `DesignSync`, then copy it here, so all three match. PR 9a
found them ~220 lines apart, because earlier corrections went upstream but the
local copy was never re-pulled. Check the diff when you touch one.

Anything that shows up there is drift. Fix it by deciding which side is right,
changing **the design project**, and re-pulling — never by patching one copy.

**Corrections go upstream, not into the app.** When the port finds a real
problem with the handoff, fix it in the design project so the next re-sync keeps
the fix. Done once already: the token layer originally specified the bare Segoe
UI stack and no web fonts, which cannot work off Windows — Segoe UI is not
licensable for web embedding, and the stack fell through to Tahoma/Helvetica
rather than the platform's own UI font. `--font-sans` now names Segoe first,
then **Selawik** (Microsoft's OFL-1.1 metric-compatible replacement, vendored in
`wwwroot/fonts/` and declared in `wwwroot/fonts.css`), then `system-ui`. That
correction was written back to the design project, so this copy and the app copy
both match upstream again.

Push a correction with `DesignSync`: `finalize_plan` (writes, deletes, and
`localDir` pointing at this folder), then `write_files` with a `localPath`.

Component and page CSS is *translated*, not copied — see CLAUDE.md,
"Implementing a Claude Design handoff". Port the prototype's rules near-verbatim
onto our tokens, but re-express structure through our Blazor components.
