// Module for the Object Explorer release detail page. Owns the
// document-level Alt+1..Alt+4 keybind dispatcher that flips the "Search
// in" scope (Objects / Procedures / Content / Compare). The dispatcher
// lives in JS rather than a Blazor `@onkeydown` because focus typically
// sits in the search textbox and Blazor event handlers on outer divs
// don't see keystrokes that bubble from a child input.

let currentRef = null;
let keyHandler = null;

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
}

export function detach() {
    if (keyHandler) {
        document.removeEventListener("keydown", keyHandler);
        keyHandler = null;
    }
    currentRef = null;
}
