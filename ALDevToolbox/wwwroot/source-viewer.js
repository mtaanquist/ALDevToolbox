// Object Explorer source-file viewer — client glue.
//
// The page is server-rendered HTML (no Blazor circuit). This module mounts
// the CodeMirror viewer against the rendered DOM and wires every
// in-document interaction directly to JS handlers, with a JSON-endpoint
// roundtrip for the gestures that need server data. The shape mirrors
// code-editor.js's expected dotNetRef interface so we can reuse
// mountReadOnly verbatim. See .design/source-viewer-redesign.md.

// Forward this module's cache-bust query (?v=…) to its imports so
// /code-editor.js doesn't stay cached after a deploy that bumped both.
const moduleVersion = new URL(import.meta.url).searchParams.get("v") ?? "";
const codeEditorUrl = moduleVersion ? `/code-editor.js?v=${moduleVersion}` : "/code-editor.js";
const { mountReadOnly, scrollToLine, openSearch } = await import(codeEditorUrl);

const FILE_URL_PREFIX = "/object-explorer/file/";

function init() {
    const root = document.querySelector(".source-viewer");
    if (!root) return;

    const codeHost = root.querySelector(".source-viewer__code");
    if (!codeHost) return;

    // Guard against double-mount via Blazor enhanced navigation.
    if (codeHost.querySelector(".cm-editor")) return;

    const fileId = Number(root.dataset.fileId);
    if (!Number.isFinite(fileId)) return;

    const initialLineAttr = root.dataset.initialLine;
    const initialLine = initialLineAttr
        ? Number(initialLineAttr)
        : Number(new URLSearchParams(location.search).get("line"));

    const declarations = parseJsonAttr(codeHost.dataset.declarations) ?? [];
    const resolvables = parseJsonAttr(codeHost.dataset.resolvables) ?? [];
    const content = codeHost.dataset.content ?? "";
    const language = codeHost.dataset.language ?? "al";

    // Clear the data-content payload from the DOM — the editor owns it now.
    codeHost.removeAttribute("data-content");
    codeHost.removeAttribute("data-declarations");
    codeHost.removeAttribute("data-resolvables");

    const notice = root.querySelector(".source-viewer__notice");
    const tabs = new TabController(root);

    const jsBridge = {
        invokeMethodAsync(method, ...args) {
            switch (method) {
                case "OnFindReferences":
                    return onFindReferences(args[0]);
                case "OnFindReferencesAt":
                    return onFindReferencesAt(args[0], args[1]);
                case "OnGoToDefinition":
                    return onGoToDefinition(args[0], args[1]);
                case "OnFindInFile":
                    return onFindInFile(args[0], args[1]);
                default:
                    return Promise.resolve();
            }
        },
    };

    const editorId = mountReadOnly(codeHost, content, language, {
        declarations,
        resolvables,
        dotNetRef: jsBridge,
        // VS Code-style status bar at the bottom of the editor. Shows
        // cursor line/col, total lines, and selection size when a range
        // is active. The static "1,073 line(s)" caption under the page
        // heading moves into this live readout.
        statusBar: true,
    });

    if (Number.isFinite(initialLine) && initialLine >= 1) {
        requestAnimationFrame(() => scrollToLine(editorId, initialLine, true));
    }

    wireOutlineFilter(root);
    wireSectionToggles(root);
    wireSameFileLinks(root, editorId, fileId);
    wireOutlineFindReferences(root);
    wireRefsCloseButton(root);
    wireSearchShortcut(root, editorId);
    wirePopstate(root, editorId);
    wireOutlineResizer(root);

    // If the server already rendered a session into the references panel's
    // data-session attribute (page loaded with ?refSet=token), parse it
    // and render the panel client-side so all rendering paths funnel
    // through renderReferencesPanel.
    const refsPanel = root.querySelector('[data-panel="references"]');
    if (refsPanel) {
        const inlineSession = parseJsonAttr(refsPanel.dataset.session);
        refsPanel.removeAttribute("data-session");
        if (inlineSession) {
            renderReferencesPanel(root, inlineSession, fileId, editorId);
        }
    }

    /// Right-click anywhere on an outline row to "Find references" for
    /// the symbol it represents. Procedure / field / trigger rows mint
    /// a member-scoped session (server side composes declarations +
    /// owner-type refs + call-site refs once those land). Object rows
    /// mint the existing object-scoped session.
    function wireOutlineFindReferences(panelRoot) {
        const outlinePanel = panelRoot.querySelector('[data-panel="outline"]');
        if (!outlinePanel) return;
        outlinePanel.addEventListener("contextmenu", e => {
            const row = e.target instanceof Element
                ? e.target.closest(".source-viewer__outline-item")
                : null;
            if (!row) return;
            const symbolId = row.dataset.symbolId;
            const objectId = row.dataset.objectId;
            if (!symbolId && !objectId) return;
            e.preventDefault();
            openOutlineRefsMenu(e.clientX, e.clientY, row, symbolId, objectId);
        });
    }

    function openOutlineRefsMenu(x, y, row, symbolId, objectId) {
        // One-item menu for now: "Find references". A second item ("Go to
        // definition") would be redundant — the outline row IS the
        // declaration; left-click jumps to it.
        const menu = document.createElement("div");
        menu.className = "source-viewer__outline-menu";
        menu.style.left = x + "px";
        menu.style.top = y + "px";

        const item = document.createElement("button");
        item.type = "button";
        item.className = "source-viewer__outline-menu-item";
        item.textContent = "Find references";
        item.addEventListener("click", async () => {
            menu.remove();
            if (symbolId) {
                await mintMemberSession(symbolId);
            } else if (objectId) {
                await mintObjectSession(objectId);
            }
        });
        menu.appendChild(item);

        document.body.appendChild(menu);
        const close = () => menu.remove();
        document.addEventListener("click", close, { once: true });
        document.addEventListener("scroll", close, { once: true, capture: true });
    }

    async function mintMemberSession(symbolId) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/references/sessions/from-member-symbol/${symbolId}`,
                { credentials: "same-origin" });
            if (!res.ok) {
                showNotice("Couldn't mint references for that symbol.");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("from-member-symbol failed:", err);
            showNotice("Couldn't reach the server.");
        }
    }

    async function mintObjectSession(objectId) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/references/sessions/from-symbol/${objectId}`,
                { credentials: "same-origin" });
            if (!res.ok) {
                showNotice("Couldn't mint references for that object.");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("from-symbol failed:", err);
            showNotice("Couldn't reach the server.");
        }
    }

    async function onFindReferencesAt(line, column) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/references/sessions/at-position?fileId=${fileId}&line=${line}&column=${column}`,
                { credentials: "same-origin" });
            if (res.status === 204 || res.status === 404) {
                // The server couldn't resolve the clicked token to a known
                // object. Procedure / field / variable references aren't
                // tracked yet (the import pipeline only records
                // object-to-object references), so this is expected for
                // anything that isn't an object name like a table or
                // codeunit. See .design/source-viewer-redesign.md
                // "Procedure-level Find references".
                showNotice("Find references currently works only for object names (tables, codeunits, pages, etc.). Procedure and field references coming soon.");
                return;
            }
            if (!res.ok) {
                showNotice("Couldn't search references (server error).");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("Find references at position failed:", err);
            showNotice("Couldn't reach the server.");
        }
    }

    async function onFindReferences(symbolId) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/references/sessions/from-symbol/${symbolId}`,
                { credentials: "same-origin" });
            if (!res.ok) {
                location.assign(`/object-explorer/object/${symbolId}#find-references`);
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("Find references failed:", err);
            location.assign(`/object-explorer/object/${symbolId}#find-references`);
        }
    }

    /// Render the References panel client-side and stash the token in the
    /// URL via replaceState so a refresh keeps the panel visible. Doesn't
    /// navigate — the user stays on their current file and clicks results
    /// to jump.
    function applyReferenceSession(session) {
        if (!session) return;
        renderReferencesPanel(root, session, fileId, editorId);
        tabs.show("references");
        tabs.activate("references");
        const url = new URL(location.href);
        url.searchParams.set("refSet", session.token);
        history.replaceState(null, "", url.pathname + url.search);
    }

    async function onGoToDefinition(line, column) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/files/${fileId}/goto?line=${line}&column=${column}`,
                { credentials: "same-origin" });
            if (res.status === 204) {
                showNotice("No definition found for that token.");
                return;
            }
            if (!res.ok) {
                showNotice("Couldn't resolve that token (server error).");
                return;
            }
            const target = await res.json();
            if (target.fileId === fileId) {
                jumpInThisFile(target.lineNumber);
                return;
            }
            location.assign(`${FILE_URL_PREFIX}${target.fileId}?line=${target.lineNumber}${preservedQueryTail()}`);
        } catch (err) {
            console.warn("Go to definition failed:", err);
            showNotice("Couldn't reach the server.");
        }
    }

    async function onFindInFile(line, column) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/files/${fileId}/find-in-file?line=${line}&column=${column}`,
                { credentials: "same-origin" });
            if (res.status === 204) {
                renderFindResults(null);
                return;
            }
            if (!res.ok) {
                showNotice("Couldn't search this file (server error).");
                return;
            }
            const data = await res.json();
            renderFindResults(data);
        } catch (err) {
            console.warn("Find in file failed:", err);
            showNotice("Couldn't reach the server.");
        }
    }

    function renderFindResults(data) {
        const findHost = root.querySelector(".source-viewer__find-host");
        if (!findHost) return;
        findHost.innerHTML = "";
        if (!data) {
            tabs.show("find", false);
            return;
        }
        const heading = document.createElement("h2");
        heading.className = "source-viewer__outline-find-heading";
        heading.textContent = `Find "${data.word}"`;
        findHost.appendChild(heading);

        if (!data.occurrences || data.occurrences.length === 0) {
            const p = document.createElement("p");
            p.className = "muted";
            p.textContent = "No occurrences in this file.";
            findHost.appendChild(p);
        } else {
            const count = document.createElement("p");
            count.className = "muted";
            count.textContent = `${data.occurrences.length.toLocaleString()} occurrence(s)`;
            findHost.appendChild(count);

            const list = document.createElement("ul");
            list.className = "source-viewer__find-list";
            for (const occ of data.occurrences) {
                const li = document.createElement("li");
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "source-viewer__outline-link";
                btn.addEventListener("click", () => jumpToLineInThisFile(occ.line));

                const lineLabel = document.createElement("span");
                lineLabel.className = "source-viewer__outline-kind";
                lineLabel.textContent = `L${occ.line}`;
                btn.appendChild(lineLabel);

                const snip = document.createElement("code");
                snip.className = "source-viewer__find-snippet";
                snip.textContent = (occ.lineText ?? "").trim();
                btn.appendChild(snip);

                li.appendChild(btn);
                list.appendChild(li);
            }
            findHost.appendChild(list);
        }

        tabs.show("find", true);
        tabs.activate("find");
    }

    function jumpToLineInThisFile(line) {
        jumpInThisFile(line);
    }

    /// Jumps to a line and pushes a history entry so the browser back
    /// button restores the previous position. Outline / Find-in-file
    /// / Cmd-click jumps all route through here. The URL preserves any
    /// non-line query (e.g. ?refSet=) so the references panel survives.
    function jumpInThisFile(line) {
        scrollToLine(editorId, line, true);
        const url = `${FILE_URL_PREFIX}${fileId}?line=${line}${preservedQueryTail()}`;
        // Skip the push when the URL is identical to the current one —
        // back button shouldn't have to press through duplicates.
        if (location.pathname + location.search !== url) {
            history.pushState(null, "", url);
        }
    }

    /// Wires window.popstate so the editor scrolls to whatever line the
    /// URL points at after back/forward navigation. The page itself is
    /// the same SSR document; only the line jumps.
    function wirePopstate(_root, eid) {
        window.addEventListener("popstate", () => {
            const ln = Number(new URLSearchParams(location.search).get("line"));
            if (Number.isFinite(ln) && ln >= 1) {
                scrollToLine(eid, ln, true);
            }
        });
    }

    function showNotice(text) {
        if (!notice) return;
        notice.textContent = text;
        notice.hidden = false;
    }
    function clearNotice() {
        if (!notice) return;
        notice.textContent = "";
        notice.hidden = true;
    }
}

