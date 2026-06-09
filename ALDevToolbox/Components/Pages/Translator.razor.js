// Companion module for the Translator page (/translator).
//
// Owns three browser-side concerns the C# component can't do itself:
//   1. IndexedDB persistence of the in-progress file. The working file can be
//      several MB — too big for localStorage's ~5 MB quota — so the verbatim
//      original XML + the edit overlay live in IndexedDB and survive a refresh
//      or a dropped SignalR circuit. Nothing is sent to the server until export.
//   2. The Alt-chord keyboard dispatcher (Poedit-style). Detected by physical
//      `event.code` + `event.altKey` so it behaves identically on Windows/Linux
//      and macOS, where Option turns number/letter keys into special characters.
//   3. The export hand-off: fills a hidden form from IndexedDB and submits it,
//      so the multi-MB original never travels over the Blazor connection.
//
// Multi-MB payloads cross the circuit via streaming, never as plain interop
// args: .NET -> JS uses a DotNetStreamReference (putBaseStream); JS -> .NET
// returns an ArrayBuffer that .NET reads as an IJSStreamReference
// (loadOriginalStream). Both bypass the 32 KB hub message limit.

const DB_NAME = "aldt-translator";
const STORE = "session";
const KEY = "current";

let keyHandler = null;
let dotNetRef = null;

function openDb() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = () => {
            const db = req.result;
            if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE);
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

async function getRec() {
    try {
        const db = await openDb();
        return await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, "readonly");
            const req = tx.objectStore(STORE).get(KEY);
            req.onsuccess = () => resolve(req.result || null);
            req.onerror = () => reject(req.error);
        });
    } catch {
        return null;
    }
}

async function putRec(rec) {
    try {
        rec.savedAt = Date.now();
        const db = await openDb();
        await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, "readwrite");
            tx.objectStore(STORE).put(rec, KEY);
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
        return true;
    } catch {
        return false;
    }
}

// Called once when a file is opened. The original XML arrives as a
// DotNetStreamReference so multi-MB files don't hit the interop arg limit.
// Resets the edit overlay.
export async function putBaseStream(fileName, sourceLang, targetLang, streamRef) {
    const buf = await streamRef.arrayBuffer();
    const originalXml = new TextDecoder("utf-8").decode(buf);
    await putRec({ fileName, originalXml, sourceLang, targetLang, edits: {} });
}

// Debounced incremental edit save (for resume). `editsJson` is the full overlay.
export async function putEdits(editsJson) {
    const rec = await getRec();
    if (!rec) return;
    try { rec.edits = JSON.parse(editsJson); } catch { return; }
    await putRec(rec);
}

export async function putMeta(fileName, targetLang) {
    const rec = await getRec();
    if (!rec) return;
    rec.fileName = fileName;
    rec.targetLang = targetLang;
    await putRec(rec);
}

// Lightweight check for the restore prompt — the file name + when it was last
// saved (epoch ms, 0 for sessions written before timestamps existed).
export async function peek() {
    const rec = await getRec();
    if (!rec) return null;
    return { fileName: rec.fileName, savedAt: rec.savedAt || 0 };
}

// Small metadata for an explicit restore (no original XML — that streams).
export async function loadMeta() {
    const rec = await getRec();
    if (!rec) return null;
    return {
        fileName: rec.fileName,
        sourceLang: rec.sourceLang || null,
        targetLang: rec.targetLang || null,
        edits: rec.edits || {},
    };
}

// The verbatim original, returned as an ArrayBuffer so .NET receives it as an
// IJSStreamReference and reads it as a stream (bypasses the hub size limit).
export async function loadOriginalStream() {
    const rec = await getRec();
    const xml = rec && rec.originalXml ? rec.originalXml : "";
    return new TextEncoder().encode(xml).buffer;
}

export async function clear() {
    try {
        const db = await openDb();
        await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, "readwrite");
            tx.objectStore(STORE).delete(KEY);
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    } catch { /* ignore */ }
}

// Reads the verbatim original from IndexedDB, fills the hidden export form with
// it + the (authoritative) edit overlay + the chosen file name, and submits.
// The original never crosses the SignalR circuit — this is a plain browser POST
// that returns the file as a download.
export async function exportNow(fileName, editsJson) {
    const rec = await getRec();
    if (!rec || !rec.originalXml) return false;
    const form = document.getElementById("tr-export-form");
    if (!form) return false;
    // The form submit below is a navigation, which would otherwise fire the
    // unsaved-changes beforeunload prompt. Detach it first — exporting is a
    // deliberate save, not an accidental exit.
    setBeforeUnload(false);
    form.querySelector("#tr-export-filename").value = fileName;
    form.querySelector("#tr-export-original").value = rec.originalXml;
    form.querySelector("#tr-export-edits").value = editsJson;
    form.submit();
    return true;
}

