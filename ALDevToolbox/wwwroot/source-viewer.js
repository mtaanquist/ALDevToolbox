// Object Explorer source-file viewer — client glue.
//
// The page is server-rendered HTML (no Blazor circuit). This module mounts
// the CodeMirror viewer against the rendered DOM and wires every
// in-document interaction directly to JS handlers, with a JSON-endpoint
// roundtrip for the two gestures that need server data (Go to definition,
// Find in this file). The shape mirrors code-editor.js's expected
// dotNetRef interface so we can reuse mountReadOnly verbatim.
//
// See .design/source-viewer-redesign.md for the motivation.

import { mountReadOnly, scrollToLine } from "/code-editor.js";

const FILE_URL_PREFIX = "/object-explorer/file/";

function init() {
    const root = document.querySelector(".source-viewer");
    if (!root) return;

    const codeHost = root.querySelector(".source-viewer__code");
    if (!codeHost) return;

    // Guard against double-mount when the page is reached via Blazor's
    // enhanced navigation (the module URL is cached, but DOMContentLoaded
    // also re-fires on the first visit). Once mounted, the CodeMirror
    // DOM has rendered into codeHost and `.cm-editor` lives inside.
    if (codeHost.querySelector(".cm-editor")) return;

    const fileId = Number(root.dataset.fileId);
    if (!Number.isFinite(fileId)) return;

    const initialLineAttr = root.dataset.initialLine;
    const initialLine = initialLineAttr ? Number(initialLineAttr) : Number(new URLSearchParams(location.search).get("line"));

    const declarations = parseJsonAttr(codeHost.dataset.declarations) ?? [];
    const resolvables = parseJsonAttr(codeHost.dataset.resolvables) ?? [];
    const content = codeHost.dataset.content ?? "";
    const language = codeHost.dataset.language ?? "al";

    // Clear the data-content payload from the DOM as soon as we've captured
    // it. Otherwise the (potentially multi-MB) text stays attached to the
    // div, doubling the page's memory footprint.
    codeHost.removeAttribute("data-content");
    codeHost.removeAttribute("data-declarations");
    codeHost.removeAttribute("data-resolvables");

    const notice = root.querySelector(".source-viewer__notice");
    const findHost = root.querySelector(".source-viewer__find-host");

    // Duck-type the Blazor DotNetObjectReference the read-only mount
    // expects. code-editor.js calls dotNetRef.invokeMethodAsync(name, args)
    // for the three context-menu items; we route each to a JS handler.
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
    });

    if (Number.isFinite(initialLine) && initialLine >= 1) {
        // mountReadOnly does its own deferred scroll only when passed
        // scrollToLine in options; we want the same behaviour but with
        // an explicit call so the same path is used for in-page jumps.
        requestAnimationFrame(() => scrollToLine(editorId, initialLine, true));
    }

    wireOutlineFilter(root);
    wireSectionToggles(root);
    wireSameFileLinks(root, editorId, fileId);

    async function onFindReferencesAt(line, column) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/references/sessions/at-position?fileId=${fileId}&line=${line}&column=${column}`,
                { credentials: "same-origin" });
            if (res.status === 204 || res.status === 404) {
                showNotice("No references found for that token.");
                return;
            }
            if (!res.ok) {
                showNotice("Couldn't search references (server error).");
                return;
            }
            const session = await res.json();
            if (!session.results || session.results.length === 0) {
                showNotice("No references found for that token.");
                return;
            }
            const first = session.results[0];
            const targetFile = first.sourceFileId;
            const targetLine = first.lineNumber ?? 1;
            location.assign(
                `${FILE_URL_PREFIX}${targetFile}?line=${targetLine}&refSet=${encodeURIComponent(session.token)}`);
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
                // Fall back to the object detail page so the user still
                // gets a useful destination when the cache mint fails.
                location.assign(`/object-explorer/object/${symbolId}#find-references`);
                return;
            }
            const session = await res.json();
            // Empty result set — go to the object page to show the
            // "no references found" message rather than landing on the
            // current file with an empty panel.
            if (!session.results || session.results.length === 0) {
                location.assign(`/object-explorer/object/${symbolId}#find-references`);
                return;
            }
            const first = session.results[0];
            const targetFile = first.sourceFileId;
            const targetLine = first.lineNumber ?? 1;
            location.assign(
                `${FILE_URL_PREFIX}${targetFile}?line=${targetLine}&refSet=${encodeURIComponent(session.token)}`);
        } catch (err) {
            console.warn("Find references failed:", err);
            location.assign(`/object-explorer/object/${symbolId}#find-references`);
        }
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
                scrollToLine(editorId, target.lineNumber, true);
                history.replaceState(null, "", `${FILE_URL_PREFIX}${fileId}?line=${target.lineNumber}${preservedQueryTail()}`);
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
        if (!findHost) return;
        findHost.innerHTML = "";
        if (!data) {
            findHost.hidden = true;
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

        const clear = document.createElement("button");
        clear.className = "btn btn--sm";
        clear.type = "button";
        clear.textContent = "Clear";
        clear.addEventListener("click", () => renderFindResults(null));
        findHost.appendChild(clear);

        findHost.hidden = false;
    }

    function jumpToLineInThisFile(line) {
        scrollToLine(editorId, line, true);
        history.replaceState(null, "", `${FILE_URL_PREFIX}${fileId}?line=${line}${preservedQueryTail()}`);
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
            // Let modifier-click / middle-click open in a new tab.
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            const line = Number(a.dataset.line);
            if (!Number.isFinite(line) || line < 1) return;
            e.preventDefault();
            scrollToLine(editorId, line, true);
            history.replaceState(null, "", `${FILE_URL_PREFIX}${fileId}?line=${line}${preservedQueryTail()}`);
        });
    });
}

// Preserves any non-`line` query parameters on URL replacement — keeps
// future refSet tokens (Find-references session) intact across in-page
// jumps. Returns either "" or "&key=val[&…]".
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

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
} else {
    init();
}

// When the user arrives via Blazor's enhanced navigation from another
// page (any InteractiveServer page in the app), the module URL is
// cached and the <script> tag re-executes against the new DOM. The
// init() guard above prevents double-mounting; this listener catches
// any case where the script is parsed before the new DOM is in place.
if (typeof globalThis.Blazor !== "undefined" && globalThis.Blazor.addEventListener) {
    globalThis.Blazor.addEventListener("enhancedload", init);
}
