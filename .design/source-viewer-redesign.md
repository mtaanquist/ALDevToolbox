# Object Explorer source viewer — static SSR + JS sprinkle

This document proposes replacing the current Blazor Server-interactive source-file viewer (`/object-explorer/file/{FileId}`) with a static-SSR-plus-vanilla-JS architecture. It does not propose changing any other page in the toolbox.

**Status:** proposal. No code in this doc has been written. If approved, it lands as a follow-up after the current `object-explorer-symbols` integration branch merges to `main`, and runs side-by-side with the existing viewer behind a route flag for one iteration before the old path is removed.

## Why now

The file viewer has accumulated a list of bugs whose root cause is the same architectural impedance mismatch, not any one piece of code. The pattern:

| Bug | Surfaced as | Root cause |
|---|---|---|
| Editor renders unstyled after same-file outline click | `cm-editor`'s `ϵ`-prefixed classes lose their style rules | Blazor's enhanced-nav streaming diff strips CodeMirror's runtime-injected `<style>` tags from `<head>` |
| Outline link to a different `?line=` in the same file silently does nothing | No scroll | Same-page `Nav.NavigateTo` runs through the enhanced-nav pipeline anyway |
| `InvalidOperationException: JavaScript interop calls cannot be issued at this time` opening any release | 500 on first navigation | `OnInitializedAsync` runs during static prerender; the URL-sync helper called JS interop unconditionally |
| Click handler runs, state mutates, but the editor doesn't scroll | "Nothing happens" | `JS.InvokeVoidAsync` throwing inside the handler swallows auto-`StateHasChanged`; no re-render fires |
| Editor scrolls but horizontal column is shifted | First few characters cut off after every jump | CodeMirror's `scrollIntoView` defaults to `x: "nearest"` and the page-level state leaks across navigations |
| `scrollToLine` is called twice per click | Wasted JS roundtrip and a flash glitch | Explicit `StateHasChanged` + auto-`StateHasChanged` both queue renders; `_mountedScrollSequence` was assigned after the `await`, so the second render saw the stale value |

Each individual fix is small; the cumulative cost is large. Every interaction on this page — filter the outline, click an outline row, Cmd-click in the code, update the URL bar — round-trips to the server, even though every effect is local to the browser DOM. The server doesn't need to know what line the user is looking at to render the next frame; it doesn't even need to know the user filtered the outline. Routing those interactions through SignalR is the *cause* of the impedance with CodeMirror, not an unrelated detail.

## What the page actually does

Strip away the framework and the source viewer's user-visible job is small:

1. Render a `.al` source file with AL syntax highlighting in a CodeMirror editor.
2. Render an outline of objects and sub-symbols (procedures, fields, triggers) on the right.
3. Allow filtering that outline by name as the user types.
4. Scroll the editor to a line when the user clicks an outline entry.
5. Update the browser URL bar to reflect the scrolled-to line.
6. Cmd/Ctrl-click an identifier in the code to navigate to its definition.
7. Right-click an identifier for "Find references" (which leaves the page) or "Find in this file" (which shows a local results panel).

Of those, exactly one — Find references — leaves the page. Everything else is in-document interaction over data the server already shipped down on the initial render. The current architecture treats all seven as Blazor events; six of them have no business being Blazor events.

## Proposed architecture

A single Razor page renders the viewer statically — no `@rendermode InteractiveServer`, no circuit, no auto-`StateHasChanged`. The server fetches the file plus its outline plus its declarations from the database and renders them into the HTML response. A small `source-viewer.js` module — order of 100–200 lines — wires up the client-side affordances against the already-rendered DOM. The CodeMirror integration stays as-is.

The shape:

```razor
@page "/object-explorer/file/{FileId:long}"
@attribute [Authorize]
@inject ALDevToolbox.Services.ObjectExplorer.ObjectExplorerService ObjectExplorer

<div class="source-viewer"
     data-file-id="@FileId"
     data-initial-line="@(_highlightLine?.ToString() ?? "")">

    <header>…breadcrumb + file path…</header>

    <div class="source-viewer__layout">
        <div class="source-viewer__code"
             data-content="@_file.Content"
             data-declarations="@System.Text.Json.JsonSerializer.Serialize(_declarations)"></div>

        <aside class="source-viewer__outline">
            <h2>Outline</h2>
            <input class="source-viewer__outline-filter" placeholder="Filter symbols…" />
            @foreach (var group in groups)
            {
                <section data-section-key="@group.Key">
                    <button class="source-viewer__outline-section-toggle">
                        <span>@group.Title</span> <span>(@group.Items.Count)</span>
                    </button>
                    <ul>
                        @foreach (var entry in group.Items)
                        {
                            <li>
                                @if (entry.Item.ObjectId is { } oid)
                                {
                                    <a href="@($"/object-explorer/object/{oid}")">…</a>
                                }
                                else
                                {
                                    <a href="@($"/object-explorer/file/{FileId}?line={entry.Item.LineNumber}")"
                                       data-line="@entry.Item.LineNumber">…</a>
                                }
                            </li>
                        }
                    </ul>
                </section>
            }
        </aside>
    </div>
</div>

<script type="module" src="/source-viewer.js"></script>
```

What's gone from the current implementation:

- `@rendermode InteractiveServer` on the route — the page is server-rendered HTML, full stop.
- The `_scrollSequence` / `ScrollSequence` parameter pair — there's no Blazor parent to push state to.
- `JumpToLineInThisFileAsync`, the explicit `StateHasChanged()`, the try/catch around `history.replaceState`, the `_isInteractive` flag — all replaced by direct JS.
- `OnInitializedAsync` reading `?line=` from `HttpContext.Request.Query` — replaced by client-side `URLSearchParams`.
- `CodeViewer.razor`'s parameter-diffing render loop — replaced by a single `mount()` call from `source-viewer.js`.

What stays as-is:

- `ObjectExplorerService.GetFileAsync` / `GetFileOutlineAsync` / `ListDeclarationsInFileAsync` — same queries, same DTOs.
- `SourceFileOutlineGrouper` — pure function, runs server-side once to produce the grouped HTML.
- `AlSymbolExtractor`, the canonicaliser, the import pipeline — entirely unaffected, this is a server-side data layer.
- `code-editor.js` — mostly unaffected; `mountReadOnly` is called from `source-viewer.js` instead of through Blazor.

### What `source-viewer.js` does

A single module with a handful of small responsibilities:

```javascript
// source-viewer.js (sketch)
import { mountReadOnly, scrollToLine } from "/code-editor.js";

async function init() {
    const root = document.querySelector(".source-viewer");
    const fileId = Number(root.dataset.fileId);
    const codeHost = root.querySelector(".source-viewer__code");
    const editorId = await mountReadOnly(
        codeHost,
        codeHost.dataset.content,
        "al",
        { declarations: JSON.parse(codeHost.dataset.declarations) });

    // 1. Initial scroll from ?line=
    const initial = Number(new URLSearchParams(location.search).get("line"));
    if (initial >= 1) scrollToLine(editorId, initial, true);

    // 2. Outline filter — DOM filter, no roundtrip.
    const filter = root.querySelector(".source-viewer__outline-filter");
    filter.addEventListener("input", () => applyFilter(root, filter.value));

    // 3. Section toggles.
    root.querySelectorAll(".source-viewer__outline-section-toggle")
        .forEach(b => b.addEventListener("click", e => toggleSection(b.parentElement)));

    // 4. Same-file outline clicks — intercept, scroll, replaceState.
    root.querySelectorAll("a[data-line]").forEach(a => {
        a.addEventListener("click", e => {
            // Let middle-click / cmd-click open in a new tab as usual.
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey) return;
            e.preventDefault();
            const line = Number(a.dataset.line);
            scrollToLine(editorId, line, true);
            history.replaceState(null, "", `/object-explorer/file/${fileId}?line=${line}`);
        });
    });

    // 5. Cmd-click + right-click gestures stay wired through code-editor.js's
    //    existing dotNetRef path, but the callback now POSTs to a JSON
    //    endpoint and uses location.assign for cross-file targets.
}
init();
```

100 lines of code, no framework dependencies, no lifecycle to reason about. The outline filter and section toggle are pure DOM operations; the scroll is one call into the existing CodeMirror module; the URL update is one line.

### Cmd/Ctrl-click → "Go to definition"

The existing Blazor pipeline (`HandleGoToDefinition` → `ObjectExplorer.GoToDefinitionAsync` → `Nav.NavigateTo`) becomes a single JSON endpoint:

