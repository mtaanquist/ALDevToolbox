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
    highlightActiveLine, keymap, Decoration, WidgetType, showPanel, gutter, GutterMarker }
    from "https://esm.sh/@codemirror/view@6.34.1?deps=@codemirror/state@6.4.1";
import { EditorState, Compartment, RangeSetBuilder, StateField, StateEffect }
    from "https://esm.sh/@codemirror/state@6.4.1";
import { defaultKeymap, history, historyKeymap, indentWithTab }
    from "https://esm.sh/@codemirror/commands@6.7.1?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1";
import { syntaxHighlighting, defaultHighlightStyle, indentOnInput, bracketMatching,
    foldGutter, foldKeymap, StreamLanguage }
    from "https://esm.sh/@codemirror/language@6.10.6?deps=@codemirror/state@6.4.1,@codemirror/view@6.34.1";
import { search, searchKeymap, highlightSelectionMatches, openSearchPanel }
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
// (line + block), and numeric literals. Categorisation cross-checked against
// Microsoft's AL TextMate grammar (github.com/microsoft/AL/blob/master/grammar/
// alsyntax.tmlanguage) so built-in types render distinctly from control
// keywords, the same way the AL VS Code extension shows them.
const AL_KEYWORDS = new Set([
    // Control flow.
    "begin", "end", "if", "then", "else", "do", "while", "repeat", "until",
    "for", "to", "downto", "foreach", "in", "case", "of", "exit", "break",
    // Declaration & scope.
    "procedure", "trigger", "local", "internal", "protected", "var",
    "with", "namespace", "using", "interface", "implements", "extends",
    "implements", "raises", "obsolete", "subscribers", "subscriber",
    "temporary", "rec", "xrec", "currfieldno", "currpage", "currreport",
    // Object-type keywords (typed-reference introducers — also in
    // AL_OBJECT_KEYWORDS below so the following identifier is coloured).
    "codeunit", "table", "tableextension", "page", "pageextension",
    "pagecustomization", "report", "reportextension", "xmlport", "query",
    "enum", "enumextension", "permissionset", "permissionsetextension",
    "profile", "controladdin", "dotnet",
    // Operator keywords.
    "and", "or", "not", "xor", "div", "mod",
    // Boolean / null literals.
    "true", "false",
    // Metadata / property keywords (from the MS grammar). Not exhaustive,
    // but covers what's surprising-when-uncoloured in real BaseApp code.
    "where", "ascending", "descending", "filter", "const", "average",
    "count", "exist", "field", "min", "max", "sum",
    "add", "addfirst", "addlast", "addbefore", "addafter",
    "modify", "movebefore", "moveafter", "customizes",
    "action", "actions", "fields", "keys", "schema", "values",
    "elements", "textelement", "tableelement", "fieldattribute",
    "textattribute", "requestpage",
]);

// Built-in AL types. Distinct from AL_KEYWORDS so they get `typeName`
// styling — matches the AL VS Code extension's `keyword.other.builtintypes.al`
// scope. Drawn from the MS grammar and the current AL methods reference.
const AL_BUILTIN_TYPES = new Set([
    // Primitives.
    "boolean", "byte", "char", "code", "date", "dateformula", "datetime",
    "decimal", "duration", "guid", "integer", "biginteger", "label", "option",
    "text", "time", "variant",
    // Streams / files.
    "instream", "outstream", "file",
    // Reference types and containers.
    "array", "list", "dictionary", "blob", "media", "mediaset",
    "recordid", "recordref", "fieldref", "keyref",
    // Modern (HTTP / JSON / XML).
    "httpclient", "httpcontent", "httpheaders", "httprequestmessage",
    "httpresponsemessage",
    "jsonarray", "jsonobject", "jsontoken", "jsonvalue",
    "xmlattribute", "xmlattributecollection", "xmlcdata", "xmlcomment",
    "xmldeclaration", "xmldocument", "xmldocumenttype", "xmlelement",
    "xmlnamespacemanager", "xmlnamespacescope", "xmlnametable", "xmlnode",
    "xmlnodelist", "xmlprocessinginstruction", "xmlreadoptions", "xmltext",
    "xmlwriteoptions",
    // UI / runtime.
    "action", "dialog", "errorinfo", "filterpagebuilder", "notification",
    "page", "report", "session", "sessionsettings", "textbuilder",
    "textconst", "textencoding", "verbosity", "version", "testpage",
    "clienttype", "tableconnectiontype",
]);

