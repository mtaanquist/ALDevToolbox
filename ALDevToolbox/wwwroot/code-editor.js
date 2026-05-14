// CodeMirror 6 companion shared by every page that embeds a syntax-aware
// text editor (TOML for /admin/templates, JSON for /admin/configuration/workspace).
//
// Pulled in as ESM modules from esm.sh so the rest of the app stays JS-bundler-
// free (per .design/milestones.md → P2.2). Versions are pinned to keep cache
// hits stable and to avoid surprise behaviour drifts.
//
// Every URL carries the same `?deps=` query so esm.sh resolves the shared
// CodeMirror packages to a single canonical instance. Without this, each
// package fetches its own latest-matching @codemirror/state and the
// instanceof checks inside CodeMirror's extension system break with the
// "Unrecognized extension value in extension set" error.
//
// The exported functions are ID-keyed so Blazor's IJSObjectReference can
// stay a plain integer rather than wrapping the EditorView itself: simpler
// lifecycle, no DotNet.createJSObjectReference plumbing.

import { EditorView, lineNumbers, highlightActiveLineGutter, highlightSpecialChars,
    drawSelection, dropCursor, rectangularSelection, crosshairCursor,
    highlightActiveLine, keymap, Decoration }
    from "https://esm.sh/@codemirror/view@6.34.1?deps=@codemirror/state@6.4.1";
import { EditorState, Compartment, RangeSetBuilder }
    from "https://esm.sh/@codemirror/state@6.4.1";
import { defaultKeymap, history, historyKeymap, indentWithTab }
    from "https://esm.sh/@codemirror/commands@6.7.1?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1";
import { syntaxHighlighting, defaultHighlightStyle, indentOnInput, bracketMatching,
    foldGutter, foldKeymap, StreamLanguage }
    from "https://esm.sh/@codemirror/language@6.10.6?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1";
import { search, searchKeymap, highlightSelectionMatches }
    from "https://esm.sh/@codemirror/search@6.5.7?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1";
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap }
    from "https://esm.sh/@codemirror/autocomplete@6.18.3?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1,@codemirror/language@6.10.6";
import { lintKeymap, lintGutter, setDiagnostics }
    from "https://esm.sh/@codemirror/lint@6.8.4?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1";
import { toml }
    from "https://esm.sh/@codemirror/legacy-modes@6.4.1/mode/toml?deps=@codemirror/state@6.4.1,@codemirror/language@6.10.6";
import { json as jsonMode }
    from "https://esm.sh/@codemirror/lang-json@6.0.1?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1,@codemirror/language@6.10.6";
import { oneDark }
    from "https://esm.sh/@codemirror/theme-one-dark@6.1.2?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1,@codemirror/language@6.10.6";

// Lightweight AL StreamParser. Not a full AL grammar — recognises keywords,
// strings, double-quoted identifiers (AL allows spaces inside `"..."`), comments
// (line + block), and numeric literals. Same depth as the Prism `al` grammar;
// shipped inline so we don't pull a third-party AL grammar package.
const AL_KEYWORDS = new Set([
    "begin", "end", "if", "then", "else", "do", "while", "repeat", "until",
    "for", "to", "downto", "foreach", "in", "case", "of", "exit",
    "procedure", "trigger", "local", "internal", "protected", "var",
    "with", "namespace", "using", "interface", "implements", "extends",
    "codeunit", "table", "tableextension", "page", "pageextension",
    "pagecustomization", "report", "reportextension", "xmlport", "query",
    "enum", "enumextension", "permissionset", "permissionsetextension",
    "profile", "controladdin", "dotnet",
    "and", "or", "not", "xor", "div", "mod", "true", "false",
    "record", "temporary", "label", "text", "integer", "biginteger",
    "decimal", "boolean", "char", "date", "time", "datetime", "duration",
    "guid", "code", "blob", "media", "mediaset", "option", "list", "dictionary",
    "array", "variant", "instream", "outstream",
    "rec", "xrec", "currfieldno", "currpage", "currreport",
    "raises", "obsolete", "subscribers", "subscriber",
]);