```
GET /api/object-explorer/files/{fileId}/goto?line=N&column=M
→ 200 { "fileId": 42, "lineNumber": 18 } | 204 No Content
```

`source-viewer.js` calls it on Cmd-click, then either calls `scrollToLine` + `history.replaceState` (same file) or `location.assign(newUrl)` (different file). No state mutation, no `StateHasChanged`, no `_isInteractive` flag, no try/catch around enhanced-nav timing.

### Right-click → "Find references"

The current implementation already navigates to a different page (`/object-explorer/object/{symbolId}#find-references`). That's a real navigation — a plain anchor on the menu item, no JS interop needed. The right-click menu itself is rendered by `code-editor.js`; only the `onClick` handler changes from "invoke .NET callback" to "set `location.href`".

### Right-click → "Find in this file"

A pure client-side operation: take the clicked word, scan the editor's document for matches via CodeMirror's `view.state.doc.iterLines()`, render the results into a panel below the outline. No server call needed — the file content is already in the editor's state. The current implementation routes this through a `FindInFileAsync` service call which then returns occurrences; that round-trip disappears.

## How planned features fit

Three features still on the roadmap shape this decision, because they're the ones that justify the refactor's cost:

### Diffing the same file across releases

A side-by-side diff of `Foo.Codeunit.al` from `BC 25.18 DK` against the same file from `BC 28.1 DK` is conceptually two file viewers in one page. Under the current architecture, that means two `<CodeViewer>` components, two `_scrollSequence` parameter pairs, two parent re-render cycles, and one shared SignalR circuit serialising every interaction.

Under the proposed architecture, it's two `mountReadOnly` calls in one `source-viewer.js`, each instance carrying its own `editorId`. Scroll synchronisation between panes (a typical diff affordance — when the left pane scrolls, the right scrolls in lockstep) is a single `scroll` event listener that calls `scrollToLine` on the other instance. No Blazor parameter ceremony, no double-render dedup logic.

The diff data itself — the per-line "added / removed / changed" classifications — is already a server-side responsibility (the existing `FileDiff.razor` does this for the legacy path). That stays a server-side query; the client just consumes a JSON payload of `{ leftFileId, rightFileId, lineClasses }` and stamps decorations via CodeMirror's `lineDecorations`.

### Find references (cross-app)

The heavy SQL is server-side and stays server-side — this is exactly the kind of query the symbol-package join enables (`SearchReferencesAsync`, recursive-CTE on `oe_module_references`). The viewer's role is just: render the link that takes the user to the references page. That's already a cross-page navigation; under the proposed architecture it's a plain `<a href>` instead of an `OnFindReferences` event callback. The change is *less* code than today, not more.

### Procedure-level Find references

The deferred feature ("procedure method names need a separate query shape against `oe_module_symbols`") becomes the same shape as object-level: a JSON-shaped lookup + a navigation. Same anchor pattern. No new infrastructure on the viewer side.

### Variable-receiver-aware Cmd-click

`myCust.Insert()` resolving to `Customer.Insert()` needs the variable-type map. That's a server-side resolution: the client passes `(fileId, line, column)`, the server walks the variable's declared type, returns a target. Same `goto` endpoint shape — the JSON body grows a field, the JS doesn't change.

### Resolvable-token underlines

The deferred cosmetic: a faint underline on every token that has a known navigation target. Under the proposed architecture, the server includes a `resolvables: [{line, columnStart, columnEnd}]` array in the initial page data; `code-editor.js`'s existing `buildResolvableDecorationExtensions` consumes it at mount time. No further wiring needed.

## Migration

The route can ship side-by-side: register `/object-explorer/file/{FileId}` as the new static-SSR page and keep the old interactive one at `/object-explorer/file-legacy/{FileId}` for one release, with a feature flag (env var) deciding which one the outline rows link to. Compare in production against the same imported releases for an iteration. When the new path proves stable, the legacy route gets deleted in a single commit.

The two pages share `ObjectExplorerService`, `SourceFileOutlineGrouper`, and the underlying `code-editor.js` module verbatim. The diff is concentrated in the razor file and the new `source-viewer.js`. Risk surface is small.

## What we explicitly don't do

