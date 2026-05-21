// Module for the Piper page (/piper). Owns the document-level Alt+1/Alt+2
// keybind dispatcher and the localStorage round-trip for remembering which
// input mode the user was last on (Text or Table).

const MODE_KEY = "piper.inputMode";

let currentRef = null;
let keyHandler = null;

export function init(dotNetRef) {
    // Replace any previous handler from an earlier page mount (Blazor
    // Server tears modules down when the user navigates away, but be
    // defensive in case dispose was skipped on disconnect).
    detach();

    currentRef = dotNetRef;
    keyHandler = (ev) => {
        if (!ev.altKey || ev.ctrlKey || ev.metaKey) return;
        // Alt+1 → Text, Alt+2 → Table. Both `event.key` (which honours
        // keyboard layout) and `event.code` (which is physical) cover the
        // common cases — Mac Option layouts produce printable characters
        // for some digit rows.
        let mode = null;
        if (ev.key === "1" || ev.code === "Digit1") mode = "Text";
        else if (ev.key === "2" || ev.code === "Digit2") mode = "Table";
        if (mode === null) return;

        ev.preventDefault();
        currentRef.invokeMethodAsync("SetModeFromKeybind", mode);
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

export function readMode() {
    try {
        return localStorage.getItem(MODE_KEY);
    } catch {
        return null;
    }
}

export function writeMode(mode) {
    try {
        localStorage.setItem(MODE_KEY, mode);
    } catch {
        // Quota exceeded, private mode, etc. — non-fatal.
    }
}