// ── Tab controller ───────────────────────────────────────────────

class TabController {
    constructor(root) {
        this.root = root;
        this.tabs = Array.from(root.querySelectorAll(".source-viewer__tab"));
        this.panels = Array.from(root.querySelectorAll(".source-viewer__panel"));
        this.tabs.forEach(t => t.addEventListener("click", () => this.activate(t.dataset.tab)));
    }

    activate(name) {
        for (const tab of this.tabs) {
            const match = tab.dataset.tab === name;
            tab.classList.toggle("is-active", match);
            tab.setAttribute("aria-selected", match ? "true" : "false");
        }
        for (const panel of this.panels) {
            panel.classList.toggle("is-active", panel.dataset.panel === name);
        }
    }

    /// Make a tab visible (or hide it). The corresponding panel's data-panel
    /// attribute and the tab's data-tab attribute must match.
    show(name, visible = true) {
        const tab = this.tabs.find(t => t.dataset.tab === name);
        if (tab) tab.hidden = !visible;
        const panel = this.panels.find(p => p.dataset.panel === name);
        if (panel) panel.hidden = !visible;
    }
}

// ── References panel renderer ────────────────────────────────────

function renderReferencesPanel(root, session, fileId, editorId) {
    const panel = root.querySelector('[data-panel="references"]');
    if (!panel) return;
    panel.innerHTML = "";
    panel.hidden = false;

    const header = document.createElement("div");
    header.className = "source-viewer__refs-header";
    const label = document.createElement("p");
    label.className = "muted source-viewer__refs-target";
    label.textContent = session.targetLabel ?? "References";
    const close = document.createElement("button");
    close.type = "button";
    close.className = "btn btn--sm source-viewer__refs-close";
    close.dataset.action = "close-refs";
    close.textContent = "Close";
    close.title = "Close reference set";
    header.appendChild(label);
    header.appendChild(close);
    panel.appendChild(header);

    const tabCountEl = root.querySelector('.source-viewer__tab[data-tab="references"] .source-viewer__tab-count');
    const tabBtn = root.querySelector('.source-viewer__tab[data-tab="references"]');
    if (tabBtn) tabBtn.hidden = false;

    const count = session.results?.length ?? 0;
    if (tabCountEl) {
        tabCountEl.textContent = count.toLocaleString();
    } else if (tabBtn) {
        const c = document.createElement("span");
        c.className = "source-viewer__tab-count";
        c.textContent = count.toLocaleString();
        tabBtn.appendChild(c);
    }

    if (count === 0) {
        const p = document.createElement("p");
        p.className = "muted";
        p.textContent = "No references in this Release's chain.";
        panel.appendChild(p);
    } else {
        // Group by category so declarations / calls / indirect refs render
        // under their own headings. Order matters: declarations are the
        // most direct match, calls the actual usages (phase 2), owner_type
        // the indirect-via-type bucket, and any unknown category falls in
        // last. Within a group, server-side ordering (module, object,
        // line) is preserved.
        const groups = groupByCategory(session.results);
        for (const [category, rows] of groups) {
            const section = document.createElement("section");
            section.className = "source-viewer__refs-group";
            section.dataset.category = category;

            const heading = document.createElement("h3");
            heading.className = "source-viewer__refs-group-heading";
            heading.textContent = `${categoryLabel(category)} · ${rows.length.toLocaleString()}`;
            section.appendChild(heading);

            const list = document.createElement("ul");
            list.className = "source-viewer__refs-list";
            for (const r of rows) {
                list.appendChild(buildRefsRow(r, session, fileId, editorId));
            }
            section.appendChild(list);
            panel.appendChild(section);
        }
    }
}

