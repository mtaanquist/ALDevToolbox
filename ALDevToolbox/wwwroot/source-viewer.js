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
const { mountReadOnly, mountCompareEditor, setDiff, getValue, setValue, scrollToLine, scrollComparePanes, openSearch, selectAll, containsNode, syncComparePanes, topLine, cursorPosition } = await import(codeEditorUrl);

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
    if (editorsByPane.length === 2
        && editorsByPane[0].root.classList.contains("source-viewer--compare")
        && editorsByPane[1].root.classList.contains("source-viewer--compare")) {
        const [left, right] = editorsByPane;
        wireCompareScrollSync(left, right);
        wireCompareChangeNav(left, right);
        // Editable Compare tool: both panes editable → wire the live re-diff,
        // Swap/Clear, and the summary read-out.
        if (left.root.__editableCompare && right.root.__editableCompare) {
            wireEditableCompare(left, right);
        }
    }
}

// Vertical sync is line-anchored (syncComparePanes maps the source's top line to
// its counterpart and scrolls there via CodeMirror's measured geometry) so the
// panes don't drift the way raw scrollTop mirroring did once filler block
// widgets enter CodeMirror's height estimation. Horizontal sync stays a plain
// mirror — columns aren't affected by fillers.
//
// Only the pane the pointer is over is allowed to DRIVE the sync. The old
// two-way binding tried to recognise its own programmatic echoes by target
// scrollTop, but CodeMirror moves the destination twice per sync (an
// intermediate scrollIntoView hop, then the measured correction), so one of
// the two events always leaked through as a "user" scroll and synced the
// source pane straight back — which read as jumping mid-scroll and, after an
// overview-ruler jump, as a scroll position you couldn't get back above.
// Gating on the hovered pane makes every echo on the other pane inert, no
// echo bookkeeping needed. (Wheel and scrollbar gestures both require the
// pointer over the pane they scroll, so the gate matches how the panes are
// actually driven.)
function wireCompareScrollSync(left, right) {
    const leftScroller = left.root.querySelector(".cm-scroller");
    const rightScroller = right.root.querySelector(".cm-scroller");
    if (!leftScroller || !rightScroller) return;

    // Which pane the user is interacting with. Tracked on the pane ROOT (not
    // the scroller) so clicks on the overview ruler count as driving that
    // pane. pointerenter alone misses the "page loaded with the cursor
    // already inside a pane" case (no entry event fires until the pointer
    // moves across a boundary), so wheel and pointerdown claim the pane too.
    let active = null;
    for (const [pane, name] of [[left, "left"], [right, "right"]]) {
        const claim = () => { active = name; };
        pane.root.addEventListener("pointerenter", claim);
        pane.root.addEventListener("pointerdown", claim);
        pane.root.addEventListener("wheel", claim, { passive: true });
    }

    leftScroller.addEventListener("scroll", () => {
        if (active !== "left") return;
        syncComparePanes(left.editorId, right.editorId);
        if (rightScroller.scrollLeft !== leftScroller.scrollLeft) {
            rightScroller.scrollLeft = leftScroller.scrollLeft;
        }
    });
    rightScroller.addEventListener("scroll", () => {
        if (active !== "right") return;
        syncComparePanes(right.editorId, left.editorId);
        if (leftScroller.scrollLeft !== rightScroller.scrollLeft) {
            leftScroller.scrollLeft = rightScroller.scrollLeft;
        }
    });

    // ── Reading-position deep link ───────────────────────────────
    //
    // ?line=N is the RIGHT (newer) pane's top line. On load, jump both
    // panes there; while scrolling, mirror the current top line back into
    // the URL (debounced replaceState) so a refresh keeps your place and
    // the address bar is always a shareable link to the spot you're
    // looking at.
    const initialLine = Number(new URLSearchParams(location.search).get("line"));
    if (Number.isFinite(initialLine) && initialLine >= 1) {
        requestAnimationFrame(() => {
            scrollToLine(right.editorId, initialLine, false, "top");
            // scrollToLine settles over two animation frames; bring the left
            // pane along once it has.
            setTimeout(() => syncComparePanes(right.editorId, left.editorId), 80);
        });
    }

    let urlTimer = 0;
    const scheduleUrlSync = () => {
        clearTimeout(urlTimer);
        urlTimer = setTimeout(() => {
            const ln = topLine(right.editorId);
            if (!ln) return;
            const url = new URL(location.href);
            if (ln > 1) url.searchParams.set("line", String(ln));
            else url.searchParams.delete("line");
            history.replaceState(null, "", url.pathname + url.search);
        }, 300);
    };
    leftScroller.addEventListener("scroll", scheduleUrlSync);
    rightScroller.addEventListener("scroll", scheduleUrlSync);
}