const alParser = {
    startState() { return { inBlockComment: false }; },
    token(stream, state) {
        if (state.inBlockComment) {
            while (!stream.eol()) {
                if (stream.match("*/")) {
                    state.inBlockComment = false;
                    return "comment";
                }
                stream.next();
            }
            return "comment";
        }
        if (stream.eatSpace()) return null;
        if (stream.match("//")) {
            stream.skipToEnd();
            return "comment";
        }
        if (stream.match("/*")) {
            state.inBlockComment = true;
            return "comment";
        }
        // Double-quoted AL identifier ("Sales-Post").
        if (stream.peek() === '"') {
            stream.next();
            while (!stream.eol()) {
                const ch = stream.next();
                if (ch === '"') break;
            }
            return "variableName";
        }
        // Single-quoted string literal.
        if (stream.peek() === "'") {
            stream.next();
            while (!stream.eol()) {
                const ch = stream.next();
                if (ch === "'") {
                    if (stream.peek() === "'") {
                        stream.next(); // escaped quote
                        continue;
                    }
                    break;
                }
            }
            return "string";
        }
        if (stream.match(/^\d+(\.\d+)?/)) return "number";
        if (stream.match(/^[A-Za-z_][A-Za-z0-9_]*/)) {
            const word = stream.current().toLowerCase();
            if (AL_KEYWORDS.has(word)) return "keyword";
            return null;
        }
        stream.next();
        return null;
    },
};

let nextId = 1;
const editors = new Map();

// Browser-level guard against losing edits to a full reload, tab close, or a
// browser back that exits the SPA. In-app navigation goes through Blazor's
// LocationChangingHandler instead — see the callers under Components/Pages.
let beforeUnloadAttached = false;
function beforeUnloadHandler(e) {
    e.preventDefault();
    // Modern browsers ignore the message text and show their own copy, but
    // Chrome still requires a non-empty returnValue to actually trigger the
    // dialog.
    e.returnValue = "";
    return "";
}
function syncBeforeUnload() {
    const anyDirty = [...editors.values()].some(rec => rec.dirty);
    if (anyDirty && !beforeUnloadAttached) {
        window.addEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = true;
    } else if (!anyDirty && beforeUnloadAttached) {
        window.removeEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = false;
    }
}

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

// Returns the CodeMirror language extension for the requested mode. Unknown
// modes fall back to plain text so the editor still renders rather than
// throwing inside the EditorState constructor.
function languageExtensionFor(language) {
    switch (language) {
        case "toml": return StreamLanguage.define(toml);
        case "json": return jsonMode();
        case "al": return StreamLanguage.define(alParser);
        default: return [];
    }
}

function buildExtensions(themeCompartment, dirtyListener, language) {
    return [
        dirtyListener,
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
        languageExtensionFor(language),
        lintGutter(),
        themeCompartment.of(themeExtensions()),
    ];
}