function groupByCategory(rows) {
    const order = ["declaration", "call", "owner_type", "object"];
    const buckets = new Map();
    for (const c of order) buckets.set(c, []);
    for (const r of rows ?? []) {
        const c = r.category ?? "object";
        if (!buckets.has(c)) buckets.set(c, []);
        buckets.get(c).push(r);
    }
    // Drop empty buckets in their declared order; preserve insertion order
    // for any unknown categories that slipped through.
    return Array.from(buckets.entries()).filter(([, v]) => v.length > 0);
}

function categoryLabel(category) {
    switch (category) {
        case "declaration": return "Declarations";
        case "call":        return "Calls";
        case "owner_type":  return "Indirect references (via type)";
        case "object":      return "References";
        default:            return category;
    }
}

function buildRefsRow(r, session, fileId, editorId) {
    const li = document.createElement("li");
    li.className = "source-viewer__refs-item";

    const srcFid = r.sourceFileId;
    const ln = r.lineNumber;
    const objectKind = r.sourceObjectKind ?? "";
    if (srcFid != null && ln != null) {
        const inSameFile = srcFid === fileId;
        const a = document.createElement("a");
        a.href = `${FILE_URL_PREFIX}${srcFid}?line=${ln}&refSet=${encodeURIComponent(session.token)}`;
        if (inSameFile) {
            a.dataset.line = String(ln);
            a.addEventListener("click", e => {
                if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
                e.preventDefault();
                scrollToLine(editorId, ln, true);
                if (location.pathname + location.search !== a.href.replace(location.origin, "")) {
                    history.pushState(null, "", a.href);
                }
            });
        }
        const sub = r.memberName
            ? `${r.memberName}${r.memberSignature ?? ""}`
            : r.referenceKind;
        a.title = `${r.sourceFilePath ?? r.sourceModuleName} · L${ln} (${sub})`;
        appendRowTop(a, objectKind, r.sourceObjectName, `${r.sourceModuleName} · L${ln}`);
        if (r.memberName) {
            appendMemberRow(a, r.memberKind, r.memberName, r.memberSignature);
        }
        if (r.snippet) {
            const snip = document.createElement("code");
            snip.className = "source-viewer__refs-snippet";
            snip.textContent = r.snippet;
            a.appendChild(snip);
        }
        li.appendChild(a);
    } else {
        const a = document.createElement("a");
        a.href = `/object-explorer/object/${r.sourceObjectId}`;
        a.title = `${r.sourceModuleName} · ${r.sourceObjectName} (no source)`;
        appendRowTop(a, objectKind, r.sourceObjectName, `${r.sourceModuleName} · no source`);
        li.appendChild(a);
    }
    return li;
}

