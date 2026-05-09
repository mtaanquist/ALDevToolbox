// CodeMirror 6 companion for /admin/templates TOML mode.
//
// We pull CodeMirror in as ESM modules from esm.sh so the rest of the app
// stays JS-bundler-free (per .design/milestones.md → P2.2). Versions are
// pinned to keep cache hits stable and to avoid surprise behaviour drifts.
//
// The exported functions are ID-keyed so Blazor's IJSObjectReference can
// stay a plain integer rather than wrapping the EditorView itself: simpler
// lifecycle, no DotNet.createJSObjectReference plumbing.

import { EditorView, lineNumbers, highlightActiveLineGutter, highlightSpecialChars,
    drawSelection, dropCursor, rectangularSelection, crosshairCursor,
    highlightActiveLine, keymap } from "https://esm.sh/@codemirror/view@6.34.1";
import { EditorState, Compartment } from "https://esm.sh/@codemirror/state@6.4.1";
import { defaultKeymap, history, historyKeymap, indentWithTab }
    from "https://esm.sh/@codemirror/commands@6.7.1";
import { syntaxHighlighting, defaultHighlightStyle, indentOnInput, bracketMatching,
    foldGutter, foldKeymap, StreamLanguage }
    from "https://esm.sh/@codemirror/language@6.10.6";
import { searchKeymap, highlightSelectionMatches }
    from "https://esm.sh/@codemirror/search@6.5.7";
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap }
    from "https://esm.sh/@codemirror/autocomplete@6.18.3";
import { lintKeymap, lintGutter, setDiagnostics }
    from "https://esm.sh/@codemirror/lint@6.8.4";
import { toml }
    from "https://esm.sh/@codemirror/legacy-modes@6.4.1/mode/toml";
import { oneDark }
    from "https://esm.sh/@codemirror/theme-one-dark@6.1.2";

let nextId = 1;
const editors = new Map();

// Best-effort read of the active theme — matches the rules in
// wwwroot/theme.js so the editor flips with the rest of the page.
function isDarkTheme() {
    const attr = document.documentElement.getAttribute("data-theme");
    if (attr === "dark") return true;
    if (attr === "light") return false;
    return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}

function themeExtensions() {
    return isDarkTheme() ? [oneDark] : [];
}

function buildExtensions(themeCompartment) {
    return [
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightSpecialChars(),
        history(),
        foldGutter(),
        drawSelection(),
        dropCursor(),
        EditorState.allowMultipleSelections.of(true),
        indentOnInput(),
        syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
        bracketMatching(),
        closeBrackets(),
        autocompletion(),
        rectangularSelection(),
        crosshairCursor(),
        highlightActiveLine(),
        highlightSelectionMatches(),
        keymap.of([
            ...closeBracketsKeymap,
            ...defaultKeymap,
            ...searchKeymap,
            ...historyKeymap,
            ...foldKeymap,
            ...completionKeymap,
            ...lintKeymap,
            indentWithTab,
        ]),
        StreamLanguage.define(toml),
        lintGutter(),
        themeCompartment.of(themeExtensions()),
    ];
}

export function mount(container, initialValue) {
    if (!container) return 0;
    const id = nextId++;
    const themeCompartment = new Compartment();

    const view = new EditorView({
        parent: container,
        state: EditorState.create({
            doc: initialValue ?? "",
            extensions: buildExtensions(themeCompartment),
        }),
    });

    const reconfigureTheme = () => {
        view.dispatch({ effects: themeCompartment.reconfigure(themeExtensions()) });
    };

    const themeObserver = new MutationObserver(reconfigureTheme);
    themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["data-theme"],
    });

    const mql = window.matchMedia?.("(prefers-color-scheme: dark)");
    mql?.addEventListener?.("change", reconfigureTheme);

    editors.set(id, {
        view,
        dispose: () => {
            themeObserver.disconnect();
            mql?.removeEventListener?.("change", reconfigureTheme);
            view.destroy();
        },
    });
    return id;
}

export function getValue(id) {
    return editors.get(id)?.view.state.doc.toString() ?? "";
}

export function setValue(id, value) {
    const e = editors.get(id);
    if (!e) return;
    const next = value ?? "";
    if (e.view.state.doc.toString() === next) return;
    e.view.dispatch({
        changes: { from: 0, to: e.view.state.doc.length, insert: next },
    });
}

// Issues come from the server: line is 1-based, message is human text.
// We render them as gutter markers + underlines via CodeMirror's lint
// extension so admins see exactly which line refused to parse.
export function setIssues(id, issues) {
    const e = editors.get(id);
    if (!e) return;
    const list = Array.isArray(issues) ? issues : [];
    const docLines = e.view.state.doc.lines;
    const diagnostics = list
        .filter(it => it && Number.isInteger(it.line) && it.line >= 1)
        .map(it => {
            const line = Math.max(1, Math.min(it.line, docLines));
            const li = e.view.state.doc.line(line);
            return {
                from: li.from,
                to: li.to,
                severity: it.severity === "warning" ? "warning" : "error",
                message: it.message ?? "",
            };
        });
    e.view.dispatch(setDiagnostics(e.view.state, diagnostics));
}

export function dispose(id) {
    const e = editors.get(id);
    if (!e) return;
    try { e.dispose(); } catch { /* ignore */ }
    editors.delete(id);
}