export function mount(container, initialValue, language) {
    if (!container) return 0;
    const id = nextId++;
    const themeCompartment = new Compartment();
    const initial = initialValue ?? "";
    const lang = typeof language === "string" ? language : "toml";

    // Re-evaluate dirtiness on every doc change so the navigate-away guard
    // (both browser-level beforeunload and Blazor's LocationChangingHandler)
    // stays in sync without polling from C#.
    const dirtyListener = EditorView.updateListener.of((update) => {
        if (!update.docChanged) return;
        const rec = editors.get(id);
        if (!rec) return;
        const next = view.state.doc.toString() !== rec.pristine;
        if (next !== rec.dirty) {
            rec.dirty = next;
            syncBeforeUnload();
        }
    });

    const view = new EditorView({
        parent: container,
        state: EditorState.create({
            doc: initial,
            extensions: buildExtensions(themeCompartment, dirtyListener, lang),
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
        pristine: initial,
        dirty: false,
        dispose: () => {
            themeObserver.disconnect();
            mql?.removeEventListener?.("change", reconfigureTheme);
            view.destroy();
        },
    });
    return id;
}

// Read-only viewer used by Object Explorer. No history, no autocomplete, no
// dirty tracking — just the language colouriser, line numbers, search,
// folding, and an EditorView.editable.of(false) so cursor placement still
// works but typing is rejected.
// options:
//   lineDecorations: { [1-basedLineNumber]: cssClass }  — full-line backgrounds (diff view)
//   declarations:    [{ line, columnStart, columnEnd, symbolId, kind, name }]
//                     ranges that get a click affordance + right-click "Find references"
//   resolvables:     [{ line, columnStart, columnEnd }] — extra ranges that
//                     show the resolvable-token underline (object references,
//                     procedure calls). Cosmetic only; the actual Go to
//                     definition still runs through the server.
//   dotNetRef:        Blazor DotNetObjectReference; callbacks fire `OnFindReferences(symbolId)`
//   scrollToLine:     1-based line to scroll to + flash after mount (deep-link from references)
export function mountReadOnly(container, value, language, options) {
    if (!container) return 0;
    const id = nextId++;
    const themeCompartment = new Compartment();
    const initial = value ?? "";
    const lang = typeof language === "string" ? language : "al";
    const opts = options ?? {};

    const decorationExtensions = buildLineDecorationExtensions(opts.lineDecorations);
    const declarationExtensions = buildDeclarationDecorationExtensions(opts.declarations);
    const resolvableExtensions = buildResolvableDecorationExtensions(opts.resolvables);

    const view = new EditorView({
        parent: container,
        state: EditorState.create({
            doc: initial,
            extensions: [
                EditorView.editable.of(false),
                EditorState.readOnly.of(true),
                lineNumbers(),
                highlightSpecialChars(),
                foldGutter(),
                drawSelection(),
                EditorState.allowMultipleSelections.of(true),
                syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
                highlightActiveLine(),
                highlightSelectionMatches(),
                // Ctrl/Cmd-F brings up CodeMirror's search panel. `search()`
                // registers the panel state; searchKeymap binds the key.
                search({ top: true }),
                keymap.of([...defaultKeymap, ...searchKeymap, ...foldKeymap]),
                languageExtensionFor(lang),
                ...decorationExtensions,
                ...declarationExtensions,
                ...resolvableExtensions,
                themeCompartment.of(themeExtensions()),
            ],
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

    // Context-menu wiring for click-to-find. Listens for right-clicks on the
    // editor DOM; if the click hits a declaration's name range, surface a
    // small floating menu and stop the browser's default menu. Anything
    // outside a declaration falls through (browser menu kept).
    const declarations = Array.isArray(opts.declarations) ? opts.declarations : [];
    let openMenu = null;
    const closeMenu = () => {
        if (openMenu) {
            openMenu.remove();
            openMenu = null;
        }
    };

    // Right-click anywhere offers "Go to definition" when a callback is wired;
    // additionally offers "Find references" when the click lands inside a
    // declaration name range. Click outside either menu item to dismiss.
    const onContextMenu = (event) => {
        if (!opts.dotNetRef) return;
        const pos = view.posAtCoords({ x: event.clientX, y: event.clientY });
        if (pos === null) return;
        const line = view.state.doc.lineAt(pos);
        const colInLine = pos - line.from + 1; // 1-based to match C# columns
        const onDeclaration = declarations.find(d =>
            d.line === line.number
            && colInLine >= d.columnStart
            && colInLine <= d.columnEnd);

        const items = [];
        if (onDeclaration) {
            items.push({
                label: "Find references",
                action: () => opts.dotNetRef.invokeMethodAsync(
                    "OnFindReferences", onDeclaration.symbolId),
            });
        }
        // Disable "Go to definition" when the click lands on the declaration
        // name itself — the target would be the click site, which causes the
        // viewer to navigate to its current URL and break re-mounting state.
        items.push({
            label: "Go to definition",
            disabled: Boolean(onDeclaration),
            action: () => opts.dotNetRef.invokeMethodAsync(
                "OnGoToDefinition", line.number, colInLine),
        });
        // "Find in this file" is the only gesture for variables / fields that
        // don't have a symbol-table entry. Always offered so users have a
        // reliable way to scan within a long file.
        items.push({
            label: "Find in this file",
            action: () => opts.dotNetRef.invokeMethodAsync(
                "OnFindInFile", line.number, colInLine),
        });

        event.preventDefault();
        closeMenu();
        openMenu = renderMenu(event.clientX, event.clientY, items);
    };
    container.addEventListener("contextmenu", onContextMenu);
    document.addEventListener("click", closeMenu);
    document.addEventListener("scroll", closeMenu, true);

    // Cmd/Ctrl-click anywhere in the editor fires Go to definition.
    // Holding the modifier (without clicking) toggles a class on the editor
    // body so identifier-shaped tokens get a hover affordance — gives users
    // the IDE-style "what's clickable" feedback even though we can't
    // pre-resolve which tokens have a definition without a full parse.
    const onClickForDefinition = (event) => {
        if (!event.metaKey && !event.ctrlKey) return;
        if (!opts.dotNetRef) return;
        const pos = view.posAtCoords({ x: event.clientX, y: event.clientY });
        if (pos === null) return;
        const line = view.state.doc.lineAt(pos);
        const colInLine = pos - line.from + 1;
        // Cmd/Ctrl-click on the declaration name itself would resolve to the
        // current location — same URL, same line — and break the viewer
        // (see right-click handler above). Swallow the click instead.
        const onDeclaration = declarations.find(d =>
            d.line === line.number
            && colInLine >= d.columnStart
            && colInLine <= d.columnEnd);
        if (onDeclaration) {
            event.preventDefault();
            return;
        }
        event.preventDefault();
        opts.dotNetRef.invokeMethodAsync("OnGoToDefinition", line.number, colInLine)
            .catch(err => console.warn("Go to definition callback failed:", err));
    };
    container.addEventListener("click", onClickForDefinition);

    const updateModifierClass = (event) => {
        if (event.metaKey || event.ctrlKey) {
            container.classList.add("cm-modifier-down");
        } else {
            container.classList.remove("cm-modifier-down");
        }
    };
    container.addEventListener("mousemove", updateModifierClass);
    container.addEventListener("keydown", updateModifierClass);
    container.addEventListener("keyup", updateModifierClass);
    container.addEventListener("mouseleave", () => container.classList.remove("cm-modifier-down"));
    // Modifier release outside the editor still needs to clear the class.
    window.addEventListener("blur", () => container.classList.remove("cm-modifier-down"));

    editors.set(id, {
        view,
        pristine: initial,
        dirty: false,
        dispose: () => {
            container.removeEventListener("contextmenu", onContextMenu);
            container.removeEventListener("click", onClickForDefinition);
            container.removeEventListener("mousemove", updateModifierClass);
            container.removeEventListener("keydown", updateModifierClass);
            container.removeEventListener("keyup", updateModifierClass);
            document.removeEventListener("click", closeMenu);
            document.removeEventListener("scroll", closeMenu, true);
            container.classList.remove("cm-modifier-down");
            closeMenu();
            themeObserver.disconnect();
            mql?.removeEventListener?.("change", reconfigureTheme);
            view.destroy();
        },
    });

    // Deferred scroll-and-flash: wait one rAF so CodeMirror has laid out
    // and our height measurements are correct before we ask it to scroll.
    if (typeof opts.scrollToLine === "number" && opts.scrollToLine >= 1) {
        requestAnimationFrame(() => scrollToLine(id, opts.scrollToLine, /*flash*/ true));
    }

    return id;
}

// Public: scroll the editor to a 1-based line number, with an optional
// short fade-out highlight so the eye lands in the right place.
//
// Two-pass scroll: CM6 estimates heights for unmeasured lines, so the
// first scroll lands roughly in place (rendering new lines as a side
// effect), and the second corrects against CM's now-accurate height
// map. Both passes set view.scrollDOM.scrollTop directly rather than
// dispatching EditorView.scrollIntoView — the effect path through CM's
// transaction system was leaving the viewport in inconsistent states
// when triggered from outside a CM-initiated update.
export function scrollToLine(id, lineNumber, flash) {
    const e = editors.get(id);
    if (!e) return;
    const view = e.view;
    if (!Number.isInteger(lineNumber) || lineNumber < 1) return;
    const totalLines = view.state.doc.lines;
    const safeLine = Math.min(lineNumber, totalLines);

    const doScroll = () => {
        const line = view.state.doc.line(safeLine);
        const block = view.lineBlockAt(line.from);
        const scroller = view.scrollDOM;
        const scrollMax = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
        const target = block.top - scroller.clientHeight / 2 + block.height / 2;
        scroller.scrollTop = Math.max(0, Math.min(scrollMax, target));
    };

    requestAnimationFrame(() => {
        doScroll();
        requestAnimationFrame(() => {
            doScroll();
            if (!flash) return;
            requestAnimationFrame(() => {
                const line = view.state.doc.line(safeLine);
                const dom = view.domAtPos(line.from);
                let lineEl = dom?.node instanceof Element ? dom.node : dom?.node?.parentElement;
                while (lineEl && !(lineEl.classList && lineEl.classList.contains("cm-line"))) {
                    lineEl = lineEl.parentElement;
                }
                if (!lineEl) return;
                // Adding a class that's already present doesn't restart its
                // CSS animation. Remove → force a reflow → re-add so
                // repeated clicks on the same reference re-flash the line.
                lineEl.classList.remove("cm-line--flash");
                // eslint-disable-next-line no-unused-expressions
                lineEl.offsetWidth;
                lineEl.classList.add("cm-line--flash");
                setTimeout(() => lineEl.classList.remove("cm-line--flash"), 1500);
            });
        });
    });
}

// Builds a CodeMirror extension that wraps each declaration name range
// with a `cm-symbol-decl` class so users see what's clickable.
function buildDeclarationDecorationExtensions(declarations) {
    if (!Array.isArray(declarations) || declarations.length === 0) return [];
    return [EditorView.decorations.of((view) => {
        const builder = new RangeSetBuilder();
        for (const decl of declarations) {
            const lineNo = decl.line;
            if (!Number.isInteger(lineNo) || lineNo < 1 || lineNo > view.state.doc.lines) continue;
            const line = view.state.doc.line(lineNo);
            const from = line.from + Math.max(0, (decl.columnStart ?? 1) - 1);
            const toCol = decl.columnEnd ?? (decl.columnStart ?? 1);
            const to = Math.min(line.to, line.from + Math.max(from - line.from, toCol - 1));
            if (to <= from) continue;
            builder.add(from, to, Decoration.mark({
                class: "cm-symbol-decl",
                attributes: { "data-symbol-id": String(decl.symbolId) },
            }));
        }
        return builder.finish();
    })];
}

// Decorates every range the server identified as a "resolvable" reference
// (object names, procedure call sites, etc.) with `cm-symbol-ref` so users
// get the same dotted underline they see on declarations. Ranges are
// pre-sorted server-side; the RangeSetBuilder still requires ascending order.
function buildResolvableDecorationExtensions(resolvables) {
    if (!Array.isArray(resolvables) || resolvables.length === 0) return [];
    // Defensive sort: ranges must be added in order of `from` to the builder,
    // and pre-sorted input is cheap to re-verify here.
    const sorted = resolvables
        .filter(r => Number.isInteger(r.line) && r.line >= 1)
        .slice()
        .sort((a, b) => (a.line - b.line) || ((a.columnStart ?? 1) - (b.columnStart ?? 1)));
    return [EditorView.decorations.of((view) => {
        const builder = new RangeSetBuilder();
        const docLines = view.state.doc.lines;
        for (const ref of sorted) {
            if (ref.line > docLines) continue;
            const line = view.state.doc.line(ref.line);
            const from = line.from + Math.max(0, (ref.columnStart ?? 1) - 1);
            const toCol = ref.columnEnd ?? (ref.columnStart ?? 1);
            const to = Math.min(line.to, line.from + Math.max(from - line.from, toCol - 1));
            if (to <= from) continue;
            builder.add(from, to, Decoration.mark({ class: "cm-symbol-ref" }));
        }
        return builder.finish();
    })];
}

// Renders a small floating context menu at (x, y). Each item is
// `{ label, action }`; `action` returns the promise from a DotNet
// invokeMethodAsync call. The menu removes itself when an item is
// clicked or when the document-level click handler in mountReadOnly
// closes it.
function renderMenu(x, y, items) {
    const menu = document.createElement("div");
    menu.className = "cm-symbol-menu";
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;

    for (const item of items) {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "cm-symbol-menu__item";
        btn.textContent = item.label;
        if (item.disabled) {
            btn.disabled = true;
            btn.classList.add("cm-symbol-menu__item--disabled");
        } else {
            btn.addEventListener("click", () => {
                menu.remove();
                try {
                    const result = item.action();
                    if (result && typeof result.catch === "function") {
                        result.catch(err => console.warn(`${item.label} failed:`, err));
                    }
                } catch (err) {
                    console.warn(`${item.label} threw:`, err);
                }
            });
        }
        menu.appendChild(btn);
    }

    document.body.appendChild(menu);
    return menu;
}

// Translates the C# {lineNumber: cssClass} map into a CodeMirror extension
// that decorates whole lines. Used by the Object Explorer diff view to mark
// added / removed / modified lines on each side.
function buildLineDecorationExtensions(lineDecorations) {
    if (!lineDecorations || typeof lineDecorations !== "object") return [];
    return [EditorView.decorations.of((view) => {
        const builder = new RangeSetBuilder();
        for (let i = 1; i <= view.state.doc.lines; i++) {
            const cls = lineDecorations[i];
            if (!cls) continue;
            const line = view.state.doc.line(i);
            builder.add(line.from, line.from, Decoration.line({ class: cls }));
        }
        return builder.finish();
    })];
}

export function isDirty(id) {
    return editors.get(id)?.dirty ?? false;
}

// Called after a successful save (or after the editor is intentionally
// repopulated from the server) so the next edit starts a fresh dirty cycle.
export function markPristine(id) {
    const rec = editors.get(id);
    if (!rec) return;
    rec.pristine = rec.view.state.doc.toString();
    if (rec.dirty) {
        rec.dirty = false;
        syncBeforeUnload();
    }
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
    // setValue is server-driven (mode switch, post-save refresh) so the new
    // text is the new pristine baseline. Without resetting, the dirty flag
    // would stay sticky and the navigation guard would warn falsely.
    e.pristine = next;
    if (e.dirty) {
        e.dirty = false;
        syncBeforeUnload();
    }
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
    syncBeforeUnload();
}
