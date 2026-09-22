// @ts-check
//
// The command palette. Ctrl+K (Cmd+K on macOS) from any page, type, Enter.
// See .design/command-palette.md - including why this file is allowed to be
// bigger than the "tiny .razor.js companion" rule CLAUDE.md otherwise asks for:
// MainLayout is a static frame and several pages hold no circuit at all, so an
// interactive island in the layout would tax every page just so the palette can
// open.
//
// Two rules this file keeps, and they are the reason it can be trusted with
// customer names:
//
//   1. It writes no HTML and chooses no class names. Everything it puts on
//      screen is a clone of a <template> CommandPalette.razor rendered, with
//      textContent and href filled in. Markup injection is therefore not a
//      thing that can happen here, and the design system stays in one place.
//   2. It binds its listeners once, on `document`, and looks the palette's
//      elements up at open time. Blazor's enhanced navigation replaces the
//      layout's DOM on every hop, so a cached node reference is a reference to
//      something that has already been thrown away. Same idiom as
//      shell-drawer.js and nav-groups.js.
(function () {
    "use strict";

    /** One result row as the search endpoint sends it.
     * @typedef {{ kind: string, title: string, subtitle: (string|null), href: string }} PaletteItem */
    /** One ranked, capped group of rows, already in display order.
     * @typedef {{ id: string, label: string, items: PaletteItem[] }} PaletteGroup */
    /** The whole response. `top` is a single row lifted above every group.
     * @typedef {{ groups: PaletteGroup[], top: (PaletteItem|null) }} PaletteResponse */
    /** The record the page is about: its name, and its own destinations.
     * @typedef {{ label: string, items: PaletteItem[] }} PaletteContextBlock */
    /** What /palette/context answers. `recents` are the remembered links the
     *  caller may still open, in the order asked, titled by the server.
     * @typedef {{ context: (PaletteContextBlock|null), recents: PaletteItem[] }} PaletteContextResponse */

    /** Milliseconds of quiet before a query is sent. */
    const DEBOUNCE_MS = 120;
    /** Shorter than this and the server is not asked at all - "Go to" filters locally. */
    const MIN_QUERY = 2;
    /** Longer than this and the endpoint answers empty without touching the database. */
    const MAX_QUERY = 100;
    const SEARCH_URL = "/palette/search";
    const CONTEXT_URL = "/palette/context";
    /** Where recents live: this browser tab's session, and nowhere else. */
    const RECENTS_KEY = "aldt-palette-recents";
    const MAX_RECENTS = 8;
    /** Longer than any link the palette stores; the server refuses longer too. */
    const MAX_HREF = 200;
    /**
     * How long an open waits for the context block before drawing without it.
     * Long enough that a local answer lands before the first paint, so the list
     * does not draw "Go to" and then shove it down; short enough that a slow
     * answer never leaves the palette looking empty.
     */
    const CONTEXT_WAIT_MS = 200;

    /** Where focus was when the palette opened, so Esc can put it back. @type {Element|null} */
    let lastFocused = null;
    /** @type {AbortController|null} */
    let inFlight = null;
    let debounceTimer = 0;
    /** Every request carries a number; a late answer to an old query is dropped. */
    let requestSeq = 0;
    /** The last results drawn. Kept while a new request is in flight, so the
     *  list does not blink empty on every keystroke. @type {PaletteResponse|null} */
    let serverResults = null;
    /** @type {"ok"|"unavailable"} */
    let serverState = "ok";
    /** @type {HTMLAnchorElement[]} */
    let rows = [];
    let activeIndex = -1;
    /** The context block and checked recents for this open, once they arrive.
     *  Null until then, and null for good when the request failed - an
     *  unchecked recent is never drawn. @type {PaletteContextResponse|null} */
    let contextData = null;
    /** Every context request carries a number, like the search. */
    let contextSeq = 0;
    let contextTimer = 0;
    /** True between an open and its first draw, while the context is awaited. */
    let firstPaintPending = false;
    /** Whether the person has moved the selection since opening. Until they
     *  have, the context arriving puts the selection on its first row; after,
     *  their place is kept. */
    let userMoved = false;

    // ---------------------------------------------------------------- lookup

    function root() { return document.getElementById("cmdp"); }
    function list() { return document.getElementById("cmdp-list"); }
    function live() { return document.getElementById("cmdp-live"); }

    /**
     * Everything in the dialog that Tab may land on, in order: the input, Close,
     * then the foot's controls when they are on screen (Clear recents is hidden
     * without recents; the key hints hide at phone width, the link does not).
     * @returns {HTMLElement[]}
     */
    function focusStops() {
        const found = document.querySelectorAll(
            "#cmdp-input, #cmdp .cmdp__close, #cmdp .cmdp__foot-btn, #cmdp .cmdp__foot-link");
        const stops = [];
        for (let i = 0; i < found.length; i++) {
            const el = found[i];
            if (el instanceof HTMLElement && !el.hidden && el.getClientRects().length > 0) stops.push(el);
        }
        return stops;
    }

    /** @returns {HTMLInputElement|null} */
    function input() {
        const el = document.getElementById("cmdp-input");
        return el instanceof HTMLInputElement ? el : null;
    }

    function isOpen() {
        const el = root();
        return el instanceof HTMLElement && !el.hidden;
    }

    /** @param {string} selector @returns {HTMLTemplateElement|null} */
    function template(selector) {
        const el = document.querySelector(selector);
        return el instanceof HTMLTemplateElement ? el : null;
    }

    // ------------------------------------------------------------- matching

    // Case- and accent-insensitive, so `moller` finds "Møller". NFD splits the
    // accents off letters that have a decomposition; the handful below have
    // none, and Nordic customer names are not a hypothetical here.
    const LETTER_FOLDS = [[/ø/g, "o"], [/æ/g, "ae"], [/ß/g, "ss"], [/ł/g, "l"], [/đ/g, "d"]];

    /** @param {string|null} text */
    function fold(text) {
        let folded = (text || "").toLowerCase().normalize("NFD").replace(/[̀-ͯ]/g, "");
        for (let i = 0; i < LETTER_FOLDS.length; i++) {
            folded = folded.replace(
                /** @type {RegExp} */(LETTER_FOLDS[i][0]),
                /** @type {string} */(LETTER_FOLDS[i][1]));
        }
        return folded;
    }

    /** The query split into terms. Every term has to match somewhere, in any order.
     * @param {string} query @returns {string[]} */
    function terms(query) {
        return fold(query).split(/\s+/).filter(function (t) { return t.length > 0; });
    }

    /** @param {string} haystack @param {string} term */
    function matchesWordStart(haystack, term) {
        let at = haystack.indexOf(term);
        while (at >= 0) {
            if (at === 0 || !/[a-z0-9]/.test(haystack.charAt(at - 1))) return true;
            at = haystack.indexOf(term, at + 1);
        }
        return false;
    }

    /**
     * How well a row matches: 0 is no match, then substring, word-start, and a
     * prefix of the title itself. The tiers are the ones in the design doc, and
     * the same order the server ranks its own rows in.
     * @param {string} haystack @param {string} title @param {string[]} termList
     */
    function score(haystack, title, termList) {
        for (let i = 0; i < termList.length; i++) {
            if (haystack.indexOf(termList[i]) < 0) return 0;
        }
        if (title.indexOf(termList.join(" ")) === 0) return 3;
        for (let i = 0; i < termList.length; i++) {
            if (!matchesWordStart(haystack, termList[i])) return 1;
        }
        return 2;
    }

    // ------------------------------------------------------------- "Go to"

    /** The destinations Razor rendered for this person, inert inside a <template>.
     * @returns {HTMLAnchorElement[]} */
    function destinations() {
        const tpl = template("#cmdp-goto");
        if (!tpl) return [];
        return /** @type {HTMLAnchorElement[]} */ (
            Array.prototype.slice.call(tpl.content.querySelectorAll(".cmdp-row")));
    }

    /** @param {HTMLAnchorElement} row @returns {HTMLAnchorElement} */
    function clone(row) {
        return /** @type {HTMLAnchorElement} */ (row.cloneNode(true));
    }

    /** @param {HTMLAnchorElement} row @param {string} attribute */
    function attr(row, attribute) { return row.getAttribute(attribute) || ""; }

    /**
     * The destinations matching `query`, best tier first and ties broken by
     * sidebar order. Alphabetical would be the obvious tie-break and is wrong
     * here: "trans" is a prefix of both "Translation memory" and "Translator",
     * and by name the admin page wins, so typing three letters and pressing
     * Enter lands a consultant on a page they did not want. The sidebar's order
     * is a real editorial order - tools first, then the authoring pages - and
     * respecting it puts the tool above the page that administers it every
     * time. An empty query keeps that order too.
     * @param {string} query @returns {HTMLAnchorElement[]}
     */
    function matchDestinations(query) {
        const source = destinations();
        const termList = terms(query);
        if (termList.length === 0) return source.map(clone);

        const hits = [];
        for (let i = 0; i < source.length; i++) {
            const row = source[i];
            const points = score(fold(attr(row, "data-search")), fold(attr(row, "data-title")), termList);
            if (points > 0) hits.push({ row: row, points: points, order: i });
        }
        hits.sort(function (a, b) { return b.points - a.points || a.order - b.order; });
        return hits.map(function (hit) { return clone(hit.row); });
    }

    /**
     * The one tool to offer when nothing matched at all: the closest thing to
     * what was typed, or the Solutions list, which is where someone looking for
     * a customer finds out whether it exists.
     * @param {string} query @returns {HTMLAnchorElement|null}
     */
    function closestDestination(query) {
        const source = destinations();
        if (source.length === 0) return null;

        const termList = terms(query);
        for (let i = 0; i < source.length; i++) {
            const haystack = fold(attr(source[i], "data-search"));
            for (let t = 0; t < termList.length; t++) {
                if (haystack.indexOf(termList[t]) >= 0) return clone(source[i]);
            }
        }
        for (let i = 0; i < source.length; i++) {
            if (attr(source[i], "href") === "/solutions") return clone(source[i]);
        }
        return clone(source[0]);
    }

    // ------------------------------------------------------------ Commands

    /**
     * Every action this file will run. A row naming anything else is not drawn
     * and cannot be run. Kept equal to PaletteCommands.Actions on the server,
     * and none of them may write to a customer's tenant - see
     * .design/command-palette.md, "Commands".
     */
    const ACTIONS = ["theme:light", "theme:dark", "theme:system", "copy-link", "sign-out", "refresh"];
    /** How long "Copied" stays on the row before the palette closes. */
    const COPIED_MS = 1200;

    /** @param {Element} row @returns {string} */
    function actionOf(row) {
        const action = row.getAttribute("data-command-action") || "";
        return ACTIONS.indexOf(action) >= 0 ? action : "";
    }

    /** What a re-draw keeps the selection on: a link, or for an act command its action.
     * @param {Element} row @returns {string|null} */
    function rowKey(row) {
        return row.getAttribute("href") || row.getAttribute("data-command-action");
    }

    /**
     * The page's own Refresh button, when it has one it is offering right now.
     * A button that is mid-refresh (disabled) is not offered - pressing it again
     * would do nothing.
     * @returns {HTMLButtonElement|null}
     */
    function pageRefreshButton() {
        const el = document.querySelector("[data-page-refresh]");
        return el instanceof HTMLButtonElement && !el.disabled && el.getClientRects().length > 0 ? el : null;
    }

    /** @returns {{ current: function(): string, set: function(string): void }|null} */
    function themeApi() {
        const aldt = /** @type {any} */ (window).aldt;
        return aldt && aldt.theme && typeof aldt.theme.set === "function" ? aldt.theme : null;
    }

    /**
     * The commands Razor rendered for this person, less the ones this page or
     * this browser cannot run: Refresh on a page with no Refresh, an action the
     * script does not know.
     * @returns {HTMLAnchorElement[]}
     */
    function commands() {
        const tpl = template("#cmdp-commands");
        if (!tpl) return [];
        const all = /** @type {HTMLAnchorElement[]} */ (
            Array.prototype.slice.call(tpl.content.querySelectorAll(".cmdp-row")));
        return all.filter(function (row) {
            if (!row.hasAttribute("data-command-action")) return true;
            const action = actionOf(row);
            if (!action) return false;
            if (action === "refresh") return pageRefreshButton() !== null;
            if (action.indexOf("theme:") === 0) return themeApi() !== null;
            return true;
        });
    }

    /**
     * A command row ready to draw. The theme row matching the current theme
     * shows the "Current" line Razor rendered for it, hidden, in place of its
     * usual one.
     * @param {HTMLAnchorElement} row @returns {HTMLAnchorElement}
     */
    function commandClone(row) {
        const copy = clone(row);
        const action = actionOf(copy);
        const theme = themeApi();
        if (theme && action.indexOf("theme:") === 0) {
            swapSubtitle(copy, "data-cmdp-sub-current", action === "theme:" + theme.current());
        }
        return copy;
    }

    /**
     * Shows one of a row's alternative second lines instead of its usual one.
     * @param {Element} row @param {string} attribute @param {boolean} on
     */
    function swapSubtitle(row, attribute, on) {
        if (!row.querySelector("[" + attribute + "]")) return;
        const wanted = on ? attribute : "data-cmdp-sub";
        const lines = row.querySelectorAll(".cmdp-row__sub");
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (line instanceof HTMLElement) line.hidden = !line.hasAttribute(wanted);
        }
    }

    /** Commands matching `query`, ranked like "Go to": best tier first, ties in catalogue order.
     * @param {string} query @returns {HTMLAnchorElement[]} */
    function matchCommands(query) {
        const source = commands();
        const termList = terms(query);
        // Before anything is typed, only the few marked for it: the box is
        // opened to jump somewhere, and a dozen commands would push every page
        // below the fold. The rest are one word away.
        if (termList.length === 0) {
            return source
                .filter(function (row) { return row.hasAttribute("data-cmdp-on-open"); })
                .map(commandClone);
        }

        const hits = [];
        for (let i = 0; i < source.length; i++) {
            const row = source[i];
            const points = score(fold(attr(row, "data-search")), fold(attr(row, "data-title")), termList);
            if (points > 0) hits.push({ row: row, points: points, order: i });
        }
        hits.sort(function (a, b) { return b.points - a.points || a.order - b.order; });
        return hits.map(function (hit) { return commandClone(hit.row); });
    }

    /**
     * Runs an act command. Only the actions in ACTIONS exist; anything else is
     * refused before it gets here and again here. Every one closes the palette
     * and gives focus back first, so what it does lands on the page, not in the
     * dialog - except Copy link, which shows "Copied" on its row for a moment
     * so the person can see it worked.
     * @param {HTMLElement} row
     */
    function runCommand(row) {
        const action = actionOf(row);
        if (!action) return;

        if (action === "copy-link") {
            const address = window.location.href;
            // Says how it went on the row itself, out loud as well, and closes
            // only on success: a failed copy has to stay on screen long enough
            // to be read, and the person then closes it themselves.
            /** @param {string} which @param {boolean} closeAfter */
            const report = function (which, closeAfter) {
                swapSubtitle(row, which, true);
                const said = row.querySelector("[" + which + "]");
                const region = live();
                if (region && said) region.textContent = said.textContent || "";
                if (closeAfter) window.setTimeout(close, COPIED_MS);
            };
            if (!navigator.clipboard) { report("data-cmdp-sub-failed", false); return; }
            navigator.clipboard.writeText(address).then(
                function () { report("data-cmdp-sub-done", true); },
                function () { report("data-cmdp-sub-failed", false); });
            return;
        }

        close();
        if (action.indexOf("theme:") === 0) {
            const theme = themeApi();
            if (theme) theme.set(action.substring("theme:".length));
        } else if (action === "sign-out") {
            // The top bar's own form: it carries the antiforgery token, and a
            // POST built here would be a second way to sign out to keep right.
            const form = document.querySelector("form.signout-form");
            if (form instanceof HTMLFormElement) form.requestSubmit();
        } else if (action === "refresh") {
            const button = pageRefreshButton();
            if (button) button.click();
        }
    }

    // ------------------------------------------------------ where you have been

    /** An in-app path, and nothing that could leave the app.
     * @param {unknown} href @returns {href is string} */
    function isLocalHref(href) {
        return typeof href === "string"
            && href.length > 0 && href.length <= MAX_HREF
            && href.charAt(0) === "/" && href.charAt(1) !== "/" && href.charAt(1) !== "\\";
    }

    /**
     * The links this browser tab went to recently, newest first. Links only -
     * never a title, so no customer name is ever written to storage; the
     * server titles each one when it re-checks it. Storage can be missing,
     * full or refused (a private window, a blocked site), and every one of
     * those reads as "no recents".
     * @returns {string[]}
     */
    function readRecents() {
        try {
            const parsed = JSON.parse(window.sessionStorage.getItem(RECENTS_KEY) || "[]");
            if (!Array.isArray(parsed)) return [];
            return parsed.filter(isLocalHref).slice(0, MAX_RECENTS);
        } catch (e) {
            return [];
        }
    }

    /** @param {string[]} hrefs */
    function writeRecents(hrefs) {
        try {
            if (hrefs.length === 0) window.sessionStorage.removeItem(RECENTS_KEY);
            else window.sessionStorage.setItem(RECENTS_KEY, JSON.stringify(hrefs.slice(0, MAX_RECENTS)));
        } catch (e) { /* storage refused: the palette simply has no recents */ }
    }

    /** Puts `href` at the front of the recents. @param {string|null} href */
    function remember(href) {
        if (!isLocalHref(href)) return;
        const rest = readRecents().filter(function (h) { return h !== href; });
        writeRecents([href].concat(rest));
    }

    /**
     * What the page says it is about, from the one element it renders for the
     * purpose (PaletteContext.razor). Looked up each time, never cached: an
     * enhanced navigation swaps the page underneath us.
     * @returns {{ at: string, href: string }|null}
     */
    function pageContext() {
        const el = document.querySelector("[data-palette-context]");
        if (!el) return null;
        const at = el.getAttribute("data-palette-context") || "";
        const href = el.getAttribute("data-palette-href") || "";
        if (!at) return null;
        return { at: at, href: isLocalHref(href) ? href : "" };
    }

    /** A visit to a solution or an environment counts as having been there,
     *  however you arrived. */
    function rememberVisit() {
        const here = pageContext();
        if (here && here.href) remember(here.href);
    }

    /**
     * Asks the server for this page's context block and which recents may still
     * be offered. The current page is left out of the recents sent: its context
     * block already stands for it. Recents the server declined are dropped from
     * storage too, so a Solution that went Private does not come back the next
     * time the server is unreachable.
     */
    function loadContext() {
        const here = pageContext();
        const sent = readRecents().filter(function (h) { return !here || h !== here.href; });
        const mine = ++contextSeq;
        contextData = null;

        if (!here && sent.length === 0) {
            firstPaintPending = false;
            return;
        }

        const params = new URLSearchParams();
        if (here) params.append("at", here.at);
        for (let i = 0; i < sent.length; i++) params.append("recent", sent[i]);

        firstPaintPending = true;
        window.clearTimeout(contextTimer);
        contextTimer = window.setTimeout(function () {
            if (mine !== contextSeq || !firstPaintPending) return;
            firstPaintPending = false;
            if (isOpen()) redraw(true);
        }, CONTEXT_WAIT_MS);

        fetch(CONTEXT_URL + "?" + params.toString(), {
            credentials: "same-origin",
            headers: { "Accept": "application/json" }
        }).then(function (response) {
            if (!response.ok) throw new Error("palette context returned " + response.status);
            return response.json();
        }).then(function (data) {
            if (mine !== contextSeq) return;
            const answer = /** @type {PaletteContextResponse} */ (data || {});
            contextData = {
                context: answer.context && Array.isArray(answer.context.items) ? answer.context : null,
                recents: Array.isArray(answer.recents) ? answer.recents : []
            };
            pruneRecents(sent, contextData.recents);
            settle();
        }).catch(function () {
            if (mine !== contextSeq) return;
            // Nothing is shown that the server has not just vouched for, so a
            // failed check means no context and no recents - "Go to" still works.
            contextData = null;
            settle();
        });

        function settle() {
            window.clearTimeout(contextTimer);
            const fresh = firstPaintPending || !userMoved;
            firstPaintPending = false;
            if (isOpen()) redraw(fresh);
        }
    }

    /** @param {string[]} sent @param {PaletteItem[]} kept */
    function pruneRecents(sent, kept) {
        const keptHrefs = kept.map(function (item) { return item && item.href; });
        const stored = readRecents();
        const next = stored.filter(function (h) {
            return sent.indexOf(h) < 0 || keptHrefs.indexOf(h) >= 0;
        });
        if (next.length !== stored.length) writeRecents(next);
    }

    /** Forgets every recent, on the person's say-so. */
    function clearRecents() {
        writeRecents([]);
        if (contextData) contextData = { context: contextData.context, recents: [] };
        redraw(true);
        const field = input();
        if (field) field.focus();
    }

    /**
     * Rows for `items` that match `query` - all of them, in the server's order,
     * for an empty query; otherwise best tier first under the same
     * every-term-must-match rule as "Go to", ties kept in the server's order.
     * Rows whose link is already on screen are skipped, so a recent that is
     * also in the context block is drawn once.
     * @param {PaletteItem[]} items @param {string} query @param {Set<string>} drawn
     * @returns {HTMLAnchorElement[]}
     */
    function matchItems(items, query, drawn) {
        const termList = terms(query);
        const hits = [];
        for (let i = 0; i < items.length; i++) {
            const item = items[i];
            if (!item || typeof item.href !== "string" || drawn.has(item.href)) continue;
            let points = 1;
            if (termList.length > 0) {
                const title = fold(item.title || "");
                points = score(title + " " + fold(item.subtitle || ""), title, termList);
                if (points === 0) continue;
            }
            const row = resultRow(item);
            if (row) hits.push({ row: row, points: points, order: i, href: item.href });
        }
        hits.sort(function (a, b) { return b.points - a.points || a.order - b.order; });
        return hits.map(function (hit) { drawn.add(hit.href); return hit.row; });
    }

    // -------------------------------------------------------------- drawing

    /**
     * Starts a group and returns the node its rows belong in. The rows go
     * *inside* a role="group" rather than after a loose heading, because a
     * listbox may only own options and groups - and because the grouping is
     * the difference between "Contoso" the Solution and "Contoso" the
     * environment, which is worth hearing as well as seeing.
     * @param {DocumentFragment|HTMLElement} into @param {string} label
     * @returns {DocumentFragment|HTMLElement} where to append this group's rows
     */
    function appendGroup(into, label) {
        const tpl = template("template[data-palette-group]");
        if (!tpl) return into;
        const node = /** @type {DocumentFragment} */ (tpl.content.cloneNode(true));
        const heading = node.querySelector(".cmdp__group");
        if (heading) heading.textContent = label;
        const group = node.querySelector(".cmdp__group-wrap");
        if (group instanceof HTMLElement) group.setAttribute("aria-label", label);
        into.appendChild(node);
        return group instanceof HTMLElement ? group : into;
    }

    /** @param {string} kind @param {string} query @returns {DocumentFragment|null} */
    function stateBlock(kind, query) {
        const tpl = template('template[data-palette-state="' + kind + '"]');
        if (!tpl) return null;
        const node = /** @type {DocumentFragment} */ (tpl.content.cloneNode(true));
        const slot = node.querySelector("[data-cmdp-query]");
        if (slot) slot.textContent = query;
        return node;
    }

    /**
     * One row from the endpoint. The kind picks the template - and only ever a
     * template - so an unknown kind falls back rather than reaching anything
     * else, and an href that is not an in-app path is refused outright. The
     * one exception is the "external" kind (Business Central and its admin
     * centre, from an environment's context block), which may carry an https
     * link and nothing else; its template opens it in a new tab.
     * @param {PaletteItem} item @returns {HTMLAnchorElement|null}
     */
    function resultRow(item) {
        if (!item || typeof item.href !== "string") return null;

        const kind = typeof item.kind === "string" && /^[a-z0-9-]+$/.test(item.kind) ? item.kind : "default";
        if (kind === "external") {
            if (!/^https:\/\/[^/\\]/.test(item.href)) return null;
        } else if (!isLocalHref(item.href)) {
            return null;
        }
        // An outside link never falls back to the default template: that one
        // would open it in place of the app.
        const tpl = template('template[data-palette-row="' + kind + '"]')
            || (kind === "external" ? null : template('template[data-palette-row="default"]'));
        if (!tpl) return null;

        const node = /** @type {DocumentFragment} */ (tpl.content.cloneNode(true));
        const row = node.querySelector(".cmdp-row");
        if (!(row instanceof HTMLAnchorElement)) return null;

        row.setAttribute("href", item.href);
        const title = row.querySelector(".cmdp-row__title");
        if (title) title.textContent = item.title || "";
        const subtitle = row.querySelector(".cmdp-row__sub");
        if (subtitle) subtitle.textContent = item.subtitle || "";
        return row;
    }

    /** @param {string} query */
    function render(query) {
        const container = list();
        if (!container) return;

        container.textContent = "";
        const frag = document.createDocumentFragment();
        let rowCount = 0;
        /** Links already drawn, so one place is offered once. "Go to" is not
         *  counted: it is the whole tool list, and stays whole. @type {Set<string>} */
        const drawn = new Set();

        if (serverState === "unavailable") {
            const block = stateBlock("unavailable", query);
            if (block) frag.appendChild(block);
        }

        if (serverResults && serverResults.top) {
            const row = resultRow(serverResults.top);
            if (row) {
                appendGroup(frag, "Best match").appendChild(row);
                drawn.add(serverResults.top.href);
                rowCount++;
            }
        }

        // Where you are, then where you have been - both filtered here, like
        // "Go to", so they answer every keystroke at once and sit still above
        // the search results as those arrive.
        if (contextData && contextData.context) {
            const block = contextData.context;
            const built = matchItems(block.items, query, drawn);
            if (built.length > 0) {
                const into = appendGroup(frag, block.label || "This page");
                for (let i = 0; i < built.length; i++) { into.appendChild(built[i]); rowCount++; }
            }
        }

        let recentCount = 0;
        if (contextData && contextData.recents.length > 0) {
            const built = matchItems(contextData.recents, query, drawn);
            if (built.length > 0) {
                const into = appendGroup(frag, "Recent");
                for (let i = 0; i < built.length; i++) { into.appendChild(built[i]); rowCount++; }
            }
            recentCount = contextData.recents.length;
        }
        showClearRecents(recentCount > 0);

        // Commands are local too, so they sit with the other local groups and
        // search results never land above them. Not counted in `drawn`: a
        // command is an action, and "New workspace" beside "Workspace" in Go to
        // is two ways to say one thing, both worth finding.
        const matchedCommands = matchCommands(query);
        if (matchedCommands.length > 0) {
            const into = appendGroup(frag, "Commands");
            for (let i = 0; i < matchedCommands.length; i++) { into.appendChild(matchedCommands[i]); rowCount++; }
        }

        if (serverResults) {
            const groups = serverResults.groups || [];
            for (let g = 0; g < groups.length; g++) {
                const group = groups[g];
                const items = (group && group.items) || [];
                const built = [];
                for (let i = 0; i < items.length; i++) {
                    if (!items[i] || drawn.has(items[i].href)) continue;
                    const row = resultRow(items[i]);
                    if (row) { built.push(row); drawn.add(items[i].href); }
                }
                if (built.length === 0) continue;
                const into = appendGroup(frag, (group && group.label) || "Results");
                for (let i = 0; i < built.length; i++) { into.appendChild(built[i]); rowCount++; }
            }
        }

        const matched = matchDestinations(query);
        if (matched.length > 0) {
            const into = appendGroup(frag, "Go to");
            for (let i = 0; i < matched.length; i++) { into.appendChild(matched[i]); rowCount++; }
        }

        const empty = rowCount === 0;
        if (empty) {
            // When the search is down we have already said so; "nothing matches"
            // on top of it would be a second sentence about the same silence.
            if (serverState === "ok") {
                const block = stateBlock("no-results", query);
                if (block) frag.appendChild(block);
            }
            const closest = closestDestination(query);
            if (closest) appendGroup(frag, "Go to").appendChild(closest);
        }

        container.appendChild(frag);
        announce(query, rowCount, empty);
    }

    /**
     * What a screen reader is told after a re-draw. Following
     * aria-activedescendant it hears the selected row and nothing else, so the
     * two sentences that *replace* the rows would otherwise land in silence,
     * and so would "seven results" when the list refills under a stationary
     * cursor. An empty query says nothing: the list is then the page index the
     * input's own label already described.
     * @param {string} query @param {number} rowCount @param {boolean} empty
     */
    function announce(query, rowCount, empty) {
        const region = live();
        if (!region) return;

        let message = "";
        if (serverState === "unavailable") {
            message = "Search is not available right now. You can still go to any page.";
        } else if (empty && query) {
            message = 'No search results for "' + query + '".';
        } else if (query) {
            message = rowCount === 1 ? "1 result." : rowCount + " results.";
        }

        if (region.textContent !== message) region.textContent = message;
    }

    /** The foot's Clear recents, shown only while there are recents to clear.
     * @param {boolean} show */
    function showClearRecents(show) {
        const button = clearButton();
        if (button) button.hidden = !show;
    }

    /** @returns {HTMLButtonElement|null} */
    function clearButton() {
        const el = document.querySelector("#cmdp [data-cmdp-clear-recents]");
        return el instanceof HTMLButtonElement ? el : null;
    }

    /**
     * Re-draw, keeping the user's place: the row that was selected stays
     * selected if it is still on the list. `fresh` starts again at the top
     * instead - for the context block arriving under a person who has not
     * moved yet, where "their place" is only the row that happened to be first.
     * @param {boolean} [fresh]
     */
    function redraw(fresh) {
        const field = input();
        const query = field ? field.value.trim() : "";
        const previous = !fresh && activeIndex >= 0 && rows[activeIndex] ? rowKey(rows[activeIndex]) : null;

        render(query);
        reindex();

        let next = 0;
        if (previous) {
            for (let i = 0; i < rows.length; i++) {
                if (rowKey(rows[i]) === previous) { next = i; break; }
            }
        }
        select(next);
    }

    /** Gives every row on screen an id, which is what aria-activedescendant points at. */
    function reindex() {
        const container = list();
        rows = container
            ? /** @type {HTMLAnchorElement[]} */ (Array.prototype.slice.call(container.querySelectorAll(".cmdp-row")))
            : [];
        for (let i = 0; i < rows.length; i++) {
            rows[i].id = "cmdp-opt-" + i;
            rows[i].setAttribute("aria-selected", "false");
        }
    }

    /**
     * @param {number} index Wraps, so Down from the last row lands on the first.
     * @param {boolean} [scroll] False when the pointer put us here - scrolling a
     * half-visible row under the cursor into view moves the list out from under it.
     */
    function select(index, scroll) {
        const field = input();
        for (let i = 0; i < rows.length; i++) {
            rows[i].setAttribute("aria-selected", "false");
        }
        if (rows.length === 0) {
            activeIndex = -1;
            if (field) field.removeAttribute("aria-activedescendant");
            return;
        }

        activeIndex = ((index % rows.length) + rows.length) % rows.length;
        const row = rows[activeIndex];
        row.setAttribute("aria-selected", "true");
        if (scroll !== false) row.scrollIntoView({ block: "nearest" });
        if (field) field.setAttribute("aria-activedescendant", row.id);
    }

    // --------------------------------------------------------- open / close

    function open() {
        const el = root();
        const field = input();
        if (!(el instanceof HTMLElement) || !field) return;

        lastFocused = document.activeElement;
        serverResults = null;
        serverState = "ok";
        userMoved = false;
        field.value = "";
        el.hidden = false;
        field.setAttribute("aria-expanded", "true");
        loadContext();
        if (firstPaintPending) {
            // Held for the context block, briefly: see CONTEXT_WAIT_MS. The input
            // is live meanwhile, and typing draws at once.
            const container = list();
            if (container) container.textContent = "";
            rows = [];
            activeIndex = -1;
            showClearRecents(false);
        } else {
            redraw();
        }
        field.focus();
    }

    function close() {
        if (!isOpen()) return;
        const el = root();
        const field = input();

        cancel();
        // A context answer still on its way belongs to this open, not the next.
        contextSeq++;
        window.clearTimeout(contextTimer);
        firstPaintPending = false;
        if (field) {
            field.setAttribute("aria-expanded", "false");
            field.removeAttribute("aria-activedescendant");
        }
        // Emptied rather than left holding the last count: the next open starts
        // on an empty query, which says nothing, and a stale sentence would be
        // read out the moment anything else touched the region.
        const region = live();
        if (region) region.textContent = "";
        if (el instanceof HTMLElement) el.hidden = true;
        activeIndex = -1;

        if (lastFocused instanceof HTMLElement && document.contains(lastFocused)) {
            try { lastFocused.focus(); } catch (e) { /* the element may have gone away mid-navigation */ }
        }
        lastFocused = null;
    }

    /** True while one of our own dialogs is up. A ConfirmDialog naming a
     *  production environment is not something to be navigated away from by
     *  reflex, so the hotkey stands down rather than stealing the keystroke. */
    function otherOverlayIsOpen() {
        const layers = document.querySelectorAll(".modal-layer");
        for (let i = 0; i < layers.length; i++) {
            const layer = layers[i];
            if (layer.id === "cmdp") continue;
            if (layer instanceof HTMLElement && layer.hidden) continue;
            return true;
        }
        return document.querySelector("dialog[open]") !== null;
    }

    function isMac() {
        const agent = /** @type {any} */ (navigator);
        const platform = (agent.userAgentData && agent.userAgentData.platform)
            || navigator.platform || navigator.userAgent || "";
        return /mac|iphone|ipad|ipod/i.test(platform);
    }

    /**
     * Teaches the top bar's button which modifier this machine uses. Razor
     * renders "Ctrl" because the server cannot know, and the decision itself
     * lives in isMac() above - the same one line that decides which keystroke
     * actually opens the palette, rather than a second copy of it in markup
     * that could disagree.
     *
     * The spoken half is a separate, clipped span: the key caps are decoration
     * (aria-hidden), so without it the button's name would be "Search or jump
     * to..." and the shortcut would be the one thing a screen-reader user never
     * heard.
     */
    function applyShortcutLabels() {
        const mac = isMac();
        const mod = mac ? "Cmd" : "Ctrl";

        const caps = document.querySelectorAll("[data-cmdp-mod]");
        for (let i = 0; i < caps.length; i++) caps[i].textContent = mod;

        const spoken = document.querySelectorAll("[data-cmdp-shortcut]");
        for (let i = 0; i < spoken.length; i++) spoken[i].textContent = ", " + mod + " K";

        const openers = document.querySelectorAll("[data-cmdp-open]");
        for (let i = 0; i < openers.length; i++) {
            openers[i].setAttribute("aria-keyshortcuts", mac ? "Meta+K" : "Control+K");
        }
    }

    // -------------------------------------------------------------- search

    function cancel() {
        window.clearTimeout(debounceTimer);
        if (inFlight) { inFlight.abort(); inFlight = null; }
    }

    /** @param {string} raw */
    function scheduleSearch(raw) {
        window.clearTimeout(debounceTimer);
        const query = raw.trim();
        if (query.length < MIN_QUERY || query.length > MAX_QUERY) {
            // Nothing to ask, and nothing worth keeping from the last answer:
            // the user is back to browsing the tool list.
            cancel();
            requestSeq++;
            serverResults = null;
            serverState = "ok";
            return;
        }
        debounceTimer = window.setTimeout(function () { runSearch(query); }, DEBOUNCE_MS);
    }

    /** @param {string} query */
    function runSearch(query) {
        cancel();
        const controller = new AbortController();
        inFlight = controller;
        const mine = ++requestSeq;

        fetch(SEARCH_URL + "?q=" + encodeURIComponent(query), {
            signal: controller.signal,
            credentials: "same-origin",
            headers: { "Accept": "application/json" }
        }).then(function (response) {
            if (!response.ok) throw new Error("palette search returned " + response.status);
            return response.json();
        }).then(function (data) {
            if (mine !== requestSeq) return;
            serverResults = /** @type {PaletteResponse} */ (data);
            serverState = "ok";
            redraw();
        }).catch(function (error) {
            if (error && error.name === "AbortError") return;
            if (mine !== requestSeq) return;
            // Offline, 404 while the endpoint is not deployed yet, 500, a
            // signed-out cookie: all of them mean the same thing to the person
            // typing, and "Go to" still works.
            serverResults = null;
            serverState = "unavailable";
            redraw();
        });
    }

    // ------------------------------------------------------------ listeners

    // Capture phase, on the document, so CodeMirror's keymap and the source
    // viewer's own shortcuts never see the keystroke.
    document.addEventListener("keydown", function (e) {
        if (e.key !== "k" && e.key !== "K") return;
        if (e.altKey || e.shiftKey) return;
        const wanted = isMac() ? (e.metaKey && !e.ctrlKey) : (e.ctrlKey && !e.metaKey);
        if (!wanted) return;
        if (!root()) return;

        if (isOpen()) {
            e.preventDefault();
            e.stopPropagation();
            close();
            return;
        }
        if (otherOverlayIsOpen()) return;

        e.preventDefault();
        e.stopPropagation();
        open();
    }, true);

    document.addEventListener("keydown", function (e) {
        if (!isOpen()) return;

        if (e.key === "Escape") { e.preventDefault(); close(); return; }
        if (e.key === "ArrowDown") { e.preventDefault(); userMoved = true; select(activeIndex + 1); return; }
        if (e.key === "ArrowUp") { e.preventDefault(); userMoved = true; select(activeIndex - 1); return; }
        if (e.key === "Home") { e.preventDefault(); userMoved = true; select(0); return; }
        if (e.key === "End") { e.preventDefault(); userMoved = true; select(rows.length - 1); return; }

        if (e.key === "Tab") {
            // Focus cycles through what in here can hold it - the input, Close,
            // and the foot's Clear recents and docs link when they are showing -
            // and never leaves, because outside the scrim nothing is visible and
            // nothing says where the cursor went.
            const field = input();
            if (!field) return;
            e.preventDefault();
            const stops = focusStops();
            const at = stops.indexOf(/** @type {HTMLElement} */(document.activeElement));
            const step = e.shiftKey ? -1 : 1;
            const next = at < 0 ? 0 : (at + step + stops.length) % stops.length;
            (stops[next] || field).focus();
            return;
        }

        if (e.key === "Enter") {
            // Enter on Close or Clear recents is that button's own press, not
            // "open the selected row" - which is what it used to do on Close.
            if (document.activeElement !== input()) return;
            const row = activeIndex >= 0 ? rows[activeIndex] : null;
            if (!row) return;
            e.preventDefault();
            // An act command has nowhere to open in a new tab; it just runs.
            if (row.hasAttribute("data-command-action")) { runCommand(row); return; }
            if (e.ctrlKey || e.metaKey) {
                if (row.hasAttribute("data-cmdp-remember")) remember(row.getAttribute("href"));
                window.open(row.href, "_blank", "noopener");
                return;
            }
            row.click();
            close();
        }
    });

    document.addEventListener("input", function (e) {
        if (!isOpen()) return;
        const target = e.target;
        if (!(target instanceof HTMLInputElement) || target.id !== "cmdp-input") return;
        // Typing draws now, context or not; the block slots in when it lands.
        firstPaintPending = false;
        scheduleSearch(target.value);
        redraw();
    });

    // The pointer moves the selection instead of lighting a row of its own:
    // two highlighted rows is two answers to "what does Enter do".
    document.addEventListener("mousemove", function (e) {
        if (!isOpen() || rows.length === 0) return;
        const target = e.target;
        if (!(target instanceof Element)) return;
        const row = target.closest(".cmdp-row");
        if (!row) return;
        const index = rows.indexOf(/** @type {HTMLAnchorElement} */(row));
        if (index >= 0 && index !== activeIndex) { userMoved = true; select(index, false); }
    });

    document.addEventListener("click", function (e) {
        if (!isOpen()) return;
        const target = e.target;
        if (!(target instanceof Element)) return;

        // The opener is outside .cmdp, so the last rule here would read a click
        // on it as a click on the page behind and close what it just opened.
        // The listener below owns that button.
        if (target.closest("[data-cmdp-open]")) return;

        if (target.closest("[data-cmdp-close]")) { e.preventDefault(); close(); return; }
        if (target.closest("[data-cmdp-clear-recents]")) { e.preventDefault(); clearRecents(); return; }
        // A row is a real link: let the browser (and Blazor's enhanced
        // navigation) follow it, and just get out of the way. A place worth
        // coming back to - a record, a page from "Go to" - is remembered on the
        // way; a tab of the page you are on is not, its record already is.
        const picked = target.closest(".cmdp-row");
        if (picked instanceof HTMLElement && picked.hasAttribute("data-command-action")) {
            e.preventDefault();
            runCommand(picked);
            return;
        }
        if (picked) {
            if (picked.hasAttribute("data-cmdp-remember")) remember(picked.getAttribute("href"));
            close();
            return;
        }
        if (!target.closest(".cmdp")) close();
    });

    // The top bar's way in, and on a phone or a tablet the only one. It runs
    // the same open() the hotkey does - one code path, so the two can never
    // disagree about what "open" means. Registered after the handler above so
    // that handler has already declined the click.
    document.addEventListener("click", function (e) {
        const target = e.target;
        if (!(target instanceof Element)) return;
        if (!target.closest("[data-cmdp-open]")) return;

        e.preventDefault();
        if (isOpen()) { close(); return; }
        if (!root() || otherOverlayIsOpen()) return;
        open();
    });

    // Enhanced navigation patches the layout, which replaces the palette with a
    // fresh hidden skeleton. Anything we were holding points at nodes that are
    // gone.
    document.addEventListener("enhancedload", function () {
        cancel();
        rows = [];
        activeIndex = -1;
        lastFocused = null;
        serverResults = null;
        serverState = "ok";
        contextSeq++;
        contextData = null;
        window.clearTimeout(contextTimer);
        firstPaintPending = false;
        applyShortcutLabels();
        rememberVisit();
    });

    // The scripts sit at the end of <body>, so the top bar is already parsed -
    // and so is the page, and with it any context element it rendered.
    applyShortcutLabels();
    rememberVisit();
})();