function appendRowTop(anchor, kind, name, meta) {
    const top = document.createElement("div");
    top.className = "source-viewer__refs-row-top";
    if (kind) {
        top.appendChild(buildKindBadge(kind));
    }
    const nameSpan = document.createElement("span");
    nameSpan.className = "source-viewer__refs-name";
    nameSpan.textContent = name;
    top.appendChild(nameSpan);
    const metaSpan = document.createElement("span");
    metaSpan.className = "source-viewer__refs-meta muted";
    metaSpan.textContent = meta;
    top.appendChild(metaSpan);
    anchor.appendChild(top);
}

function appendMemberRow(anchor, kind, name, signature) {
    const row = document.createElement("div");
    row.className = "source-viewer__refs-member-row";
    if (kind) {
        row.appendChild(buildKindBadge(kind));
    }
    const nameSpan = document.createElement("span");
    nameSpan.className = "source-viewer__refs-member-name";
    nameSpan.textContent = `${name}${signature ?? ""}`;
    row.appendChild(nameSpan);
    anchor.appendChild(row);
}

/// Renders a kind badge that matches the outline pane's pill-style chips.
/// Label text + colour family are derived from the raw kind string the
/// server hands us (procedure / table / event_publisher / …). Mirrors the
/// C# helpers (KindBadgeLabel / KindBadgeClass) in SourceFileViewer.razor —
/// keep the two in sync when a new symbol kind lands.
function buildKindBadge(kind) {
    const span = document.createElement("span");
    span.className = `source-viewer__outline-badge ${kindBadgeClass(kind)}`;
    span.textContent = kindBadgeLabel(kind);
    return span;
}