// ── Next/previous change navigation ──────────────────────────────
//
// Steps both compare panes through the diff's change blocks. Blocks are
// computed in the same *visual* space as the overview ruler (source line
// plus the filler gaps above it) so a deletion that only exists in the
// left pane and an insertion that only exists in the right pane still
// order correctly against each other; runs from the two panes whose
// visual ranges touch (a Modified block appears in both) collapse into
// one stop.
//
// Wired to optional [data-diff-nav="next"/"prev"] buttons anywhere on the
// page and to Ctrl/Cmd+ArrowDown / ArrowUp. The buttons sit OUTSIDE the
// panes, so the hovered-pane scroll-sync gate never fires for these
// programmatic jumps — each jump scrolls its anchor pane and then
// explicitly syncs the other one, mirroring the ?line= deep-link path.
function wireCompareChangeNav(left, right) {
    const go = (delta) => {
        // Blocks are recomputed on each jump, not captured once: the editable
        // Compare tool re-diffs live, so __compareDiffRows/__compareFillers
        // change under us. (For the read-only OE page they're stable, so this
        // is just a cheap recompute.)
        const blocks = computeChangeBlocks(left, right);
        if (blocks.length === 0) return;
        const rightGaps = (right.root.__compareFillers ?? [])
            .filter(f => f && Number.isFinite(f.before) && Number.isFinite(f.size) && f.size > 0);
        const rightVisualOf = (line) =>
            (line - 1) + rightGaps.reduce((sum, f) => sum + (f.before <= line ? f.size : 0), 0);
        // Where the user currently is, in visual rows. The right pane is
        // the reference (same choice the URL deep-link makes).
        const ln = topLine(right.editorId);
        const current = ln ? rightVisualOf(ln) : 0;
        // A "top"-aligned jump typically leaves the previous line still
        // peeking at the viewport top, so topLine reads one below the block
        // we just landed on. Tolerate a full line either way or "next"
        // would keep re-selecting the current block.
        let target = null;
        if (delta > 0) {
            target = blocks.find(b => b.visual > current + 1.5) ?? null;
        } else {
            for (const b of blocks) {
                if (b.visual < current - 1.5) target = b;
                else break;
            }
        }
        if (!target) return;
        const pane = target.pane === "left" ? left : right;
        const other = pane === left ? right : left;
        // Move both panes together in the same frames — no visible one-then-
        // the-other step (see scrollComparePanes).
        scrollComparePanes(pane.editorId, other.editorId, target.line, true);
    };

    document.querySelectorAll("[data-diff-nav]").forEach(btn => {
        if (btn.__compareNavBound) return;
        btn.__compareNavBound = true;
        btn.addEventListener("click", () => go(btn.dataset.diffNav === "prev" ? -1 : 1));
    });

    window.addEventListener("keydown", e => {
        if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
        if (!(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey) return;
        // Stale listener from a previous mount (the Compare tool remounts
        // fresh panes on every run) — panes gone, do nothing.
        if (!document.contains(left.root) || !document.contains(right.root)) return;
        const active = document.activeElement;
        if (active instanceof HTMLElement
            && (active.tagName === "INPUT" || active.tagName === "TEXTAREA" || active.isContentEditable)) {
            return;
        }
        e.preventDefault();
        go(e.key === "ArrowDown" ? 1 : -1);
    });
}

/// Coalesces each pane's changed lines into blocks, positions them in the
/// shared visual space, and merges blocks from opposite panes whose visual
/// ranges overlap or touch (preferring the right pane's anchor — it's the
/// pane the scroll-sync and URL treat as primary). Returns
/// [{pane: "left"|"right", line, visual}] sorted by visual position.
function computeChangeBlocks(left, right) {
    const paneBlocks = (pane, name) => {
        const rows = (pane.root.__compareDiffRows ?? [])
            .filter(r => r && Number.isFinite(r.line))
            .sort((a, b) => a.line - b.line);
        const gaps = (pane.root.__compareFillers ?? [])
            .filter(f => f && Number.isFinite(f.before) && Number.isFinite(f.size) && f.size > 0);
        const visualOf = (line) =>
            (line - 1) + gaps.reduce((sum, f) => sum + (f.before <= line ? f.size : 0), 0);
        const blocks = [];
        for (const r of rows) {
            const last = blocks[blocks.length - 1];
            if (last && r.line === last.endLine + 1) {
                last.endLine = r.line;
                last.visualEnd = visualOf(r.line);
            } else {
                blocks.push({
                    pane: name,
                    line: r.line,
                    endLine: r.line,
                    visual: visualOf(r.line),
                    visualEnd: visualOf(r.line),
                });
            }
        }
        return blocks;
    };

    const all = [...paneBlocks(left, "left"), ...paneBlocks(right, "right")]
        .sort((a, b) => a.visual - b.visual);
    const merged = [];
    for (const b of all) {
        const last = merged[merged.length - 1];
        if (last && b.visual <= last.visualEnd + 1) {
            last.visualEnd = Math.max(last.visualEnd, b.visualEnd);
            if (last.pane === "left" && b.pane === "right") {
                last.pane = "right";
                last.line = b.line;
                last.visual = Math.min(last.visual, b.visual);
            }
        } else {
            merged.push({ ...b });
        }
    }
    return merged;
}

// ── Editable Compare tool: live re-diff across two editable panes ──
//
// Both panes are editable CodeMirror editors (mountCompareEditor). On any
// edit we debounce, POST both texts to /api/compare/diff, and swap the diff
// decorations into both panes via setDiff — no remount, so typed text and
// undo history survive. The server reuses DiffPlex + SideBySideDiffSerializer
// (the same output the read-only OE compare page consumes), so the two
// surfaces stay visually identical. The page shell (summary read-out, Swap /
// Clear buttons, Prev/Next-change nav) is plain SSR markup wired here — no
// Blazor circuit, matching the source-viewer redesign.
function wireEditableCompare(left, right) {
    const summaryEl = document.querySelector("[data-compare-summary]");
    const swapBtn = document.querySelector("[data-compare-swap]");
    const clearBtn = document.querySelector("[data-compare-clear]");
    const navBtns = Array.from(document.querySelectorAll("[data-diff-nav]"));

    const HINT = "Paste or type into either side to see what changed.";
    const setSummary = (text, isError) => {
        if (!summaryEl) return;
        summaryEl.textContent = text;
        summaryEl.classList.toggle("compare-page__summary--error", !!isError);
    };
    const setNavEnabled = (enabled) => {
        for (const b of navBtns) b.disabled = !enabled;
    };
    // Swap / Clear only do something once there's text in a pane; disable them
    // on an empty page so nothing looks actionable before the user starts.
    const refreshActionButtons = () => {
        const hasText = getValue(left.editorId) !== "" || getValue(right.editorId) !== "";
        if (swapBtn) swapBtn.disabled = !hasText;
        if (clearBtn) clearBtn.disabled = !hasText;
    };

    const applyPane = (pane, side) => {
        const rows = Array.isArray(side.diff) ? side.diff : [];
        const fillers = Array.isArray(side.fillers) ? side.fillers : [];
        const wordDiff = Array.isArray(side.wordDiff) ? side.wordDiff : [];
        setDiff(pane.editorId, { lineDecorations: diffRowsToDecorations(rows), fillers, wordDiff });
        pane.root.__compareDiffRows = rows;
        pane.root.__compareFillers = fillers;
        const totalLines = Math.max(1, getValue(pane.editorId).split("\n").length);
        buildDiffOverview(pane.root, pane.editorId, rows, totalLines, fillers);
    };

    const clearDiff = () => {
        for (const pane of [left, right]) {
            setDiff(pane.editorId, { lineDecorations: {}, fillers: [], wordDiff: [] });
            pane.root.__compareDiffRows = [];
            pane.root.__compareFillers = [];
            buildDiffOverview(pane.root, pane.editorId, [], 1, []);
        }
    };

    // Guards against an out-of-order response: a slow diff for an old keystroke
    // must not overwrite a newer one.
    let seq = 0;
    const recompute = async () => {
        refreshActionButtons();
        const leftText = getValue(left.editorId);
        const rightText = getValue(right.editorId);
        if (leftText === "" && rightText === "") {
            clearDiff();
            setSummary(HINT, false);
            setNavEnabled(false);
            return;
        }
        const mine = ++seq;
        let data;
        try {
            const res = await fetch("/api/compare/diff", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ left: leftText, right: rightText }),
            });
            data = await res.json();
        } catch {
            setSummary("Could not reach the server to compare. Try again.", true);
            return;
        }
        if (mine !== seq) return; // superseded by a newer edit
        if (data && data.error) {
            setSummary(data.error, true);
            return;
        }
        applyPane(left, data.left ?? {});
        applyPane(right, data.right ?? {});
        const s = data.summary ?? {};
        if (s.identical) {
            setSummary("The two texts are identical.", false);
            setNavEnabled(false);
        } else {
            const total = (s.added ?? 0) + (s.removed ?? 0) + (s.modified ?? 0);
            setSummary(
                `${total} change${total === 1 ? "" : "s"} - ${s.added ?? 0} added, ${s.removed ?? 0} removed, ${s.modified ?? 0} modified`,
                false);
            setNavEnabled(total > 0);
        }
    };

    let timer = 0;
    const schedule = () => {
        clearTimeout(timer);
        timer = setTimeout(recompute, 300);
    };
    left.root.__onCompareEdit = schedule;
    right.root.__onCompareEdit = schedule;

    if (swapBtn) {
        swapBtn.addEventListener("click", () => {
            const l = getValue(left.editorId);
            const r = getValue(right.editorId);
            setValue(left.editorId, r);
            setValue(right.editorId, l);
            recompute();
        });
    }
    if (clearBtn) {
        clearBtn.addEventListener("click", () => {
            setValue(left.editorId, "");
            setValue(right.editorId, "");
            clearDiff();
            setSummary(HINT, false);
            setNavEnabled(false);
            refreshActionButtons();
        });
    }

    setNavEnabled(false);
    recompute();
}

