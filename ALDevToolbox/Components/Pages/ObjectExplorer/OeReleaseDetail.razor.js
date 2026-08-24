// Module for the Object Explorer release detail page. Owns the
// document-level Alt+1..Alt+4 keybind dispatcher that flips the "Search
// in" scope (Objects / Procedures / Content / Compare). The dispatcher
// lives in JS rather than a Blazor `@onkeydown` because focus typically
// sits in the search textbox and Blazor event handlers on outer divs
// don't see keystrokes that bubble from a child input.

let currentRef = null;
let keyHandler = null;
let scrollObserver = null;
let outsideClickHandler = null;
let toggleHandler = null;

// The three filter disclosures in the bar: Search tips, Type and Options.
const FDROP_OPEN = "details.fdrop[open]";

// Closes one of them. Focus goes back to its summary, because that is where the
// user was before the panel opened - dropping a keyboard user on <body> in the
// middle of the filter row is worse than leaving the panel up (#612).
function closeFdrop(details, refocus) {
    details.removeAttribute("open");
    if (refocus) {
        const summary = details.querySelector("summary");
        if (summary) summary.focus();
    }
}

const SCOPE_BY_DIGIT = {
    "1": "Objects",
    "2": "Procedures",
    "3": "Content",
    "4": "Compare",
};

export function init(dotNetRef) {
    detach();
    currentRef = dotNetRef;
    keyHandler = (ev) => {
        // Escape closes an open filter dropdown. A native <details> has no
        // dismiss key at all, so until #612 the only way back out of Search
        // tips, Type or Options was to hit the summary a second time.
        if (ev.key === "Escape" && !ev.altKey && !ev.ctrlKey && !ev.metaKey) {
            const open = Array.from(document.querySelectorAll(FDROP_OPEN));
            if (open.length === 0) return;   // nothing to close - don't swallow it
            // Layered Escape, in the same order as the source viewer's tree
            // search (wwwroot/source-viewer.js): typed text wins. A non-empty
            // <input type="search"> - the Options panel's namespace box - keeps
            // the first press for the browser's native "clear the box", and the
            // second press, with the box now empty, closes the panel. An empty
            // box has nothing to clear, so Escape keeps travelling out to here.
            // Only for a box INSIDE an open panel: with focus elsewhere (the
            // main search box, say), Escape is aimed at the panel, not the box.
            const target = ev.target;
            if (target instanceof HTMLInputElement && target.type === "search" && target.value !== ""
                && open.some((d) => d.contains(target))) return;
            ev.preventDefault();
            for (const d of open) closeFdrop(d, d.contains(target));
            return;
        }
        // F3 focuses the search box (overriding the browser's find-next), so a
        // keyboard-first user can jump back to search from anywhere on the page.
        if (ev.key === "F3" && !ev.altKey && !ev.ctrlKey && !ev.metaKey && !ev.shiftKey) {
            // By id, not by class. This used to select `input.admin-search-input`,
            // and PR 14c moved the box onto the design layer's `.input` - so the
            // selector quietly returned null, the handler fell through without
            // calling preventDefault(), and F3 went to the browser's find-next.
            // Nothing failed loudly; the Alt+1..4 half of this same handler kept
            // working, which is what made it look fine.
            const search = document.getElementById("oe-release-search");
            if (search) {
                ev.preventDefault();
                search.focus();
                search.select();
            }
            return;
        }
        if (!ev.altKey || ev.ctrlKey || ev.metaKey) return;
        const digit = ev.key in SCOPE_BY_DIGIT
            ? ev.key
            : (ev.code === "Digit1" ? "1"
               : ev.code === "Digit2" ? "2"
               : ev.code === "Digit3" ? "3"
               : ev.code === "Digit4" ? "4"
               : null);
        if (digit === null) return;
        ev.preventDefault();
        currentRef.invokeMethodAsync("SetScopeFromKeybind", SCOPE_BY_DIGIT[digit]);
    };
    document.addEventListener("keydown", keyHandler);

    // A native <details> popover only closes when you click its summary again.
    // Close any open one when the click lands outside it, matching the
    // dismiss-on-outside-click users expect. See #273.
    //
    // The selector here was `details.kind-filter[open]`, and the design-system
    // port renamed the disclosures to `.fdrop` - so it matched nothing and
    // outside-click was silently dead for all three of them (#612). Same
    // failure shape as the F3 selector above: no error, just a handler walking
    // an empty list.
    outsideClickHandler = (ev) => {
        document.querySelectorAll(FDROP_OPEN).forEach((d) => {
            if (!d.contains(ev.target)) d.removeAttribute("open");
        });
    };
    document.addEventListener("click", outsideClickHandler);

    // One dropdown open at a time: opening Type while Options is up closes
    // Options. Native <details> siblings are independent of each other; the
    // `name` attribute would group them, but only when all three are really
    // rendered as <details> - Options degrades to a plain disabled <button> on
    // the tabs that have no extra filters - and this module already owns the
    // other two ways these panels close, so all three live together.
    //
    // Delegated, and in the capture phase, for two reasons: `toggle` does not
    // bubble, and the Options disclosure sits inside an @if that Blazor
    // re-renders whenever the scope changes - a listener attached per element
    // in init() would go with the discarded node. Clearing `open` on the others
    // fires their own toggle, which returns immediately on `!open`.
    toggleHandler = (ev) => {
        const opened = ev.target;
        if (!(opened instanceof HTMLElement) || !opened.matches("details.fdrop") || !opened.open) return;
        document.querySelectorAll(FDROP_OPEN).forEach((other) => {
            if (other !== opened) other.removeAttribute("open");
        });
    };
    document.addEventListener("toggle", toggleHandler, true);
}

// Infinite scroll: (re)observe the objects-grid sentinel so it pulls the next
// page when it scrolls into view. Re-resolved on demand because Blazor recreates
// the sentinel node when the result set or scope changes. rootMargin pre-fetches
// a little before the sentinel is actually visible, so scrolling feels seamless.
export function watchSentinel() {
    const el = document.getElementById("oe-objects-sentinel");
    if (scrollObserver) {
        scrollObserver.disconnect();
        scrollObserver = null;
    }
    if (!el || !currentRef) return;
    scrollObserver = new IntersectionObserver((entries) => {
        for (const entry of entries) {
            if (entry.isIntersecting && currentRef) {
                currentRef.invokeMethodAsync("LoadMoreObjects");
            }
        }
    }, { rootMargin: "300px" });
    scrollObserver.observe(el);
}

export function detach() {
    if (keyHandler) {
        document.removeEventListener("keydown", keyHandler);
        keyHandler = null;
    }
    if (outsideClickHandler) {
        document.removeEventListener("click", outsideClickHandler);
        outsideClickHandler = null;
    }
    if (toggleHandler) {
        // Capture flag has to match the one addEventListener was given, or the
        // listener stays attached.
        document.removeEventListener("toggle", toggleHandler, true);
        toggleHandler = null;
    }
    if (scrollObserver) {
        scrollObserver.disconnect();
        scrollObserver = null;
    }
    currentRef = null;
}