function kindBadgeLabel(kind) {
    switch ((kind ?? "").toLowerCase()) {
        case "field":                  return "field";
        case "trigger":                return "trigger";
        case "procedure":              return "proc";
        case "internal_procedure":     return "internal";
        case "protected_procedure":    return "protected";
        case "local_procedure":        return "local";
        case "event_publisher":        return "event pub";
        case "event_subscriber":       return "event sub";
        case "codeunit":               return "codeunit";
        case "table":                  return "table";
        case "tableextension":         return "table ext";
        case "page":                   return "page";
        case "pageextension":          return "page ext";
        case "report":                 return "report";
        case "reportextension":        return "report ext";
        case "xmlport":                return "xmlport";
        case "query":                  return "query";
        case "controladdin":           return "controladd";
        case "enum":                   return "enum";
        case "enumextension":          return "enum ext";
        case "interface":              return "interface";
        case "permissionset":          return "permset";
        case "permissionsetextension": return "permset ext";
        case "profile":                return "profile";
        default:                       return kind ?? "";
    }
}

function kindBadgeClass(kind) {
    switch ((kind ?? "").toLowerCase()) {
        case "field":                return "source-viewer__outline-badge--field";
        case "trigger":              return "source-viewer__outline-badge--trigger";
        case "event_publisher":
        case "event_subscriber":     return "source-viewer__outline-badge--event";
        case "procedure":
        case "internal_procedure":
        case "protected_procedure":
        case "local_procedure":      return "source-viewer__outline-badge--proc";
        default:                     return "source-viewer__outline-badge--object";
    }
}