// Browser-level guard against losing edits to a full reload, tab close, or a
// browser back that exits the SPA. In-app navigation goes through Blazor's
// LocationChangingHandler + a proper modal instead (see Translator.razor).
let beforeUnloadAttached = false;
function beforeUnloadHandler(e) {
    e.preventDefault();
    // Modern browsers ignore the message and show their own copy, but Chrome
    // still needs a non-empty returnValue to trigger the dialog.
    e.returnValue = "";
    return "";
}

export function setBeforeUnload(enabled) {
    if (enabled && !beforeUnloadAttached) {
        window.addEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = true;
    } else if (!enabled && beforeUnloadAttached) {
        window.removeEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = false;
    }
}

// #306 follow-up: a native <input type=file> does NOT reliably fire `change`
// when a file is *dropped* on it — Firefox never does, and in every browser a
// drop that lands even a pixel outside the input is ignored. The browser then
// falls back to its default action for a file dropped on the page: navigate to
// it, which the user sees as the file "downloading". So we wire the drop
// ourselves: stash the dropped file on the hidden input via a DataTransfer and
// dispatch `change`, which runs Blazor's InputFile -> OnUploadAsync exactly as
// a click-to-browse pick would. A window-level guard swallows drops that miss
// the zone so an off-target drop can't navigate the page and lose the session.
let dropTeardown = null;

export function initDropZone(zoneSelector, inputSelector) {
    teardownDropZone();
    const zone = document.querySelector(zoneSelector);
    const input = document.querySelector(inputSelector);
    if (!zone || !input) return;

    const swallow = (e) => { e.preventDefault(); };
    const onDragEnter = (e) => { e.preventDefault(); zone.classList.add("tr-drop--over"); };
    const onDragOver = (e) => {
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = "copy";
    };
    const onDragLeave = (e) => {
        // Child elements bubble their own dragleave; only clear the highlight
        // when the pointer has actually left the zone's subtree.
        if (!zone.contains(e.relatedTarget)) zone.classList.remove("tr-drop--over");
    };
    const onDrop = (e) => {
        e.preventDefault();
        zone.classList.remove("tr-drop--over");
        const files = e.dataTransfer && e.dataTransfer.files;
        if (!files || !files.length) return;
        const dt = new DataTransfer();
        dt.items.add(files[0]);
        input.files = dt.files;
        input.dispatchEvent(new Event("change", { bubbles: true }));
    };

    zone.addEventListener("dragenter", onDragEnter);
    zone.addEventListener("dragover", onDragOver);
    zone.addEventListener("dragleave", onDragLeave);
    zone.addEventListener("drop", onDrop);
    window.addEventListener("dragover", swallow);
    window.addEventListener("drop", swallow);

    dropTeardown = () => {
        zone.removeEventListener("dragenter", onDragEnter);
        zone.removeEventListener("dragover", onDragOver);
        zone.removeEventListener("dragleave", onDragLeave);
        zone.removeEventListener("drop", onDrop);
        window.removeEventListener("dragover", swallow);
        window.removeEventListener("drop", swallow);
    };
}

export function teardownDropZone() {
    if (dropTeardown) { dropTeardown(); dropTeardown = null; }
}

export function initKeys(ref) {
    detachKeys();
    dotNetRef = ref;
    keyHandler = (ev) => {
        if (!ev.altKey || ev.ctrlKey || ev.metaKey) return;
        if (ev.code === "Enter") { ev.preventDefault(); dotNetRef.invokeMethodAsync("SaveAndNextFromKey"); return; }
        if (ev.code === "ArrowUp") { ev.preventDefault(); dotNetRef.invokeMethodAsync("NavFromKey", -1); return; }
        if (ev.code === "ArrowDown") { ev.preventDefault(); dotNetRef.invokeMethodAsync("NavFromKey", 1); return; }
        // #307: Alt+Left/Right are the browser's Back/Forward on Windows/Linux —
        // swallow them so an accidental press doesn't throw the translator off
        // the page mid-edit. They are deliberately NOT unit navigation (that
        // stays on Alt+Up/Down).
        if (ev.code === "ArrowLeft" || ev.code === "ArrowRight") { ev.preventDefault(); return; }
        const m = /^Digit([1-9])$/.exec(ev.code);
        if (m) { ev.preventDefault(); dotNetRef.invokeMethodAsync("ApplySuggestionFromKey", parseInt(m[1], 10)); }
    };
    document.addEventListener("keydown", keyHandler);
}

// #304: move the cursor into the target box for the current selection and put
// it at the end of any existing text, so the user can start typing immediately
// after picking a unit. List view only — there's no .tr-tgtarea in grid view,
// where the clicked cell already holds focus.
export function focusTarget() {
    const el = document.querySelector(".tr-tgtarea");
    if (!el) return;
    el.focus();
    const end = el.value.length;
    try { el.setSelectionRange(end, end); } catch { /* not all inputs support it */ }
}

export function detachKeys() {
    if (keyHandler) {
        document.removeEventListener("keydown", keyHandler);
        keyHandler = null;
    }
    dotNetRef = null;
}