- **Move to Blazor WebAssembly for this page.** It would solve the SignalR-roundtrip problem and let us keep one framework, but it pays a ~2 MB WASM payload for a page that doesn't need much .NET logic in the browser, and per-page rendermode mixing in Blazor 8+ is still fragile around route boundaries. If a future page wants offline editing or genuinely complex client-side state, revisit.
- **Build a full SPA frontend.** The release detail page, the admin pages, the snippet browser, every form in the toolbox is the right shape for Blazor Server interactive — *a form with server-owned state*. Refactoring those to a SPA would be a year of work and isn't justified by any single page. The static-SSR approach is precisely scoped to "this page is a viewer, not a form".
- **Replace CodeMirror.** It's the right tool. Every issue we've hit is at the Blazor↔JS boundary, not at the JS↔editor boundary.
- **Eliminate the C# import pipeline.** All the work on canonicalising paths, header-based file linking, AL symbol extraction, the `oe_*` schema — entirely unaffected. This proposal touches only what the *user* sees and does, not what's stored in the database.

## Risk

The honest risks:

- **Two paradigms in the codebase.** One Blazor Server interactive page (release detail, admin pages) and one static-SSR page (the file viewer) is a mode the project hasn't carried before. A newcomer has to read both `OeReleaseDetail.razor` and `SourceFileViewer.razor` to learn "when do we use which?". Mitigation: clear convention — *forms get interactive, viewers get static* — documented at the top of both files and in this `.design/` doc.
- **JS without TypeScript or a bundler.** `source-viewer.js` would be plain ES modules served from `wwwroot/`, same as `code-editor.js`. The existing code-editor.js is already 800+ lines without those amenities and it has held up; another 100–200 lines on the same model is not a step change. If `source-viewer.js` grows past ~500 lines, that's the signal to revisit.
- **Auth.** The page has `@attribute [Authorize]` today; the proposed page keeps it (the server still renders the page, auth still gates the response). The JSON endpoints (`/api/object-explorer/...`) get the same `[Authorize]` attribute. No new auth surface.
- **Telemetry / logging.** Outline clicks today are server events and could be logged; under the proposed architecture they're not. If we ever care about "which procedures do users click on most", we'd need either a thin `POST /api/object-explorer/track` endpoint or to accept that this signal is unobserved. The trade is worth it.

## Open questions

1. **Same page or new route during migration?** Side-by-side at `/object-explorer/file/{id}` (new) vs `/object-explorer/file-legacy/{id}` (old) is what's sketched above. Alternative: one route, env-var toggles which renderer. Side-by-side is friendlier to A/B comparison; the toggle is friendlier to "easy rollback in production". Worth a 1-line answer before implementation.
2. **Outline filter scope.** Today it's a substring match on the symbol name. Should the JS reimplementation match the substring elsewhere (signature, kind)? Either way, the server-side `SourceFileOutlineGrouper` shouldn't need to change — the filter is purely a render-time DOM filter.
3. **History entries on Cmd-click navigation.** `location.assign` adds a back-button entry; the current `Nav.NavigateTo` does the same. Consistent. But should *same-file* Cmd-click jumps push history or replace it? `history.replaceState` (current) keeps the history clean but loses "back to where I was before the jump". A `history.pushState` variant for cross-procedure jumps within a file might be preferable. Decide before implementation.
4. **`<noscript>` fallback.** With `source-viewer.js` doing the editor mount, a JS-disabled browser shows an empty `<div data-content="…">`. Either add a `<noscript>` fallback that renders the raw text in a `<pre>`, or accept that this is a developer tool and JS is required.

## What would make us regret this

If, six months from now, the file viewer wants any of: live updates as someone else edits the same file, drag-and-drop reordering of anything, an embedded REPL, real-time pair-debugging — the static-SSR shape will be in the way and we'd be back to wanting interactivity. None of those are on the roadmap. If any of them land, the cost of switching back is moderate (the JSON endpoints are the contract; whatever consumes them, Blazor WASM or a SPA, gets the same data shape).

The roadmap as stated — diff between releases, procedure-level Find references, variable-receiver-aware Cmd-click, resolvable-token underlines — all *strengthens* the case for the proposed architecture. Each is a one-line addition to `source-viewer.js` or a new JSON endpoint. None requires component lifecycle, parameter diffing, or `StateHasChanged`. The features ahead are the ones the current architecture would have fought us on most.