function wireRefsCloseButton(root) {
    root.addEventListener("click", e => {
        const target = e.target instanceof Element ? e.target.closest('[data-action="close-refs"]') : null;
        if (!target) return;
        e.preventDefault();
        const panel = root.querySelector('[data-panel="references"]');
        if (panel) panel.hidden = true;
        const tab = root.querySelector('.source-viewer__tab[data-tab="references"]');
        if (tab) tab.hidden = true;
        const url = new URL(location.href);
        url.searchParams.delete("refSet");
        history.replaceState(null, "", url.pathname + url.search);
        // Activate Outline so the panel area isn't blank.
        const outlineTab = root.querySelector('.source-viewer__tab[data-tab="outline"]');
        if (outlineTab) outlineTab.click();
    });
}

// ── Ctrl/Cmd-F intercept ─────────────────────────────────────────
//
// CodeMirror's searchKeymap only fires when the editor has DOM focus.
// Browsers grab Ctrl/Cmd-F otherwise. Bind a window-level keydown that
// claims the shortcut for the editor whenever the source viewer is on
// screen.
function wireSearchShortcut(root, editorId) {
    window.addEventListener("keydown", e => {
        const isFind = e.key === "f" || e.key === "F";
        if (!isFind) return;
        if (!(e.ctrlKey || e.metaKey)) return;
        if (e.shiftKey || e.altKey) return;
        if (!document.contains(root)) return;
        e.preventDefault();
        openSearch(editorId);
    });
}

// ── Outline pieces (unchanged from prior version) ────────────────

// ── Outline resizer ──────────────────────────────────────────────
//
// Drag handle between the editor and the outline. Updates a CSS
// custom property on the layout so the outline column flexes without
// re-running React-style relayout, and persists the chosen width in
// localStorage so subsequent loads inherit the user's choice. Width
// is clamped to the same range the CSS uses (220–720px) — the panel
// stays readable, the editor still has room.

const OUTLINE_WIDTH_KEY = "aldt.source-viewer.outline-width";
const OUTLINE_WIDTH_MIN = 220;
const OUTLINE_WIDTH_MAX = 720;