// [{line, kind}] diff rows → the {lineNumber: cssClass} map setDiff/mountReadOnly
// consume. Same shape the read-only path builds inline in initOne.
function diffRowsToDecorations(rows) {
    const map = {};
    if (Array.isArray(rows)) {
        for (const row of rows) {
            if (!row || !Number.isFinite(row.line)) continue;
            map[row.line] = `cm-diff-${row.kind}`;
        }
    }
    return map;
}

/// Builds (or rebuilds) the compare-pane overview ruler. Coalesces consecutive
/// same-kind changed lines into runs (so a block of edits reads as one bar,
/// like KDiff3), positions each run proportionally over the full file height,
/// and wires a click to jump to its first line. Any existing ruler on the pane
/// is removed first so the editable Compare tool can re-run this on every live
/// diff update.
///
/// Positions are computed in *visual* space, not source-line space: the
/// alignment fillers (`[{before, size}]`) add blank rows that push real lines
/// down, so a mark's fraction is `visualRow / (totalLines + totalFiller)`. Both
/// panes share the same visual height, so a change at the same aligned row
/// reads at the same height on both ruler strips.
function buildDiffOverview(paneRoot, edId, rows, totalLines, fillers) {
    // Drop a previous ruler (live re-diff) before rebuilding.
    paneRoot.querySelector(".oe-diff-overview")?.remove();
    if (!Array.isArray(rows) || rows.length === 0 || !(totalLines > 0)) return;
    const sorted = rows
        .filter(r => r && Number.isFinite(r.line) && r.kind)
        .sort((a, b) => a.line - b.line);
    if (sorted.length === 0) return;

    const runs = [];
    for (const r of sorted) {
        const last = runs[runs.length - 1];
        if (last && last.kind === r.kind && r.line === last.end + 1) {
            last.end = r.line;
        } else {
            runs.push({ start: r.line, end: r.line, kind: r.kind });
        }
    }

    // Filler-aware geometry. `offsetBefore(L)` is the blank space rendered
    // above source line L (gaps anchored before any line ≤ L); the total
    // visual height adds every filler, including a trailing one past EOF.
    const gaps = (Array.isArray(fillers) ? fillers : [])
        .filter(f => f && Number.isFinite(f.before) && Number.isFinite(f.size) && f.size > 0);
    const totalFiller = gaps.reduce((sum, f) => sum + f.size, 0);
    const totalVisual = totalLines + totalFiller;
    const offsetBefore = (line) =>
        gaps.reduce((sum, f) => sum + (f.before <= line ? f.size : 0), 0);

    const overview = document.createElement("div");
    overview.className = "oe-diff-overview";
    overview.title = "Changes overview — click a mark to jump";
    for (const run of runs) {
        const mark = document.createElement("button");
        mark.type = "button";
        mark.className = `oe-diff-overview__mark oe-diff-overview__mark--${run.kind}`;
        // Visual top of the run's first line, and a height that also absorbs
        // any fillers sitting between start and end (interior gaps can occur
        // when the opposite side inserts mid-run).
        const top = (run.start - 1) + offsetBefore(run.start);
        const height = (run.end - run.start + 1)
            + (offsetBefore(run.end) - offsetBefore(run.start));
        mark.style.top = (top / totalVisual) * 100 + "%";
        mark.style.height = `max(3px, ${(height / totalVisual) * 100}%)`;
        const span = run.end > run.start ? `lines ${run.start}–${run.end}` : `line ${run.start}`;
        mark.title = `${run.kind} · ${span}`;
        mark.setAttribute("aria-label", `Jump to ${run.kind} change at ${span}`);
        mark.addEventListener("click", () => scrollToLine(edId, run.start, true));
        overview.appendChild(mark);
    }
    paneRoot.appendChild(overview);
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
    // Release the user is viewing from (?from=). When set, find-references
    // session mints seed here so a base object opened from a customer Release
    // surfaces the customer's own code. Empty for the normal same-Release case.
    const viewRelease = codeHost.dataset.viewRelease || "";

    // Clear the data-content payload from the DOM — the editor owns it now.
    codeHost.removeAttribute("data-content");
    codeHost.removeAttribute("data-declarations");
    codeHost.removeAttribute("data-resolvables");
    codeHost.removeAttribute("data-procedures");
    codeHost.removeAttribute("data-view-release");

    // Appends &from=/?from= to a session-mint URL so the seed Release rides
    // along. No-op when the viewer isn't tagged with a view Release.
    const withFrom = (url) =>
        viewRelease ? url + (url.includes("?") ? "&" : "?") + "from=" + viewRelease : url;

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
                case "OnFindSystemReferences":
                    return mintObjectSystemSession(args[0]);
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

    // Alignment fillers (compare page only). data-fillers carries
    // `[{before, size}, …]` — blank gaps of `size` line-heights anchored
    // before source line `before`, so the two panes stay aligned KDiff3-style.
    const fillerData = parseJsonAttr(codeHost.dataset.fillers);
    codeHost.removeAttribute("data-fillers");

    // Intra-line changed-word ranges (compare page only) — the stronger tint
    // inside modified lines. `[{line, from, to}]`, 1-based, `to` exclusive.
    const wordDiffData = parseJsonAttr(codeHost.dataset.wordDiff);
    codeHost.removeAttribute("data-word-diff");

    // Editable compare pane (the standalone Compare tool). The pane IS the
    // input: mount an editable editor with dynamic diff decorations and let
    // init() wire the live re-diff across the two panes. The diff itself is
    // recomputed server-side (POST /api/compare/diff) on a debounce; nothing
    // is baked in at mount, so we never remount and never lose typed text.
    if (isCompare && codeHost.dataset.editable === "true") {
        codeHost.removeAttribute("data-editable");
        const placeholderText = codeHost.dataset.placeholder ?? "";
        codeHost.removeAttribute("data-placeholder");
        const editorId = mountCompareEditor(codeHost, content, language, {
            lineDecorations,
            fillers: Array.isArray(fillerData) ? fillerData : [],
            wordDiff: Array.isArray(wordDiffData) ? wordDiffData : [],
            placeholder: placeholderText,
            // Late-bound so a keystroke before init() finishes wiring the pair
            // is simply ignored (nothing to diff against yet).
            onDocChanged: () => root.__onCompareEdit?.(),
        });
        root.__compareDiffRows = Array.isArray(diffData) ? diffData : [];
        root.__compareFillers = Array.isArray(fillerData) ? fillerData : [];
        root.__compareEditorId = editorId;
        root.__editableCompare = true;
        return editorId;
    }

    const editorId = mountReadOnly(codeHost, content, language, {
        declarations,
        resolvables,
        lineDecorations,
        fillers: Array.isArray(fillerData) ? fillerData : [],
        wordDiff: Array.isArray(wordDiffData) ? wordDiffData : [],
        // Folding one compare pane would break the server-computed filler
        // alignment with the other, so the compare mounts opt out.
        folding: !isCompare,
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
        // KDiff3-style overview ruler: a full-height strip mapping every
        // changed line in the WHOLE file to a coloured mark, so the spread of
        // changes is visible without scrolling (the inline change-gutter only
        // covers on-screen rows). Click a mark to jump there.
        const totalLines = content ? content.split("\n").length : 1;
        buildDiffOverview(root, editorId, diffData, totalLines, fillerData);
        // Stash the parsed diff geometry on the pane root (the data-*
        // attributes were consumed above) so the cross-pane change
        // navigation in init() can compute jump targets without
        // re-serialising anything through the DOM.
        root.__compareDiffRows = Array.isArray(diffData) ? diffData : [];
        root.__compareFillers = Array.isArray(fillerData) ? fillerData : [];
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
    wireFindReferencesShortcut(root, editorId, onFindReferencesAt);
    wireSelectAllShortcut(root, editorId, codeHost);
    wirePopstate(root, editorId);
    wireOutlineResizer(root);
    wireSymbolCard(root, codeHost, editorId, fileId, {
        onFindReferences: mintMemberSession,
    });
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
                ? e.target.closest(".sv-row")
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
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/from-member-symbol/${symbolId}`),
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
        } finally {
            busyEnd();
        }
    }

    async function mintObjectSession(objectId) {
        clearNotice();
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/from-symbol/${objectId}`),
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
        } finally {
            busyEnd();
        }
    }

    async function mintObjectSystemSession(objectId) {
        clearNotice();
        busyStart("Searching system references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/system-references/sessions/from-object/${objectId}`),
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
        } finally {
            busyEnd();
        }
    }

    async function onFindReferencesAt(line, column) {
        clearNotice();
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/at-position?fileId=${fileId}&line=${line}&column=${column}`),
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
        } finally {
            busyEnd();
        }
    }

    async function onFindReferences(symbolId) {
        clearNotice();
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/from-symbol/${symbolId}`),
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
        } finally {
            busyEnd();
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
        const section = document.createElement("div");
        section.className = "pane__sec";
        const heading = document.createElement("div");
        heading.className = "pane__sec-h sv-sec-h";
        heading.append("Occurrences of ");
        heading.appendChild(sectionName(data.word));
        const countChip = document.createElement("span");
        countChip.className = "pane__count";
        heading.appendChild(countChip);
        section.appendChild(heading);
        findHost.appendChild(section);

        if (!data.occurrences || data.occurrences.length === 0) {
            countChip.textContent = "0";
            const p = document.createElement("p");
            p.className = "muted source-viewer__panel-empty";
            p.textContent = "No occurrences in this file.";
            section.appendChild(p);
        } else {
            countChip.textContent = data.occurrences.length.toLocaleString();
            const list = document.createElement("div");
            list.className = "refs";
            for (const occ of data.occurrences) {
                list.appendChild(buildHitRow({
                    line: occ.line,
                    text: occ.lineText,
                    match: data.word,
                    onActivate: () => jumpToLineInThisFile(occ.line),
                }));
            }
            section.appendChild(list);
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

// ── Busy indicator ───────────────────────────────────────────────
//
// Find-references on a heavily-used object can take a few seconds
// server-side; without feedback the right-click gesture looks dead
// and users click again. A small fixed pill with a spinner sits at
// the bottom-centre of the viewport (never under the pointer) while
// a session-mint request is in flight, and the page cursor flips to
// `progress`. Counted, so overlapping requests keep it up until the
// last one settles.

let busyEl = null;
let busyCount = 0;

function busyStart(text) {
    busyCount++;
    if (!busyEl) {
        busyEl = document.createElement("div");
        busyEl.className = "source-viewer__busy";
        busyEl.setAttribute("role", "status");
        busyEl.setAttribute("aria-live", "polite");
        const spinner = document.createElement("span");
        spinner.className = "source-viewer__busy-spinner";
        spinner.setAttribute("aria-hidden", "true");
        const label = document.createElement("span");
        label.className = "source-viewer__busy-text";
        busyEl.appendChild(spinner);
        busyEl.appendChild(label);
        document.body.appendChild(busyEl);
    }
    busyEl.querySelector(".source-viewer__busy-text").textContent = text;
    busyEl.hidden = false;
    document.documentElement.style.cursor = "progress";
}

function busyEnd() {
    busyCount = Math.max(0, busyCount - 1);
    if (busyCount === 0) {
        if (busyEl) busyEl.hidden = true;
        document.documentElement.style.cursor = "";
    }
}

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

// Inline copies of the three Lucide glyphs the client-side renderers need.
// The server-rendered markup reaches these through <Icon Name="..." />, which
// is what IconCatalog checks; the JS builders can't, so the paths live here
// instead. ObjectExplorerInspectorTests pins them against
// Resources/Icons/{chevron-right,x,search}.svg so the copies cannot drift.
const CARET_ICON_SVG =
    '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" ' +
    'stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18 6-6-6-6"/></svg>';
const CLOSE_ICON_SVG =
    '<svg class="btn__icon" viewBox="0 0 24 24" width="12" height="12" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M18 6 6 18"/><path d="m6 6 12 12"/></svg>';
const SEARCH_ICON_SVG =
    '<svg class="search__icon" viewBox="0 0 24 24" width="15" height="15" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="m21 21-4.34-4.34"/><circle cx="11" cy="11" r="8"/></svg>';

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
            // The shortcuts control is a toggle button, not a tab — it sits
            // outside the tablist, so it takes aria-pressed instead.
            const attr = tab.getAttribute("role") === "tab" ? "aria-selected" : "aria-pressed";
            tab.setAttribute(attr, match ? "true" : "false");
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

    const count = session.results?.length ?? 0;
    const groups = count > 0 ? groupByObject(session.results) : [];

    const tabBtn = root.querySelector('.source-viewer__tab[data-tab="references"]');
    const tabCountEl = tabBtn?.querySelector(".source-viewer__tab-count");
    if (tabBtn) tabBtn.hidden = false;
    if (tabCountEl) {
        tabCountEl.textContent = count.toLocaleString();
    } else if (tabBtn) {
        const c = document.createElement("span");
        c.className = "pill-tab__count source-viewer__tab-count";
        c.textContent = count.toLocaleString();
        tabBtn.appendChild(c);
    }

    const section = document.createElement("div");
    section.className = "pane__sec";
    panel.appendChild(section);

    const heading = document.createElement("div");
    heading.className = "pane__sec-h sv-sec-h";
    heading.append("References to ");
    heading.appendChild(sectionName(session.targetName || "this symbol"));
    const countChip = document.createElement("span");
    countChip.className = "pane__count";
    // Just the total. "in N objects" would be true but the group headers
    // below already say it, and the rail is too narrow to spend a line on.
    countChip.textContent = count.toLocaleString();
    countChip.title = groups.length > 0
        ? `${count.toLocaleString()} in ${groups.length.toLocaleString()} ${groups.length === 1 ? "object" : "objects"}`
        : "no references";
    heading.appendChild(countChip);
    const spacer = document.createElement("span");
    spacer.className = "pw__spacer";
    heading.appendChild(spacer);
    const close = document.createElement("button");
    close.type = "button";
    close.className = "btn btn--icon btn--sm btn--ghost source-viewer__refs-close";
    close.dataset.action = "close-refs";
    close.title = `Close: ${session.targetLabel ?? "these references"}`;
    close.setAttribute("aria-label", "Close these references");
    close.innerHTML = CLOSE_ICON_SVG;
    heading.appendChild(close);
    section.appendChild(heading);

    // The server caps very large reference sets (see ReferenceQueryService
    // MaxReferenceMatches, issue #366). When it trimmed, tell the user the
    // list is partial so a missing row isn't read as "no such reference".
    if (session.truncated) {
        const notice = document.createElement("p");
        notice.className = "muted source-viewer__refs-truncated";
        notice.setAttribute("role", "status");
        notice.textContent =
            `Showing the first ${count.toLocaleString()} references; refine your search to see the rest.`;
        section.appendChild(notice);
    }

    if (count === 0) {
        const p = document.createElement("p");
        p.className = "muted source-viewer__panel-empty";
        p.textContent = "No references in this Release's chain.";
        section.appendChild(p);
        return;
    }

    // Filter box — mirrors the Outline's, so you can narrow a long
    // reference set by object, enclosing member, target member, or the
    // snippet text. Wired after the groups are built.
    const search = document.createElement("span");
    search.className = "search";
    search.innerHTML = SEARCH_ICON_SVG;
    const filter = document.createElement("input");
    filter.type = "text";
    filter.className = "input source-viewer__refs-filter";
    filter.placeholder = "Filter references...";
    filter.setAttribute("aria-label", "Filter references");
    search.appendChild(filter);
    const tools = document.createElement("div");
    tools.className = "pane__sec pane__sec--tools";
    tools.appendChild(search);
    section.appendChild(tools);

    // Group references by their source object so every place that touches
    // the target clusters under one header — repeated calls from the same
    // table / page no longer scatter down a flat list.
    const refs = document.createElement("div");
    refs.className = "refs";
    for (const group of groups) {
        refs.appendChild(buildRefGroup(group, session, fileId, editorId));
    }
    section.appendChild(refs);

    const empty = document.createElement("p");
    empty.className = "muted sv-empty source-viewer__refs-empty";
    empty.hidden = true;
    empty.textContent = "No references match the filter.";
    section.appendChild(empty);

    wireRefsFilter(panel);
}

/// One `.refgrp`: a collapsible header naming the object the references sit
/// in, over its `.refhit` rows.
function buildRefGroup(group, session, fileId, editorId) {
    const grp = document.createElement("div");
    grp.className = "refgrp is-open";
    if (group.objectId != null) grp.dataset.objectId = String(group.objectId);

    const head = document.createElement("button");
    head.type = "button";
    head.className = "refgrp__h";
    head.setAttribute("aria-expanded", "true");
    if (group.objectKind) head.title = kindBadgeLabel(group.objectKind);

    const caret = document.createElement("span");
    caret.className = "sv-caret is-open";
    caret.innerHTML = CARET_ICON_SVG;
    head.appendChild(caret);

    const title = document.createElement("span");
    title.className = "refgrp__name";
    title.textContent = group.objectName;
    head.appendChild(title);

    const n = document.createElement("span");
    n.className = "refgrp__n";
    n.textContent = group.rows.length.toLocaleString();
    head.appendChild(n);
    grp.appendChild(head);

    const list = document.createElement("div");
    list.className = "refgrp__rows";
    for (const r of group.rows) {
        list.appendChild(buildRefsRow(r, session, fileId, editorId));
    }
    grp.appendChild(list);

    head.addEventListener("click", () => {
        const open = grp.classList.toggle("is-open");
        head.setAttribute("aria-expanded", open ? "true" : "false");
        caret.classList.toggle("is-open", open);
        list.hidden = !open;
    });
    return grp;
}

// Filters the rendered reference rows by object / member / snippet text,
// mirroring wireOutlineFilter: hides non-matching rows, collapses sections
// with no surviving rows, and shows an empty-state when nothing matches.
function wireRefsFilter(panel) {
    const filter = panel.querySelector(".source-viewer__refs-filter");
    if (!filter) return;
    const sections = Array.from(panel.querySelectorAll(".refgrp"));
    const empty = panel.querySelector(".source-viewer__refs-empty");

    filter.addEventListener("input", () => {
        const needle = filter.value.trim().toLowerCase();
        let anyVisible = false;
        for (const section of sections) {
            const items = Array.from(section.querySelectorAll(".refhit"));
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

/// One result row, shared by the References panel and the Find-in-file
/// panel: a right-aligned line number and the source line itself, with the
/// matched name marked inside it. The handoff's `.refhit` is deliberately
/// one line — you read the code, not a stack of metadata about it — so the
/// enclosing procedure, module and reference kind moved to the hover
/// tooltip rather than being dropped.
///
/// Returns an <a> when `href` is given (so the row is middle-clickable and
/// opens in a new tab like any link) and a <button> otherwise.
/// Trims a long source line so the marked name stays visible. Cutting only
/// from the right is what put the interesting token behind the ellipsis.
const HIT_LEAD_CHARS = 20;
function elideToMatch(text, match) {
    if (!match) return text;
    const at = text.toLowerCase().indexOf(match.toLowerCase());
    if (at <= HIT_LEAD_CHARS) return text;
    return "..." + text.slice(at - HIT_LEAD_CHARS);
}

function buildHitRow({ line, text, match, href, onActivate, title, filter }) {
    const row = document.createElement(href ? "a" : "button");
    row.className = "refhit";
    if (href) {
        row.href = href;
    } else {
        row.type = "button";
    }
    if (title) row.title = title;
    if (filter) row.dataset.filter = filter;

    const n = document.createElement("span");
    n.className = "refhit__n";
    n.textContent = line != null ? String(line) : "";
    row.appendChild(n);

    const c = document.createElement("span");
    c.className = "refhit__c";
    appendMarked(c, elideToMatch((text ?? "").trim(), match), match);
    row.appendChild(c);

    if (onActivate) {
        row.addEventListener("click", e => {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            e.preventDefault();
            onActivate(e);
        });
    }
    return row;
}

/// Appends `text` to `host`, wrapping each case-insensitive occurrence of
/// `match` in a <mark>. textContent throughout — the snippet is source code
/// from another org's release and never reaches innerHTML.
function appendMarked(host, text, match) {
    if (!match) {
        host.textContent = text;
        return;
    }
    const needle = match.toLowerCase();
    const hay = text.toLowerCase();
    let from = 0;
    for (;;) {
        const at = hay.indexOf(needle, from);
        if (at < 0) break;
        if (at > from) host.appendChild(document.createTextNode(text.slice(from, at)));
        const mark = document.createElement("mark");
        mark.textContent = text.slice(at, at + match.length);
        host.appendChild(mark);
        from = at + match.length;
    }
    if (from < text.length) host.appendChild(document.createTextNode(text.slice(from)));
}

function buildRefsRow(r, session, fileId, editorId) {
    const srcFid = r.sourceFileId;
    const ln = r.lineNumber;
    const hasLoc = srcFid != null && ln != null;
    const inSameFile = hasLoc && srcFid === fileId;

    const href = hasLoc
        ? `${FILE_URL_PREFIX}${srcFid}?line=${ln}&refSet=${encodeURIComponent(session.token)}`
        : `/object-explorer/object/${r.sourceObjectId}`;

    // What to mark inside the snippet: the member the reference names. Object-
    // scope rows (variable_type, extends_target) name no member, so they stay
    // unmarked rather than guessing at a span.
    const match = r.memberName || session.targetName;

    // No snippet (object-scope references carry none) — fall back to the
    // humanised reference kind so the row still says what it is.
    const text = r.snippet
        || (r.referenceKind ? r.referenceKind.replace(/_/g, " ") : categoryLabel(r.category));

    const row = buildHitRow({
        line: ln,
        text,
        match: r.snippet ? match : null,
        href,
        title: refsRowTitle(r),
        filter: [r.sourceObjectName, r.sourceMemberName, r.memberName, r.referenceKind, r.snippet]
            .filter(Boolean).join(" ").toLowerCase(),
        onActivate: inSameFile
            ? () => {
                scrollToLine(editorId, ln, true);
                const target = href.replace(location.origin, "");
                if (location.pathname + location.search !== target) {
                    history.pushState(null, "", href);
                }
            }
            : null,
    });
    attachRefsTooltip(row, r);
    return row;
}

/// Plain-text fallback for the row's `title`, for keyboard users and for
/// the moment before the hover card appears.
function refsRowTitle(r) {
    const parts = [];
    if (r.sourceMemberName) parts.push(`in ${r.sourceMemberName}`);
    if (r.sourceFilePath) parts.push(r.sourceFilePath);
    if (r.referenceKind) parts.push(r.referenceKind.replace(/_/g, " "));
    return parts.join(" - ");
}

// ── Symbol hover card ────────────────────────────────────────────
//
// Hovering an underlined name in the code pane shows the handoff's
// `.symcard`: the declaration's signature, where it lives, and the two
// jumps you would otherwise have to guess at. It is the payoff for
// reading someone else's extension — the signature of a procedure
// declared three files away, without leaving the line you are on.
//
// The card is fetched by symbol id, cached per page, and rendered into
// <body> so it can overhang the code pane's scroll box.

const SYMBOL_CARD_DELAY_MS = 320;
const SYMBOL_CARD_GRACE_MS = 140;
const symbolCardCache = new Map();

/// True when the platform's "go to definition" modifier is Cmd rather than
/// Ctrl. Only affects the label on the card's button.
function usesCommandKey() {
    const platform = navigator.userAgentData?.platform ?? navigator.platform ?? "";
    return /mac|iphone|ipad/i.test(platform);
}

async function fetchSymbolCard(symbolId) {
    if (symbolCardCache.has(symbolId)) return symbolCardCache.get(symbolId);
    let card = null;
    try {
        const res = await fetch(`/api/object-explorer/symbols/${symbolId}/card`,
            { credentials: "same-origin" });
        card = res.ok ? await res.json() : null;
    } catch (err) {
        console.warn("Symbol card fetch failed:", err);
    }
    symbolCardCache.set(symbolId, card);
    return card;
}

function wireSymbolCard(root, codeHost, editorId, fileId, handlers) {
    let card = null;
    let showTimer = null;
    let hideTimer = null;
    let shownFor = null;
    let overCard = false;

    const clearTimers = () => {
        clearTimeout(showTimer); showTimer = null;
        clearTimeout(hideTimer); hideTimer = null;
    };

    const hide = () => {
        clearTimers();
        shownFor = null;
        if (card) { card.remove(); card = null; }
    };

    const scheduleHide = () => {
        clearTimeout(hideTimer);
        hideTimer = setTimeout(() => { if (!overCard) hide(); }, SYMBOL_CARD_GRACE_MS);
    };

    codeHost.addEventListener("mouseover", e => {
        const token = e.target instanceof Element
            ? e.target.closest("[data-symbol-id]")
            : null;
        // Object headers stamp an oe_module_objects id here, not a symbol id
        // (see buildDeclarationDecorationExtensions) — no card for those.
        const isSymbol = token
            && (token.classList.contains("cm-symbol-ref")
                || token.dataset.memberSymbol === "1");
        if (!isSymbol) {
            if (shownFor) scheduleHide();
            return;
        }
        const id = Number(token.dataset.symbolId);
        if (!Number.isFinite(id) || id <= 0) return;
        if (shownFor === token) { clearTimeout(hideTimer); return; }
        clearTimers();
        showTimer = setTimeout(async () => {
            const data = await fetchSymbolCard(id);
            if (!data || !document.contains(token)) return;
            hide();
            shownFor = token;
            card = buildSymbolCard(data, fileId, editorId, handlers, hide);
            card.addEventListener("mouseenter", () => { overCard = true; clearTimeout(hideTimer); });
            card.addEventListener("mouseleave", () => { overCard = false; scheduleHide(); });
            document.body.appendChild(card);
            placeSymbolCard(card, token);
        }, SYMBOL_CARD_DELAY_MS);
    });

    codeHost.addEventListener("mouseleave", () => { overCard = false; scheduleHide(); });
    codeHost.addEventListener("scroll", hide, true);
    window.addEventListener("keydown", e => { if (e.key === "Escape") hide(); });
    // Any navigation away from this viewer takes the card with it.
    window.addEventListener("popstate", hide);
}

function buildSymbolCard(data, fileId, editorId, handlers, dismiss) {
    const el = document.createElement("div");
    el.className = "symcard";
    el.setAttribute("role", "tooltip");

    const sig = document.createElement("span");
    sig.className = "symcard__sig";
    sig.textContent = data.signature || data.name;
    el.appendChild(sig);

    const meta = document.createElement("span");
    meta.className = "symcard__meta";
    // Two facts, not three: the meta row is one 356px monospace line, and the
    // file name already says which object the member sits on. The owner is on
    // the card's title attribute for the cases where it doesn't.
    el.title = `${data.kind.replace(/_/g, " ")} ${data.name} - ${data.ownerKind} ${data.ownerName} (${data.moduleName})`;
    for (const part of [
        data.moduleName,
        data.filePath ? `${baseName(data.filePath)}:${data.lineNumber}` : null,
    ]) {
        if (!part) continue;
        const span = document.createElement("span");
        span.textContent = part;
        meta.appendChild(span);
    }
    el.appendChild(meta);

    const acts = document.createElement("span");
    acts.className = "symcard__acts";

    if (data.fileId != null) {
        const go = document.createElement("a");
        go.className = "btn btn--sm";
        go.href = `${FILE_URL_PREFIX}${data.fileId}?line=${data.lineNumber}`;
        go.append("Go to definition");
        go.appendChild(kbdChip(usesCommandKey() ? "Cmd" : "Ctrl"));
        go.appendChild(kbdChip("click"));
        if (data.fileId === fileId) {
            go.addEventListener("click", e => {
                if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
                e.preventDefault();
                dismiss();
                scrollToLine(editorId, data.lineNumber, true);
            });
        }
        acts.appendChild(go);
    }

    const refs = document.createElement("button");
    refs.type = "button";
    refs.className = "btn btn--sm";
    refs.append("Find references");
    refs.appendChild(kbdChip("Shift"));
    refs.appendChild(kbdChip("F12"));
    refs.addEventListener("click", () => {
        dismiss();
        handlers.onFindReferences(data.symbolId);
    });
    acts.appendChild(refs);

    el.appendChild(acts);
    return el;
}

/// A `.pane__sec-h` is an uppercased micro-label, which is right for a
/// category ("FIELDS") and wrong for an AL identifier — uppercasing
/// `GetLegalEntityName` throws away the camelCase that makes it readable.
/// Section headings that name user data put it in one of these instead.
function sectionName(text) {
    const span = document.createElement("span");
    span.className = "sv-sec-name";
    span.textContent = text;
    return span;
}

function kbdChip(label) {
    const k = document.createElement("span");
    k.className = "kbd";
    k.textContent = label;
    return k;
}

function baseName(path) {
    const slash = (path ?? "").lastIndexOf("/");
    return slash < 0 ? path : path.slice(slash + 1);
}

/// Anchors the card under the hovered token, then pulls it back inside the
/// viewport — flipping above the token when there is no room below.
function placeSymbolCard(card, token) {
    const anchor = token.getBoundingClientRect();
    const box = card.getBoundingClientRect();
    const margin = 8;
    let left = anchor.left + window.scrollX;
    left = Math.min(left, window.scrollX + document.documentElement.clientWidth - box.width - margin);
    left = Math.max(left, window.scrollX + margin);

    let top = anchor.bottom + window.scrollY + 6;
    if (anchor.bottom + box.height + 6 > document.documentElement.clientHeight) {
        top = anchor.top + window.scrollY - box.height - 6;
    }
    top = Math.max(top, window.scrollY + margin);

    card.style.left = `${Math.round(left)}px`;
    card.style.top = `${Math.round(top)}px`;
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

/// Humanises a raw symbol / object kind ("event_publisher" -> "event pub")
/// for the reference group header's tooltip. Mirrors KindBadgeLabel in
/// SourceFileViewer.razor — keep the two in step when a new kind lands.
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
    if (countChip) countChip.textContent = String(rows.length);
    if (rows.length === 0) {
        const p = document.createElement("p");
        p.className = "muted source-viewer__panel-empty";
        p.textContent = "(none)";
        list.appendChild(p);
        // Collapse the empty section so it doesn't take vertical space.
        if (section) {
            section.classList.remove("is-open");
            const toggle = section.querySelector(".sv-section__toggle");
            toggle?.setAttribute("aria-expanded", "false");
            const chevron = section.querySelector(".sv-caret");
            chevron?.classList.remove("is-open");
            list.hidden = true;
        }
        return;
    }
    for (const row of rows) {
        list.appendChild(buildDepsRow(row));
    }
}

/// One `.orow` in the outline's Using / Used-by sections. Targets without
/// ingested source render as a non-clickable row; the title says why.
function buildDepsRow(row) {
    const navigable = row.targetFileId != null;
    const el = document.createElement(navigable ? "a" : "span");
    el.className = navigable ? "orow" : "orow is-inert";
    if (navigable) {
        el.href = `/object-explorer/file/${row.targetFileId}?line=${row.targetLineNumber ?? 1}`;
        el.title = [row.targetModuleName, (row.referenceKind || "").replace(/_/g, " ")]
            .filter(Boolean).join(" - ");
    } else {
        el.title = "No source imported for this object.";
    }

    const glyph = document.createElement("span");
    glyph.className = "orow__glyph";
    glyph.setAttribute("aria-hidden", "true");
    glyph.textContent = "{";
    el.appendChild(glyph);

    const name = document.createElement("span");
    name.className = navigable ? "orow__name" : "orow__name muted";
    name.textContent = row.targetObjectName ?? "";
    el.appendChild(name);

    const mod = document.createElement("span");
    mod.className = "orow__type";
    mod.textContent = row.targetModuleName ?? "";
    el.appendChild(mod);
    return el;
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
/// Shift+F12 -> find references to whatever the caret is on. Chosen over
/// F12-for-definition because a bare F12 opens the browser's developer tools
/// on every desktop browser; Cmd/Ctrl-click carries definition instead.
///
/// Bound on window rather than the editor DOM for the same reason
/// wireSearchShortcut is: the viewer's CodeMirror is read-only, so its
/// content element never takes focus and a listener on it would never hear
/// the key. The document.contains(root) guard keeps it scoped to the page.
function wireFindReferencesShortcut(root, editorId, findReferencesAt) {
    window.addEventListener("keydown", e => {
        if (e.key !== "F12" || !e.shiftKey || e.ctrlKey || e.metaKey || e.altKey) return;
        if (!document.contains(root)) return;
        const at = cursorPosition(editorId);
        if (!at) return;
        e.preventDefault();
        findReferencesAt(at.line, at.column);
    });
}

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
// alongside the code. Worse, a non-editable contentDOM never takes
// DOM focus, so clicking around the code leaves document.activeElement
// on <body> and a focus check alone can't tell "user is working in the
// code" from "user is elsewhere". Track where the last pointer-down
// landed instead (seeded true — the code IS the page's main surface),
// and route Ctrl/Cmd-A to CodeMirror's selectAll whenever the user's
// attention is in the editor. Real text inputs (the outline filter,
// the search panel) keep their native select-all.
function wireSelectAllShortcut(root, editorId, codeHost) {
    let pointerInEditor = true;
    document.addEventListener("pointerdown", e => {
        if (!document.contains(root)) return;
        pointerInEditor = e.target instanceof Node && codeHost.contains(e.target);
    });
    window.addEventListener("keydown", e => {
        const isA = e.key === "a" || e.key === "A";
        if (!isA) return;
        if (!(e.ctrlKey || e.metaKey)) return;
        if (e.shiftKey || e.altKey) return;
        if (!document.contains(root)) return;
        const active = document.activeElement;
        // Focus in a real input/textarea → native select-all of that field.
        if (active instanceof HTMLElement
            && (active.tagName === "INPUT" || active.tagName === "TEXTAREA" || active.isContentEditable)) {
            return;
        }
        const inEditor = pointerInEditor
            || (active && codeHost.contains(active))
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
    const filter = root.querySelector(".sv-filter");
    if (!filter) return;
    const sections = Array.from(root.querySelectorAll(".sv-section"));
    const empty = root.querySelector(".sv-empty");

    filter.addEventListener("input", () => {
        const needle = filter.value.trim().toLowerCase();
        let anyVisible = false;
        for (const section of sections) {
            const rows = Array.from(section.querySelectorAll(".sv-row"));
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
    const toggles = root.querySelectorAll(".sv-section__toggle");
    toggles.forEach(btn => {
        btn.addEventListener("click", () => {
            const section = btn.parentElement;
            if (!section) return;
            const open = section.classList.toggle("is-open");
            btn.setAttribute("aria-expanded", open ? "true" : "false");
            const chevron = btn.querySelector(".sv-caret");
            if (chevron) chevron.classList.toggle("is-open", open);
            const list = section.querySelector(".sv-list");
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
