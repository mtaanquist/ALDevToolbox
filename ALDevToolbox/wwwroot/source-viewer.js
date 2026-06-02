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
const { mountReadOnly, scrollToLine, openSearch, selectAll, containsNode } = await import(codeEditorUrl);

const FILE_URL_PREFIX = "/object-explorer/file/";

function init() {
    const roots = document.querySelectorAll(".source-viewer");
    if (roots.length === 0) return;
    const editorsByPane = [];
    roots.forEach(root => {
        const eid = initOne(root);
        if (eid !== null) {
            editorsByPane.push({ root, editorId: eid });
        }
    });

    // Compare-page scroll-sync: two .source-viewer--compare roots side-by-side.
    // Each pane's CodeMirror scroller gets a scroll listener that mirrors its
    // scrollTop onto the other pane, with a re-entrancy flag so the
    // mirror-back doesn't bounce.
    if (editorsByPane.length === 2
        && editorsByPane[0].root.classList.contains("source-viewer--compare")
        && editorsByPane[1].root.classList.contains("source-viewer--compare")) {
        wireCompareScrollSync(editorsByPane[0].root, editorsByPane[1].root);
    }
}

function wireCompareScrollSync(leftRoot, rightRoot) {
    const leftScroller = leftRoot.querySelector(".cm-scroller");
    const rightScroller = rightRoot.querySelector(".cm-scroller");
    if (!leftScroller || !rightScroller) return;
    let syncing = false;
    const link = (src, dst) => src.addEventListener("scroll", () => {
        if (syncing) return;
        syncing = true;
        dst.scrollTop = src.scrollTop;
        dst.scrollLeft = src.scrollLeft;
        requestAnimationFrame(() => { syncing = false; });
    });
    link(leftScroller, rightScroller);
    link(rightScroller, leftScroller);
}