function wireOutlineResizer(root) {
    const layout = root.querySelector(".source-viewer__layout");
    const handle = root.querySelector(".source-viewer__resizer");
    const outline = root.querySelector(".source-viewer__outline");
    if (!layout || !handle || !outline) return;

    // Rehydrate the last chosen width before the first paint of the
    // resizer would otherwise let the layout flash at the default.
    const stored = readStoredWidth();
    if (stored !== null) {
        layout.style.setProperty("--source-viewer-outline-width", stored + "px");
    }

    let pointerId = null;
    let startX = 0;
    let startWidth = 0;

    handle.addEventListener("pointerdown", e => {
        if (e.button !== 0) return;
        pointerId = e.pointerId;
        startX = e.clientX;
        startWidth = outline.getBoundingClientRect().width;
        handle.setPointerCapture(pointerId);
        handle.classList.add("is-dragging");
        document.body.style.cursor = "col-resize";
        e.preventDefault();
    });

    handle.addEventListener("pointermove", e => {
        if (pointerId === null || e.pointerId !== pointerId) return;
        // Drag right = handle moves right = outline narrower (it's on
        // the right of the editor). Subtract the delta so dragging the
        // visible handle towards the outline shrinks it intuitively.
        const delta = e.clientX - startX;
        const next = clamp(startWidth - delta, OUTLINE_WIDTH_MIN, OUTLINE_WIDTH_MAX);
        layout.style.setProperty("--source-viewer-outline-width", next + "px");
    });

    const endDrag = e => {
        if (pointerId === null || (e && e.pointerId !== pointerId)) return;
        try { handle.releasePointerCapture(pointerId); } catch { /* already released */ }
        pointerId = null;
        handle.classList.remove("is-dragging");
        document.body.style.cursor = "";
        const final = outline.getBoundingClientRect().width;
        storeWidth(final);
    };
    handle.addEventListener("pointerup", endDrag);
    handle.addEventListener("pointercancel", endDrag);

    // Keyboard accessibility — left/right arrow nudges the divider in
    // 20px steps so users without a mouse can still tune the column.
    handle.addEventListener("keydown", e => {
        const step = e.shiftKey ? 60 : 20;
        let delta = 0;
        if (e.key === "ArrowLeft") delta = step;       // grow outline
        else if (e.key === "ArrowRight") delta = -step; // shrink outline
        else return;
        e.preventDefault();
        const current = outline.getBoundingClientRect().width;
        const next = clamp(current + delta, OUTLINE_WIDTH_MIN, OUTLINE_WIDTH_MAX);
        layout.style.setProperty("--source-viewer-outline-width", next + "px");
        storeWidth(next);
    });
}

function clamp(v, lo, hi) {
    return Math.min(Math.max(v, lo), hi);
}

function readStoredWidth() {
    try {
        const raw = window.localStorage?.getItem(OUTLINE_WIDTH_KEY);
        if (!raw) return null;
        const n = Number(raw);
        if (!Number.isFinite(n)) return null;
        return clamp(n, OUTLINE_WIDTH_MIN, OUTLINE_WIDTH_MAX);
    } catch {
        return null;
    }
}

function storeWidth(px) {
    try {
        window.localStorage?.setItem(OUTLINE_WIDTH_KEY, String(Math.round(px)));
    } catch {
        /* storage disabled — width still applies for the session. */
    }
}

function wireOutlineFilter(root) {
    const filter = root.querySelector(".source-viewer__outline-filter");
    if (!filter) return;
    const sections = Array.from(root.querySelectorAll(".source-viewer__outline-section"));
    const empty = root.querySelector(".source-viewer__outline-empty");

    filter.addEventListener("input", () => {
        const needle = filter.value.trim().toLowerCase();
        let anyVisible = false;
        for (const section of sections) {
            const rows = Array.from(section.querySelectorAll(".source-viewer__outline-item"));
            let sectionVisible = false;
            for (const row of rows) {
                const name = (row.dataset.rowName ?? "").toLowerCase();
                const match = needle.length === 0 || name.includes(needle);
                row.hidden = !match;
                if (match) sectionVisible = true;
            }
            section.hidden = !sectionVisible;
            if (sectionVisible) anyVisible = true;
        }
        if (empty) empty.hidden = anyVisible || needle.length === 0;
    });
}