// Keywords whose following identifier is an *object name* — colour the
// next bare identifier (after any object-ID number) the same way we colour
// quoted AL identifiers, so `table 5721 Purchasing` reads the same as
// `table 36 "Sales Header"`.
const AL_OBJECT_KEYWORDS = new Set([
    "codeunit", "table", "tableextension", "page", "pageextension",
    "pagecustomization", "report", "reportextension", "xmlport", "query",
    "enum", "enumextension", "permissionset", "permissionsetextension",
    "profile", "controladdin", "record",
    "requestpage", "testpage", "testpart", "testrequestpage", "interface",
    "extends", "tabledata",
]);

// Keywords whose following identifier is the *name of a procedure or
// trigger being declared* — gives the name a distinct colour (CodeMirror
// maps `def` to a definition tag, typically bold/coloured).
const AL_DEFINITION_KEYWORDS = new Set([
    "procedure", "trigger",
]);

const alParser = {
    startState() {
        return {
            inBlockComment: false,
            expectObjectName: false,
            expectDefinitionName: false,
        };
    },
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
            // A quoted name in either expected-name slot is still the
            // declared name — colour as definition if pending, otherwise
            // a regular AL identifier.
            const tok = state.expectDefinitionName ? "def" : "variableName";
            state.expectObjectName = false;
            state.expectDefinitionName = false;
            return tok;
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
            state.expectObjectName = false;
            state.expectDefinitionName = false;
            return "string";
        }
        if (stream.match(/^\d+(\.\d+)?/)) {
            // A numeric literal between an object keyword and the name
            // (`table 5721 Purchasing`) is fine — keep the expectation
            // alive so the next identifier still gets the variableName
            // colour. Other numerics clear it.
            return "number";
        }
        if (stream.match(/^[A-Za-z_][A-Za-z0-9_]*/)) {
            const word = stream.current().toLowerCase();
            // Built-in types come first so `Integer`, `Text`, `Boolean`,
            // `HttpClient`, etc. render as types regardless of whether
            // they appear in a declaration or a type-cast position.
            if (AL_BUILTIN_TYPES.has(word)) {
                state.expectObjectName = false;
                state.expectDefinitionName = false;
                return "typeName";
            }
            if (AL_KEYWORDS.has(word)) {
                state.expectObjectName = AL_OBJECT_KEYWORDS.has(word);
                state.expectDefinitionName = AL_DEFINITION_KEYWORDS.has(word);
                return "keyword";
            }
            if (state.expectDefinitionName) {
                // First identifier after `procedure` / `trigger` is the
                // name being declared. `def` maps to CodeMirror's
                // definition tag — typically bold or accent-coloured.
                state.expectDefinitionName = false;
                state.expectObjectName = false;
                return "def";
            }
            if (state.expectObjectName) {
                state.expectObjectName = false;
                return "variableName";
            }
            return null;
        }
        // Any other character — punctuation, operator — drops the
        // object-name expectation. We hit this for `{`, `:`, `=`, etc.,
        // any of which means the declaration has moved past its name.
        state.expectObjectName = false;
        state.expectDefinitionName = false;
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
//   fillers:         [{ before, size }] — blank alignment gaps (compare view).
//                     Inserts `size` line-heights of empty space before source
//                     line `before` (or after the last line when
//                     `before > doc.lines`) so the two compare panes stay
//                     vertically aligned KDiff3-style. Real text and gutter
//                     line numbers are untouched — a filler is empty space.
//   declarations:    [{ line, columnStart, columnEnd, symbolId, kind, name }]
//                     ranges that get a click affordance + right-click "Find references"
//   resolvables:     [{ line, columnStart, columnEnd }] — extra ranges that
//                     show the resolvable-token underline (object references,
//                     procedure calls). Cosmetic only; the actual Go to
//                     definition still runs through the server.
//   procedures:      [{ startLine, endLine?, name, kind }] — procedure-like
//                     symbol ranges. Drives the status bar's "in CheckDates
//                     (line 13)" suffix, mapping the absolute editor line to
//                     BC's procedure-relative stack-trace numbering (the
//                     `procedure` declaration line counts as line 0).
//                     Only used when `statusBar: true`.
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
    // Diff change-bar gutter, only when there are diff line decorations to
    // mark (i.e. the compare page). The single-file viewer passes none, so it
    // gets no extra gutter.
    const diffGutterExtensions = buildDiffGutterExtensions(opts.lineDecorations);
    // Alignment fillers, compare page only. Blank block widgets that pad the
    // shorter side so matching lines line up across panes (single-file viewer
    // passes none, so it gets no fillers).
    const fillerExtensions = buildFillerDecorationExtensions(opts.fillers);
    const declarationExtensions = buildDeclarationDecorationExtensions(opts.declarations);
    const resolvableExtensions = buildResolvableDecorationExtensions(opts.resolvables);
    // Opt-in status bar: only the source-file viewer asks for it today.
    // The diff viewer and the admin TOML/JSON editors keep their existing
    // chrome unchanged.
    const statusBarExtensions = opts.statusBar ? [buildStatusBarExtension(opts.procedures)] : [];
    // Sticky "current line" highlight survives CodeMirror's row
    // virtualisation because the decoration lives in editor state rather
    // than on a DOM node. scrollToLine() dispatches setCurrentLineEffect
    // to set/clear it.
    const currentLineExtensions = [currentLineField, currentLineTheme];

    const view = new EditorView({
        parent: container,
        state: EditorState.create({
            doc: initial,
            extensions: [
                EditorView.editable.of(false),
                EditorState.readOnly.of(true),
                // Disable browser spellcheck on the editor content.
                // AL identifiers ("Sell-to Customer Name" etc.) light
                // up with red squiggles that are easy to confuse with
                // our resolvable / declaration dotted underlines.
                EditorView.contentAttributes.of({ spellcheck: "false" }),
                lineNumbers(),
                ...diffGutterExtensions,
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
                ...fillerExtensions,
                ...declarationExtensions,
                ...resolvableExtensions,
                ...statusBarExtensions,
                ...currentLineExtensions,
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
            // Click landed on a declaration name range — we already know the
            // symbol id, so the existing single-arg callback is fine.
            // Two ID spaces: object headers go through OnFindReferences
            // (oe_module_objects.Id → /from-symbol/); sub-symbols
            // (procedure / field / trigger / event) go through
            // OnFindMemberReferences (oe_module_symbols.Id →
            // /from-member-symbol/). isMemberSymbol decides the route.
            const callback = onDeclaration.isMemberSymbol
                ? "OnFindMemberReferences"
                : "OnFindReferences";
            items.push({
                label: "Find references",
                action: () => opts.dotNetRef.invokeMethodAsync(
                    callback, onDeclaration.symbolId),
            });
            // "Find system references" — built-in/system method calls (Insert,
            // Modify, SetRange, …) on this object. Object headers only; system
            // calls target a whole object, not a member. symbolId here is the
            // oe_module_objects.Id. See #279/#291.
            if (!onDeclaration.isMemberSymbol) {
                items.push({
                    label: "Find system references",
                    action: () => opts.dotNetRef.invokeMethodAsync(
                        "OnFindSystemReferences", onDeclaration.symbolId),
                });
            }
        } else {
            // Off-declaration click: the host decides whether the word
            // under the cursor resolves to a symbol. The two-arg variant
            // lets the host run a positional lookup server-side and fall
            // back to "no references" UI if nothing matches.
            items.push({
                label: "Find references",
                action: () => opts.dotNetRef.invokeMethodAsync(
                    "OnFindReferencesAt", line.number, colInLine),
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
function resetAncestorScrollLeft(start) {
    let node = start;
    while (node && node !== document.scrollingElement) {
        if (node.scrollLeft) node.scrollLeft = 0;
        node = node.parentElement;
    }
    if (document.scrollingElement) document.scrollingElement.scrollLeft = 0;
}

export function scrollToLine(id, lineNumber, flash) {
    const e = editors.get(id);
    if (!e) return;
    const view = e.view;
    if (!Number.isInteger(lineNumber) || lineNumber < 1) return;
    const totalLines = view.state.doc.lines;
    const safeLine = Math.min(lineNumber, totalLines);

    const findLineEl = () => {
        const line = view.state.doc.line(safeLine);
        const dom = view.domAtPos(line.from);
        let lineEl = dom?.node instanceof Element ? dom.node : dom?.node?.parentElement;
        while (lineEl && !(lineEl.classList && lineEl.classList.contains("cm-line"))) {
            lineEl = lineEl.parentElement;
        }
        return lineEl;
    };

    const doScroll = () => {
        const line = view.state.doc.line(safeLine);
        const block = view.lineBlockAt(line.from);
        const scroller = view.scrollDOM;
        const scrollMax = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
        if (scrollMax > 0) {
            // Bounded editor (default mount) — scroll the editor's own
            // scroller. Direct scrollTop avoids the inconsistent viewport
            // state we used to see going through EditorView.scrollIntoView.
            const target = block.top - scroller.clientHeight / 2 + block.height / 2;
            scroller.scrollTop = Math.max(0, Math.min(scrollMax, target));
            scroller.scrollLeft = 0;
        } else {
            // Fluid mount (`Fluid="true"`): the editor's scroller is
            // overflow:visible, so an outer container (.content) scrolls
            // the page. Use inline:"nearest" so scrollIntoView only
            // moves vertically — `inline:"start"` aligns the cm-line's
            // left edge with the scrollport's left edge, but since
            // cm-line begins AFTER the gutter, that scrolls the page
            // right by the gutter width and chops off the start of
            // shorter lines. The follow-up resetAncestorScrollLeft
            // call then clears any residual horizontal scroll the
            // previous jump (or a long line elsewhere) left behind.
            const lineEl = findLineEl();
            if (lineEl) {
                lineEl.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" });
            } else {
                view.dispatch({
                    effects: EditorView.scrollIntoView(line.from, { y: "center" }),
                });
            }
            resetAncestorScrollLeft(scroller);
        }
    };

    // Sticky-highlight the destination line via the state field. Doing
    // this before the scroll so the decoration is already in place when
    // CodeMirror paints the viewport — no first-paint flicker, and it
    // survives the user scrolling the row off-screen and back.
    if (flash) {
        view.dispatch({ effects: setCurrentLineEffect.of(safeLine) });
    }

    requestAnimationFrame(() => {
        doScroll();
        requestAnimationFrame(() => {
            doScroll();
        });
    });
}

/// Clear the sticky line highlight. The viewer doesn't currently expose
/// this beyond the file-id changing (each mount starts with a fresh
/// state), but external pages can call it via the editor id when needed.
export function clearCurrentLine(id) {
    const e = editors.get(id);
    if (!e) return;
    e.view.dispatch({ effects: setCurrentLineEffect.of(0) });
}

// Builds a CodeMirror extension that wraps each declaration name range
// with a `cm-symbol-decl` class so users see what's clickable.
function buildDeclarationDecorationExtensions(declarations) {
    if (!Array.isArray(declarations) || declarations.length === 0) return [];
    // Defensive sort: the server appends object declarations before member
    // symbols, so a file shipping multiple objects (e.g. an extension with
    // several objects in one .al) yields ranges that aren't ascending by
    // `from`. RangeSetBuilder.add requires ascending order, so normalise here
    // the same way buildResolvableDecorationExtensions does.
    const sorted = declarations
        .filter(d => Number.isInteger(d.line) && d.line >= 1)
        .slice()
        .sort((a, b) => (a.line - b.line) || ((a.columnStart ?? 1) - (b.columnStart ?? 1)));
    return [EditorView.decorations.of((view) => {
        const builder = new RangeSetBuilder();
        for (const decl of sorted) {
            const lineNo = decl.line;
            if (lineNo > view.state.doc.lines) continue;
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

// A block widget that renders `size` line-heights of empty space — the
// KDiff3-style alignment gap on the compare page. Height is computed from the
// editor's measured line height so it tracks the font/zoom, and `eq` lets
// CodeMirror skip re-rendering an unchanged filler.
class FillerWidget extends WidgetType {
    constructor(size) {
        super();
        this._size = size;
    }
    eq(other) {
        return other instanceof FillerWidget && other._size === this._size;
    }
    toDOM(view) {
        const el = document.createElement("div");
        el.className = "cm-diff-filler";
        el.style.height = (view.defaultLineHeight * this._size) + "px";
        el.setAttribute("aria-hidden", "true");
        return el;
    }
    // No DOM events to ignore inside a passive spacer.
    ignoreEvent() {
        return false;
    }
}

// Builds the block-widget filler set for the given editor state. `before` is a
// 1-based source line the gap precedes; `before > doc.lines` means a trailing
// gap (the opposite pane appended lines past this one's end), anchored after the
// last line. The serializer emits gaps in ascending line order, which is what
// RangeSetBuilder requires.
function buildFillerSet(state, fillers) {
    const builder = new RangeSetBuilder();
    const doc = state.doc;
    for (const f of fillers) {
        const size = Number(f?.size);
        const before = Number(f?.before);
        if (!Number.isFinite(size) || size < 1) continue;
        if (!Number.isFinite(before) || before < 1) continue;
        const widget = Decoration.widget({
            widget: new FillerWidget(size),
            block: true,
            // side -1 places the spacer above the anchored line; +1 below.
            side: before > doc.lines ? 1 : -1,
        });
        const pos = before > doc.lines
            ? doc.line(doc.lines).to
            : doc.line(before).from;
        builder.add(pos, pos, widget);
    }
    return builder.finish();
}

// Translates the C# [{before, size}] filler list into block-widget decorations
// that pad each compare pane so matching lines align across the two editors.
// Block widgets affect vertical layout, so CodeMirror only honours them from a
// static decoration source — a StateField here, NOT the view-function form of
// EditorView.decorations.of (which silently drops block decorations). The doc
// is read-only on the compare page, so the set is computed once at create.
function buildFillerDecorationExtensions(fillers) {
    if (!Array.isArray(fillers) || fillers.length === 0) return [];
    const field = StateField.define({
        create(state) {
            return buildFillerSet(state, fillers);
        },
        update(value, tr) {
            return tr.docChanged ? buildFillerSet(tr.state, fillers) : value;
        },
        provide: (f) => EditorView.decorations.from(f),
    });
    return [field];
}

// A GutterMarker that renders no content — just an element class, so CSS can
// paint the gutter cell as a coloured change bar.
class DiffGutterMarker extends GutterMarker {
    constructor(cls) {
        super();
        this._cls = cls;
    }
    get elementClass() {
        return this._cls;
    }
}

// Compare-page change bar: a thin gutter that paints a coloured cell on every
// inserted / deleted / modified line, so changes are visible at a glance even
// when scrolled away from the tinted line. Driven by the same `lineDecorations`
// (`{line: "cm-diff-<kind>"}`) the line backgrounds use, so the two stay in
// step. Returns no extension when there are no diff lines (single-file viewer).
function buildDiffGutterExtensions(lineDecorations) {
    if (!lineDecorations || typeof lineDecorations !== "object") return [];
    if (Object.keys(lineDecorations).length === 0) return [];

    const markers = {};
    const markerFor = (kind) => {
        if (!markers[kind]) {
            markers[kind] = new DiffGutterMarker(`cm-diff-gutter-mark cm-diff-gutter-${kind}`);
        }
        return markers[kind];
    };

    return [gutter({
        class: "cm-diff-gutter",
        lineMarker(view, line) {
            const lineNo = view.state.doc.lineAt(line.from).number;
            const cls = lineDecorations[lineNo];
            if (typeof cls !== "string" || !cls.startsWith("cm-diff-")) return null;
            return markerFor(cls.slice("cm-diff-".length));
        },
    })];
}

// ── Sticky current-line highlight ─────────────────────────────────
//
// A StateField holding a Decoration.set with at most one Decoration.line
// at the chosen 1-based line. Dispatched via setCurrentLineEffect from
// scrollToLine() so the row stays tinted even after the user scrolls it
// off-screen and back (DOM classes don't survive CM's row virtualisation,
// which is what the old fade-out animation suffered from).

const setCurrentLineEffect = StateEffect.define();

const currentLineField = StateField.define({
    create() {
        return Decoration.none;
    },
    update(value, tr) {
        // Map through doc edits so the highlight follows its line when
        // the document mutates (rare for the read-only viewer but the
        // editor is shared with editable mounts).
        value = value.map(tr.changes);
        for (const effect of tr.effects) {
            if (!effect.is(setCurrentLineEffect)) continue;
            const lineNo = effect.value;
            if (!Number.isInteger(lineNo) || lineNo < 1 || lineNo > tr.state.doc.lines) {
                value = Decoration.none;
                continue;
            }
            const line = tr.state.doc.line(lineNo);
            value = Decoration.set([
                Decoration.line({ class: "cm-line--current" }).range(line.from),
            ]);
        }
        return value;
    },
    provide: f => EditorView.decorations.from(f),
});

// Theme rule keeps the highlight readable across CM's default theme +
// the one-dark theme we swap in via themeCompartment. Anchored to the
// accent palette so the tint reads in both themes. We render the
// highlight as a translucent tint plus a left-edge accent stripe
// rather than a solid fill: a flat tint behind the line text hides
// the browser-native selection rectangle whenever the user drags
// across the linked line (`?line=N`), which is the same gesture
// users expect to work. The `box-shadow inset` adds the stripe
// without disturbing the line's text layout (no padding shift).
const currentLineTheme = EditorView.baseTheme({
    ".cm-line--current": {
        backgroundColor: "var(--color-current-line-bg, rgba(99, 102, 241, 0.08))",
        boxShadow: "inset 3px 0 0 var(--color-accent, #6366f1)",
    },
    // Native ::selection on the highlighted line — the user expected
    // to be able to drag-select text inside a `?line=N` highlighted
    // line, but the accent-soft fill that originally sat there had
    // roughly the same hue as the browser's default selection blue,
    // making the selection invisible. Force a high-contrast inversion
    // (text/background swap) so selection always stands out, even when
    // it overlaps the highlight tint.
    ".cm-line--current ::selection": {
        backgroundColor: "var(--color-text, #1f2024)",
        color: "var(--color-bg, #ffffff)",
    },
    ".cm-line--current::selection": {
        backgroundColor: "var(--color-text, #1f2024)",
        color: "var(--color-bg, #ffffff)",
    },
});

/// Bottom-docked status bar — `Ln 1, Col 1 · 1,073 lines`, plus a
/// selection-length suffix when a range is selected. Mounts via CM6's
/// `showPanel` extension so the panel lives inside the editor's height
/// box and respects the same theme. Re-renders on every transaction
/// (cursor moves and document changes both flow through `update`), but
/// the DOM is cached on the panel so we only touch textContent.
///
/// When `procedures` is supplied, the bar also shows the containing
/// procedure name and a BC stack-trace-style procedure-relative line
/// number — e.g. `Ln 1247, Col 9 · in CheckDates (line 13)`. BC reports
/// stack frames as `<procedure> line N` where the `procedure`
/// declaration counts as line 0, so the relative number is
/// `cursorLine - procedure.startLine`. End of a procedure is taken
/// from `endLine` when present (modern imports) or from the next
/// procedure's `startLine - 1` (legacy fallback). When the cursor
/// doesn't sit inside any procedure the suffix is omitted entirely.
///
/// Opt-in via `mountReadOnly(..., { statusBar: true })`. The diff and
/// admin editors don't ask for it and stay untouched.
function buildStatusBarExtension(procedures) {
    // Pre-sort once; callers usually hand us a list already ordered by
    // line, but a defensive copy + sort means a single misordered entry
    // can't desync the lookup.
    const procs = Array.isArray(procedures) ? [...procedures] : [];
    procs.sort((a, b) => (a.startLine | 0) - (b.startLine | 0));

    /// Find the procedure that brackets the given 1-based line. Uses a
    /// linear scan from the end — for a single-cursor click there's no
    /// observable difference vs. a binary search, and outlines top out
    /// in the low thousands of procedures even for the largest BC
    /// codeunits. Returns null when the cursor sits before the first
    /// procedure, between two procedures of a legacy file (no
    /// `endLine`) where the gap doesn't belong to either, or after the
    /// last procedure's explicit `endLine`.
    const findContaining = (line) => {
        for (let i = procs.length - 1; i >= 0; i--) {
            const p = procs[i];
            const start = p.startLine | 0;
            if (line < start) continue;
            // Explicit end-line wins when present. Otherwise fall back
            // to the next procedure's start − 1; the gap above that
            // (after the last procedure) is treated as in-scope.
            if (typeof p.endLine === "number" && p.endLine > 0) {
                return line <= p.endLine ? p : null;
            }
            const next = procs[i + 1];
            if (next && line >= (next.startLine | 0)) return null;
            return p;
        }
        return null;
    };

    return showPanel.of(view => {
        const dom = document.createElement("div");
        dom.className = "cm-status-bar";
        const left = document.createElement("span");
        left.className = "cm-status-bar__left";
        const right = document.createElement("span");
        right.className = "cm-status-bar__right";
        dom.appendChild(left);
        dom.appendChild(right);

        const render = (state) => {
            const sel = state.selection.main;
            const line = state.doc.lineAt(sel.head);
            const col = sel.head - line.from + 1;
            const totalLines = state.doc.lines;
            const selLen = sel.to - sel.from;
            let pos = `Ln ${line.number.toLocaleString()}, Col ${col.toLocaleString()}`;
            if (procs.length > 0) {
                const proc = findContaining(line.number);
                if (proc) {
                    // BC stack-trace convention: declaration line is 0,
                    // body counts upward from there. cursorLine -
                    // startLine reproduces the number printed in the
                    // server-side stack trace.
                    const relative = line.number - (proc.startLine | 0);
                    pos += ` · in ${proc.name} (line ${relative.toLocaleString()})`;
                }
            }
            if (selLen > 0) {
                pos += ` · ${selLen.toLocaleString()} selected`;
            }
            left.textContent = pos;
            right.textContent = `${totalLines.toLocaleString()} lines`;
        };

        render(view.state);
        return {
            dom,
            update(update) {
                if (update.docChanged || update.selectionSet || update.viewportChanged) {
                    render(update.state);
                }
            },
        };
    });
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

/// Opens CodeMirror's built-in search panel from outside the editor.
/// The default Ctrl/Cmd-F binding fires only when the editor has DOM
/// focus; this helper lets the page-level shortcut bypass that.
export function openSearch(id) {
    const e = editors.get(id);
    if (!e) return;
    e.view.focus();
    openSearchPanel(e.view);
}

/// Selects the entire document in the editor identified by id. Same
/// rationale as openSearch: defaultKeymap binds Mod-a to selectAll but
/// the read-only mount keeps contenteditable=false on the contentDOM,
/// so the browser's native "select everything on the page" wins
/// instead. The source viewer's window-level Ctrl/Cmd-A handler calls
/// this when focus is inside the editor surface.
export function selectAll(id) {
    const e = editors.get(id);
    if (!e) return;
    const view = e.view;
    view.focus();
    view.dispatch({
        selection: { anchor: 0, head: view.state.doc.length },
    });
}

/// Whether the editor identified by id contains the given DOM node.
/// Used by host-page handlers (Ctrl-A intercept, etc.) so the page
/// doesn't have to know about CodeMirror's internal DOM layout.
export function containsNode(id, node) {
    const e = editors.get(id);
    if (!e || !node) return false;
    return e.view.dom.contains(node);
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
