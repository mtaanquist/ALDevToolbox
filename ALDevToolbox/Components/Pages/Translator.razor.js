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

// Lightweight check for the restore prompt — returns just the file name.
export async function peek() {
    const rec = await getRec();
    return rec ? rec.fileName : null;
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
    form.querySelector("#tr-export-filename").value = fileName;
    form.querySelector("#tr-export-original").value = rec.originalXml;
    form.querySelector("#tr-export-edits").value = editsJson;
    form.submit();
    return true;
}

export function initKeys(ref) {
    detachKeys();
    dotNetRef = ref;
    keyHandler = (ev) => {
        if (!ev.altKey || ev.ctrlKey || ev.metaKey) return;
        if (ev.code === "Enter") { ev.preventDefault(); dotNetRef.invokeMethodAsync("SaveAndNextFromKey"); return; }
        if (ev.code === "ArrowUp") { ev.preventDefault(); dotNetRef.invokeMethodAsync("NavFromKey", -1); return; }
        if (ev.code === "ArrowDown") { ev.preventDefault(); dotNetRef.invokeMethodAsync("NavFromKey", 1); return; }
        const m = /^Digit([1-9])$/.exec(ev.code);
        if (m) { ev.preventDefault(); dotNetRef.invokeMethodAsync("ApplySuggestionFromKey", parseInt(m[1], 10)); }
    };
    document.addEventListener("keydown", keyHandler);
}

export function detachKeys() {
    if (keyHandler) {
        document.removeEventListener("keydown", keyHandler);
        keyHandler = null;
    }
    dotNetRef = null;
}