function wireSectionToggles(root) {
    const toggles = root.querySelectorAll(".source-viewer__outline-section-toggle");
    toggles.forEach(btn => {
        btn.addEventListener("click", () => {
            const section = btn.parentElement;
            if (!section) return;
            const open = section.classList.toggle("is-open");
            btn.setAttribute("aria-expanded", open ? "true" : "false");
            const chevron = btn.querySelector(".source-viewer__outline-section-chevron");
            if (chevron) chevron.classList.toggle("is-open", open);
            const list = section.querySelector(".source-viewer__outline-list");
            if (list) list.hidden = !open;
        });
    });
}

function wireSameFileLinks(root, editorId, fileId) {
    const links = root.querySelectorAll("a[data-line]");
    links.forEach(a => {
        a.addEventListener("click", e => {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            const line = Number(a.dataset.line);
            if (!Number.isFinite(line) || line < 1) return;
            e.preventDefault();
            scrollToLine(editorId, line, true);
            const url = `${FILE_URL_PREFIX}${fileId}?line=${line}${preservedQueryTail()}`;
            // pushState (not replace) so back button restores the prior
            // line position. Skip when URL is unchanged so back doesn't
            // press through duplicates.
            if (location.pathname + location.search !== url) {
                history.pushState(null, "", url);
            }
        });
    });
}

function preservedQueryTail() {
    const current = new URLSearchParams(location.search);
    current.delete("line");
    const rest = current.toString();
    return rest.length === 0 ? "" : `&${rest}`;
}

function parseJsonAttr(raw) {
    if (!raw) return null;
    try {
        return JSON.parse(raw);
    } catch (err) {
        console.warn("source-viewer: failed to parse data attribute", err);
        return null;
    }
}

/// First-load resilience: enhanced-nav into this page can run the module
/// before the patched .source-viewer DOM is queryable, AND Blazor's
/// enhanced-nav response diffing has been observed to skip script
/// execution on the first navigation entirely. Belt-and-braces:
///
///   1. Try init synchronously, plus across the first frame + tick.
///   2. Listen for DOMContentLoaded for full page loads.
///   3. Listen for Blazor's `enhancedload` event for SPA-style navs.
///   4. Watch the body via MutationObserver — if .source-viewer
///      appears later (Blazor finishing its DOM patch after the
///      module loaded), init runs the moment it lands.
///
/// init() is idempotent thanks to its own cm-editor guard, so calling
/// it repeatedly is harmless. The MutationObserver stays alive for the
/// session so subsequent enhanced navs to other source-viewer pages
/// also fire it.
function tryInit() {
    init();
    requestAnimationFrame(() => init());
    setTimeout(() => init(), 50);
    setTimeout(() => init(), 200);
}

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", tryInit, { once: true });
} else {
    tryInit();
}

if (typeof globalThis.Blazor !== "undefined" && globalThis.Blazor.addEventListener) {
    globalThis.Blazor.addEventListener("enhancedload", tryInit);
}

// MutationObserver fallback. Fires whenever .source-viewer appears
// in the DOM — whether through enhanced nav, a full page load, or
// anything else. Cheap to keep alive: we filter by selector and only
// re-call init when the editor isn't already mounted.
if (typeof MutationObserver !== "undefined") {
    const observer = new MutationObserver(() => {
        const root = document.querySelector(".source-viewer");
        if (root && !root.querySelector(".cm-editor")) {
            init();
        }
    });
    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    } else {
        // body not yet parsed — wait for DOMContentLoaded to attach.
        document.addEventListener("DOMContentLoaded", () => {
            observer.observe(document.body, { childList: true, subtree: true });
        }, { once: true });
    }
}