function initOne(root) {
    const codeHost = root.querySelector(".source-viewer__code");
    if (!codeHost) return null;

    // Guard against double-mount via Blazor enhanced navigation.
    if (codeHost.querySelector(".cm-editor")) return null;

    const fileId = Number(root.dataset.fileId);
    // fileId is optional on the side-by-side compare page (each pane carries
    // a data-file-id but the cross-pane handlers don't use it). The rest of
    // the wiring only runs when this is a navigable single-file viewer.
    const isCompare = root.classList.contains("source-viewer--compare");

    const initialLineAttr = root.dataset.initialLine;
    const initialLine = initialLineAttr
        ? Number(initialLineAttr)
        : Number(new URLSearchParams(location.search).get("line"));

    const declarations = parseJsonAttr(codeHost.dataset.declarations) ?? [];
    const resolvables = parseJsonAttr(codeHost.dataset.resolvables) ?? [];
    // Procedure-like outline rows (start line + optional end line + name + kind)
    // drive the "in CheckDates (line 13)" suffix in the status bar. Maps the
    // editor's absolute line number to BC's procedure-relative stack-trace
    // numbering, where the `procedure` declaration counts as line 0.
    const procedures = parseJsonAttr(codeHost.dataset.procedures) ?? [];
    const content = codeHost.dataset.content ?? "";
    const language = codeHost.dataset.language ?? "al";

    // Clear the data-content payload from the DOM — the editor owns it now.
    codeHost.removeAttribute("data-content");
    codeHost.removeAttribute("data-declarations");
    codeHost.removeAttribute("data-resolvables");
    codeHost.removeAttribute("data-procedures");

    const notice = root.querySelector(".source-viewer__notice");
    const tabs = new TabController(root);

    // Track the last pointer position inside the editor so notice toasts
    // can pop up near the user's mouse, rather than at the bottom of the
    // outline panel where they may be off-screen. Updated on
    // mousemove/contextmenu/click anywhere inside the editor surface.
    const pointerTracker = { x: 0, y: 0, fresh: false };
    const updatePointer = (ev) => {
        pointerTracker.x = ev.clientX;
        pointerTracker.y = ev.clientY;
        pointerTracker.fresh = true;
    };
    root.addEventListener("mousemove", updatePointer);
    root.addEventListener("contextmenu", updatePointer);
    root.addEventListener("click", updatePointer);

    const jsBridge = {
        invokeMethodAsync(method, ...args) {
            switch (method) {
                case "OnFindReferences":
                    return onFindReferences(args[0]);
                case "OnFindMemberReferences":
                    return mintMemberSession(args[0]);
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

    // Diff overlay (compare page only). data-diff carries a JSON array
    // `[{line, kind}, …]` where kind ∈ inserted | deleted | modified
    // | imaginary. Convert to the {lineNumber: cssClass} shape mountReadOnly
    // already understands and pass through as lineDecorations.
    const diffData = parseJsonAttr(codeHost.dataset.diff);
    const lineDecorations = {};
    if (Array.isArray(diffData)) {
        for (const row of diffData) {
            if (!row || !Number.isFinite(row.line)) continue;
            lineDecorations[row.line] = `cm-diff-${row.kind}`;
        }
    }
    codeHost.removeAttribute("data-diff");

    const editorId = mountReadOnly(codeHost, content, language, {
        declarations,
        resolvables,
        lineDecorations,
        procedures,
        dotNetRef: jsBridge,
        // VS Code-style status bar at the bottom of the editor. Shows
        // cursor line/col, total lines, selection size when a range is
        // active, and — when the cursor sits inside a procedure — the
        // containing procedure name plus the BC stack-trace-style
        // procedure-relative line number ("in CheckDates (line 13)").
        statusBar: true,
    });

    // Compare-page panes don't carry the outline / refs / tabs DOM so the
    // wiring below would no-op anyway, but skipping it makes the flow
    // explicit. Likewise initial line isn't useful when both panes start
    // pinned to line 1.
    if (isCompare) {
        return editorId;
    }

    if (Number.isFinite(initialLine) && initialLine >= 1) {
        requestAnimationFrame(() => scrollToLine(editorId, initialLine, true));
    }

    wireOutlineFilter(root);
    wireSectionToggles(root);
    wireSameFileLinks(root, editorId, fileId);
    wireOutlineFindReferences(root);
    wireRefsCloseButton(root);
    wireSearchShortcut(root, editorId);
    wireSelectAllShortcut(root, editorId, codeHost);
    wirePopstate(root, editorId);
    wireOutlineResizer(root);
    if (Number.isFinite(fileId)) {
        wireFileDependencies(root, fileId);
    }

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

        // "Find system references" — built-in/system method calls (Insert,
        // Modify, SetRange, …) on the object. Object rows only; system calls
        // target a whole object, not a member. See #279.
        if (objectId) {
            const sysItem = document.createElement("button");
            sysItem.type = "button";
            sysItem.className = "source-viewer__outline-menu-item";
            sysItem.textContent = "Find system references";
            sysItem.addEventListener("click", async () => {
                menu.remove();
                await mintObjectSystemSession(objectId);
            });
            menu.appendChild(sysItem);
        }

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

    async function mintObjectSystemSession(objectId) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/system-references/sessions/from-object/${objectId}`,
                { credentials: "same-origin" });
            if (!res.ok) {
                showNotice("Couldn't mint system references for that object.");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("system-references from-object failed:", err);
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

    /// Shows a transient notice as a floating toast anchored near the
    /// user's last pointer position inside the editor surface. The
    /// toast fades out on its own after a short delay so the user
    /// isn't left looking at a stale status line at the bottom of the
    /// outline (which is frequently off-screen on tall files). Falls
    /// back to the bottom-of-outline notice element when we don't have
    /// a fresh pointer position (keyboard-driven gestures).
    function showNotice(text) {
        if (!text) return;
        if (pointerTracker.fresh) {
            showFloatingToast(text, pointerTracker.x, pointerTracker.y);
            return;
        }
        if (!notice) return;
        notice.textContent = text;
        notice.hidden = false;
    }
    function clearNotice() {
        if (!notice) return;
        notice.textContent = "";
        notice.hidden = true;
    }

    return editorId;
}

// ── Floating notice toast ────────────────────────────────────────
//
// The source-viewer used to surface "No definition found", "Server
// error", etc. into a tiny <p class="source-viewer__notice"> docked
// at the bottom of the outline. On long files that paragraph sits
// well below the viewport, so users never noticed the response to
// their gesture. A floating toast anchored to the most recent
// pointer position inside the editor keeps the feedback in view,
// then fades itself out so it doesn't linger.

let floatingToastEl = null;
let floatingToastHideTimer = 0;
let floatingToastRemoveTimer = 0;

const TOAST_VISIBLE_MS = 1800;   // Time the toast stays at full opacity.
const TOAST_FADE_MS = 350;        // Length of the fade-out transition.

function showFloatingToast(text, clientX, clientY) {
    clearTimeout(floatingToastHideTimer);
    clearTimeout(floatingToastRemoveTimer);

    if (!floatingToastEl) {
        floatingToastEl = document.createElement("div");
        floatingToastEl.className = "source-viewer__toast";
        floatingToastEl.setAttribute("role", "status");
        document.body.appendChild(floatingToastEl);
    }
    const el = floatingToastEl;
    el.textContent = text;
    el.style.transition = "";
    el.style.opacity = "1";
    el.style.pointerEvents = "none";

    // Position relative to the pointer. Default: just below + to the
    // right of the cursor so the text doesn't sit under the mouse
    // arrow. Flip across the boundary when we'd otherwise spill off
    // the viewport.
    el.style.left = "0px";
    el.style.top = "0px";
    el.hidden = false;
    const rect = el.getBoundingClientRect();
    const margin = 8;
    let x = clientX + 14;
    let y = clientY + 14;
    if (x + rect.width + margin > window.innerWidth) {
        x = Math.max(margin, clientX - rect.width - 14);
    }
    if (y + rect.height + margin > window.innerHeight) {
        y = Math.max(margin, clientY - rect.height - 14);
    }
    el.style.left = `${Math.round(x + window.scrollX)}px`;
    el.style.top  = `${Math.round(y + window.scrollY)}px`;

    floatingToastHideTimer = setTimeout(() => {
        el.style.transition = `opacity ${TOAST_FADE_MS}ms ease-out`;
        el.style.opacity = "0";
        floatingToastRemoveTimer = setTimeout(() => {
            el.hidden = true;
        }, TOAST_FADE_MS);
    }, TOAST_VISIBLE_MS);
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
        // Filter box — mirrors the Outline's, so you can narrow a long
        // reference set by object, enclosing member, target member, or the
        // snippet text. Wired after the sections are built.
        const filter = document.createElement("input");
        filter.type = "text";
        filter.className = "source-viewer__refs-filter";
        filter.placeholder = "Filter references…";
        filter.setAttribute("aria-label", "Filter references");
        panel.appendChild(filter);

        // Group references by their source object so every place that
        // touches the target clusters under one header — repeated calls
        // from the same table / page no longer scatter down a flat list.
        // Each row then shows the enclosing procedure / trigger so you can
        // tell where in the object's code the reference sits.
        const groups = groupByObject(session.results);
        for (const group of groups) {
            const section = document.createElement("section");
            // Re-use the outline section vocabulary so the chevron / toggle
            // affordance reads the same in both panels. Sections open by
            // default; the click handler on the toggle flips data state +
            // chevron + list hidden.
            section.className = "source-viewer__refs-group source-viewer__outline-section is-open";
            if (group.objectId != null) section.dataset.objectId = String(group.objectId);

            const toggle = document.createElement("button");
            toggle.type = "button";
            toggle.className = "source-viewer__outline-section-toggle";
            toggle.setAttribute("aria-expanded", "true");

            const chevron = document.createElement("span");
            chevron.className = "source-viewer__outline-section-chevron is-open";
            chevron.textContent = "›";
            toggle.appendChild(chevron);

            // The object kind badge + name is the section header now.
            if (group.objectKind) toggle.appendChild(buildKindBadge(group.objectKind));

            const title = document.createElement("span");
            title.className = "source-viewer__outline-section-title";
            title.textContent = group.objectName;
            toggle.appendChild(title);

            const countSpan = document.createElement("span");
            countSpan.className = "source-viewer__outline-section-count";
            countSpan.textContent = `(${group.rows.length.toLocaleString()})`;
            toggle.appendChild(countSpan);

            section.appendChild(toggle);

            const list = document.createElement("ul");
            list.className = "source-viewer__refs-list";
            for (const r of group.rows) {
                list.appendChild(buildRefsRow(r, session, fileId, editorId));
            }
            section.appendChild(list);

            toggle.addEventListener("click", () => {
                const open = section.classList.toggle("is-open");
                toggle.setAttribute("aria-expanded", open ? "true" : "false");
                chevron.classList.toggle("is-open", open);
                list.hidden = !open;
            });

            panel.appendChild(section);
        }

        const empty = document.createElement("p");
        empty.className = "muted source-viewer__refs-empty";
        empty.hidden = true;
        empty.textContent = "No references match the filter.";
        panel.appendChild(empty);

        wireRefsFilter(panel);
    }
}

// Filters the rendered reference rows by object / member / snippet text,
// mirroring wireOutlineFilter: hides non-matching rows, collapses sections
// with no surviving rows, and shows an empty-state when nothing matches.
function wireRefsFilter(panel) {
    const filter = panel.querySelector(".source-viewer__refs-filter");
    if (!filter) return;
    const sections = Array.from(panel.querySelectorAll(".source-viewer__refs-group"));
    const empty = panel.querySelector(".source-viewer__refs-empty");

    filter.addEventListener("input", () => {
        const needle = filter.value.trim().toLowerCase();
        let anyVisible = false;
        for (const section of sections) {
            const items = Array.from(section.querySelectorAll(".source-viewer__refs-item"));
            let sectionVisible = false;
            for (const item of items) {
                const hay = item.dataset.filter ?? "";
                const match = needle.length === 0 || hay.includes(needle);
                item.hidden = !match;
                if (match) sectionVisible = true;
            }
            section.hidden = !sectionVisible;
            if (sectionVisible) anyVisible = true;
        }
        if (empty) empty.hidden = anyVisible || needle.length === 0;
    });
}

// Within an object, order rows by reference category (declarations first,
// then call sites), then by line so they read top-to-bottom like the file.
const REFS_CATEGORY_ORDER = { declaration: 0, call: 1, implementation: 2, owner_type: 3, object: 4 };

function groupByObject(rows) {
    const groups = new Map();
    for (const r of rows ?? []) {
        const key = r.sourceObjectId ?? `${r.sourceObjectKind ?? ""}/${r.sourceObjectName ?? ""}`;
        let g = groups.get(key);
        if (!g) {
            g = {
                objectId: r.sourceObjectId,
                objectKind: r.sourceObjectKind ?? "",
                objectName: r.sourceObjectName ?? "",
                rows: [],
            };
            groups.set(key, g);
        }
        g.rows.push(r);
    }
    const list = Array.from(groups.values());
    for (const g of list) {
        g.rows.sort((a, b) => {
            const ca = REFS_CATEGORY_ORDER[a.category] ?? 9;
            const cb = REFS_CATEGORY_ORDER[b.category] ?? 9;
            if (ca !== cb) return ca - cb;
            return (a.lineNumber ?? 0) - (b.lineNumber ?? 0);
        });
    }
    // Stable, scannable order: by object kind, then name.
    list.sort((a, b) =>
        a.objectKind.localeCompare(b.objectKind) ||
        a.objectName.localeCompare(b.objectName));
    return list;
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
    // Searchable haystack for the panel filter: object, enclosing member,
    // target member, reference kind, and the code snippet.
    li.dataset.filter = [
        r.sourceObjectName, r.sourceMemberName, r.memberName, r.referenceKind, r.snippet,
    ].filter(Boolean).join(" ").toLowerCase();

    const srcFid = r.sourceFileId;
    const ln = r.lineNumber;
    const hasLoc = srcFid != null && ln != null;

    const a = document.createElement("a");
    if (hasLoc) {
        const inSameFile = srcFid === fileId;
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
    } else {
        a.href = `/object-explorer/object/${r.sourceObjectId}`;
    }

    // The object now lives in the section header, so the row leads with the
    // enclosing procedure / trigger ("where in the object") when we know it.
    // Falls back to the target member, then the reference category.
    if (r.sourceMemberName) {
        appendRefsTop(a, r.sourceMemberKind, r.sourceMemberName, r.lineNumber, false);
        // Still show the target member — what the enclosing member references.
        if (r.memberName) {
            appendMemberRow(a, r.memberKind, r.memberName, r.memberSignature);
        }
    } else if (r.memberName) {
        appendRefsTop(a, r.memberKind, r.memberName, r.lineNumber, false);
    } else {
        // Object-scope reference (variable_type, extends_target, …): no member
        // context, so lead with the humanised reference kind.
        const label = r.referenceKind
            ? r.referenceKind.replace(/_/g, " ")
            : categoryLabel(r.category);
        appendRefsTop(a, null, label, r.lineNumber, true);
    }

    if (r.snippet) {
        const snip = document.createElement("code");
        snip.className = "source-viewer__refs-snippet";
        snip.textContent = r.snippet;
        a.appendChild(snip);
    }
    attachRefsTooltip(a, r);
    li.appendChild(a);
    return li;
}

function appendRefsTop(anchor, kind, name, line, muted) {
    const top = document.createElement("div");
    top.className = "source-viewer__refs-row-top";
    if (kind) {
        top.appendChild(buildKindBadge(kind));
    }
    const nameSpan = document.createElement("span");
    nameSpan.className = muted
        ? "source-viewer__refs-name muted"
        : "source-viewer__refs-name";
    nameSpan.textContent = name;
    top.appendChild(nameSpan);
    if (line != null) {
        const lineSpan = document.createElement("span");
        lineSpan.className = "source-viewer__refs-line muted";
        lineSpan.textContent = `L${line}`;
        top.appendChild(lineSpan);
    }
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

// ── Refs-row hover tooltip ───────────────────────────────────────
//
// One reusable popover element rendered against the page so the
// rows can stay visually clean. Hovering a refs row shows the
// extension (module name), source file path, line number, and the
// full reference kind / category. Positioned to the left of the
// row so it never collides with the row contents; flips above the
// row when it would otherwise spill off the bottom of the panel.

let refsTooltipEl = null;
let refsTooltipHideTimer = 0;

function ensureRefsTooltip() {
    if (refsTooltipEl) return refsTooltipEl;
    const el = document.createElement("div");
    el.className = "source-viewer__refs-tooltip";
    el.setAttribute("role", "tooltip");
    el.hidden = true;
    document.body.appendChild(el);
    refsTooltipEl = el;
    return el;
}

function attachRefsTooltip(anchor, r) {
    anchor.addEventListener("mouseenter", () => showRefsTooltip(anchor, r));
    anchor.addEventListener("mouseleave", hideRefsTooltip);
    anchor.addEventListener("focusin", () => showRefsTooltip(anchor, r));
    anchor.addEventListener("focusout", hideRefsTooltip);
}

function showRefsTooltip(anchor, r) {
    clearTimeout(refsTooltipHideTimer);
    const el = ensureRefsTooltip();
    el.innerHTML = "";

    // Module · Object — the "where" line.
    const where = document.createElement("div");
    where.className = "source-viewer__refs-tooltip-where";
    where.textContent = `${r.sourceModuleName} › ${r.sourceObjectName}`;
    el.appendChild(where);

    // File path · line — the "what file" line, or "no source" when the
    // module didn't ship sources we could ingest.
    const loc = document.createElement("div");
    loc.className = "source-viewer__refs-tooltip-loc";
    if (r.sourceFileId != null && r.lineNumber != null) {
        const path = r.sourceFilePath ?? "(source file)";
        loc.textContent = `${path} · L${r.lineNumber}`;
    } else {
        loc.textContent = "no source available";
    }
    el.appendChild(loc);

    // Enclosing procedure / trigger the reference sits inside, when known.
    if (r.sourceMemberName) {
        const inMember = document.createElement("div");
        inMember.className = "source-viewer__refs-tooltip-member";
        const enclosingKind = expandKindLabel(r.sourceMemberKind);
        inMember.textContent = enclosingKind
            ? `in ${enclosingKind} ${r.sourceMemberName}`
            : `in ${r.sourceMemberName}`;
        el.appendChild(inMember);
    }

    // Member kind+name when present (the reference is to a specific
    // procedure / field, not just the owner object).
    if (r.memberName) {
        const member = document.createElement("div");
        member.className = "source-viewer__refs-tooltip-member";
        const memberKind = expandKindLabel(r.memberKind);
        const memberSig = r.memberSignature ?? "";
        member.textContent = memberKind
            ? `${memberKind} ${r.memberName}${memberSig}`
            : `${r.memberName}${memberSig}`;
        el.appendChild(member);
    }

    // Reference kind chip + category description.
    const kindRow = document.createElement("div");
    kindRow.className = "source-viewer__refs-tooltip-kind muted";
    kindRow.textContent = describeReferenceCategory(r.category, r.referenceKind);
    el.appendChild(kindRow);

    positionRefsTooltip(el, anchor);
    el.hidden = false;
}

function hideRefsTooltip() {
    // Tiny delay lets the cursor cross the small gap between the row and
    // a neighbouring row without the tooltip flickering off.
    clearTimeout(refsTooltipHideTimer);
    refsTooltipHideTimer = setTimeout(() => {
        if (refsTooltipEl) refsTooltipEl.hidden = true;
    }, 60);
}

function positionRefsTooltip(el, anchor) {
    el.style.left = "0";
    el.style.top = "0";
    el.style.maxWidth = "420px";
    // Width must be measured pre-position so the flip math works.
    el.hidden = false;
    const rowRect = anchor.getBoundingClientRect();
    const tipRect = el.getBoundingClientRect();
    const margin = 8;
    // Preferred: place to the LEFT of the row, vertically centred. The
    // refs panel lives on the right edge of the screen, so this keeps
    // the tooltip inside the viewport on the common layout.
    let x = rowRect.left - tipRect.width - margin;
    let y = rowRect.top + (rowRect.height - tipRect.height) / 2;
    // Off-screen left → flip to the right of the row instead.
    if (x < margin) {
        x = rowRect.right + margin;
    }
    // Clamp vertical so the tooltip stays inside the viewport.
    const maxY = window.innerHeight - tipRect.height - margin;
    if (y < margin) y = margin;
    if (y > maxY) y = maxY;
    el.style.left = `${Math.round(x + window.scrollX)}px`;
    el.style.top  = `${Math.round(y + window.scrollY)}px`;
}

function expandKindLabel(kind) {
    switch ((kind ?? "").toLowerCase()) {
        case "procedure":              return "procedure";
        case "internal_procedure":     return "internal procedure";
        case "protected_procedure":    return "protected procedure";
        case "local_procedure":        return "local procedure";
        case "event_publisher":        return "event publisher";
        case "event_subscriber":       return "event subscriber";
        case "field":                  return "field";
        case "trigger":                return "trigger";
        default:                       return kind ?? "";
    }
}

function describeReferenceCategory(category, referenceKind) {
    const cat = category ?? "object";
    const kind = referenceKind ?? "";
    switch (cat) {
        case "declaration": return "Declaration of the same symbol elsewhere";
        case "call":        return kind ? `Call site (${kind})` : "Call site";
        case "owner_type":  return "Indirect — referenced via the owning type";
        case "object":      return kind ? `Reference (${kind})` : "Reference";
        default:            return kind || cat;
    }
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
        case "action":                 return "action";
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
        case "action":               return "source-viewer__outline-badge--action";
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

/// Lazy-loads the outline's "Using" and "Used by" sections via one fetch
/// to /api/object-explorer/files/{id}/dependencies. Empty sections
/// collapse and show "(none)". Targets without ingested source render with
/// the kind badge but no clickable link; the tooltip explains why.
function wireFileDependencies(root, fileId) {
    const usingList = root.querySelector('[data-deps-list="using"]');
    const usedByList = root.querySelector('[data-deps-list="used-by"]');
    if (!usingList && !usedByList) return;
    fetch(`/api/object-explorer/files/${fileId}/dependencies`, {
        credentials: "same-origin",
    }).then(r => r.ok ? r.json() : Promise.reject(r.statusText))
      .then(data => {
          renderDepsSection(root, "using", usingList, data.using ?? []);
          renderDepsSection(root, "used-by", usedByList, data.usedBy ?? []);
      })
      .catch(() => {
          renderDepsSection(root, "using", usingList, []);
          renderDepsSection(root, "used-by", usedByList, []);
      });
}

function renderDepsSection(root, key, list, rows) {
    if (!list) return;
    const section = root.querySelector(`[data-deps-section="${key}"]`);
    const countChip = section?.querySelector("[data-deps-count]");
    list.innerHTML = "";
    if (rows.length === 0) {
        const li = document.createElement("li");
        li.className = "muted";
        li.textContent = "(none)";
        list.appendChild(li);
        if (countChip) countChip.textContent = "(0)";
        // Collapse the empty section so it doesn't take vertical space.
        if (section) {
            section.classList.remove("is-open");
            const toggle = section.querySelector(".source-viewer__outline-section-toggle");
            toggle?.setAttribute("aria-expanded", "false");
            const chevron = section.querySelector(".source-viewer__outline-section-chevron");
            chevron?.classList.remove("is-open");
            list.hidden = true;
        }
        return;
    }
    if (countChip) countChip.textContent = `(${rows.length})`;
    for (const row of rows) {
        const li = document.createElement("li");
        li.className = "source-viewer__outline-item";
        const inner = row.targetFileId
            ? buildDepsLink(row)
            : buildDepsPlaceholder(row);
        li.appendChild(inner);
        list.appendChild(li);
    }
}

function buildDepsLink(row) {
    const a = document.createElement("a");
    const line = row.targetLineNumber ?? 1;
    a.href = `/object-explorer/file/${row.targetFileId}?line=${line}`;
    a.title = `${row.targetModuleName || ""} · ${row.referenceKind || ""}`.trim();
    a.appendChild(buildKindBadge(row.targetObjectKind));
    const name = document.createElement("span");
    name.className = "source-viewer__outline-name";
    name.textContent = row.targetObjectName ?? "";
    a.appendChild(name);
    if (row.targetModuleName) {
        const mod = document.createElement("span");
        mod.className = "source-viewer__outline-sig muted";
        mod.textContent = row.targetModuleName;
        a.appendChild(mod);
    }
    return a;
}

function buildDepsPlaceholder(row) {
    const span = document.createElement("span");
    span.title = "no source available";
    span.appendChild(buildKindBadge(row.targetObjectKind));
    const name = document.createElement("span");
    name.className = "source-viewer__outline-name muted";
    name.textContent = row.targetObjectName ?? "";
    span.appendChild(name);
    if (row.targetModuleName) {
        const mod = document.createElement("span");
        mod.className = "source-viewer__outline-sig muted";
        mod.textContent = row.targetModuleName;
        span.appendChild(mod);
    }
    return span;
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

// ── Ctrl/Cmd-A intercept ─────────────────────────────────────────
//
// The read-only mount keeps EditorView.editable.of(false) on the
// editor's contentDOM (contenteditable="false"). That means
// defaultKeymap's Mod-a binding never sees the keystroke — the
// browser's native "select everything on the page" wins, which
// selects the outline, the breadcrumb, and the rest of the surface
// alongside the code. Intercept at the window level only when focus
// (or the click target) lives inside the editor surface, and route to
// CodeMirror's selectAll so users get the IDE-style "select all in
// the code" they expect.
function wireSelectAllShortcut(root, editorId, codeHost) {
    window.addEventListener("keydown", e => {
        const isA = e.key === "a" || e.key === "A";
        if (!isA) return;
        if (!(e.ctrlKey || e.metaKey)) return;
        if (e.shiftKey || e.altKey) return;
        if (!document.contains(root)) return;
        const active = document.activeElement;
        const inEditor = (active && codeHost.contains(active))
            || (active && containsNode(editorId, active));
        if (!inEditor) return;
        e.preventDefault();
        selectAll(editorId);
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
        root.style.setProperty("--source-viewer-outline-width", stored + "px");
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
        root.style.setProperty("--source-viewer-outline-width", next + "px");
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
        root.style.setProperty("--source-viewer-outline-width", next + "px");
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
        // Any unmounted .source-viewer on the page triggers init.
        const hasUnmounted = Array.from(document.querySelectorAll(".source-viewer"))
            .some(r => !r.querySelector(".cm-editor"));
        if (hasUnmounted) init();
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
