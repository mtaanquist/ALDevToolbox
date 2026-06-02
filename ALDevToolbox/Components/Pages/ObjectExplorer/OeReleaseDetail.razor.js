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
        // F3 focuses the search box (overriding the browser's find-next), so a
        // keyboard-first user can jump back to search from anywhere on the page.
        if (ev.key === "F3" && !ev.altKey && !ev.ctrlKey && !ev.metaKey && !ev.shiftKey) {
            const search = document.querySelector("input.admin-search-input");
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

    // The Object-type filter is a native <details> popover, which only closes
    // when you click its summary again. Close any open one when the click lands
    // outside it, matching the dismiss-on-outside-click users expect. See #273.
    outsideClickHandler = (ev) => {
        document.querySelectorAll("details.kind-filter[open]").forEach((d) => {
            if (!d.contains(ev.target)) d.removeAttribute("open");
        });
    };
    document.addEventListener("click", outsideClickHandler);
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
    if (scrollObserver) {
        scrollObserver.disconnect();
        scrollObserver = null;
    }
    currentRef = null;
}
