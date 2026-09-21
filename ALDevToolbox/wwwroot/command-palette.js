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

    /** Milliseconds of quiet before a query is sent. */
    const DEBOUNCE_MS = 120;
    /** Shorter than this and the server is not asked at all - "Go to" filters locally. */
    const MIN_QUERY = 2;
    /** Longer than this and the endpoint answers empty without touching the database. */
    const MAX_QUERY = 100;
    const SEARCH_URL = "/palette/search";

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

    // ---------------------------------------------------------------- lookup

    function root() { return document.getElementById("cmdp"); }
    function list() { return document.getElementById("cmdp-list"); }

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

    /**
     * The rows this browser most recently picked. Deliberately empty: recents
     * are #887, and nothing is stored anywhere yet. The seam is here so the
     * empty-query list has one obvious place to grow.
     * @returns {HTMLAnchorElement[]}
     */
    function recentDestinations() { return []; }

    // -------------------------------------------------------------- drawing

    /** @param {DocumentFragment} into @param {string} label */
    function appendGroup(into, label) {
        const tpl = template("template[data-palette-group]");
        if (!tpl) return;
        const node = /** @type {DocumentFragment} */ (tpl.content.cloneNode(true));
        const heading = node.querySelector(".cmdp__group");
        if (heading) heading.textContent = label;
        into.appendChild(node);
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
     * else, and an href that is not an in-app path is refused outright.
     * @param {PaletteItem} item @returns {HTMLAnchorElement|null}
     */
    function resultRow(item) {
        if (!item || typeof item.href !== "string") return null;
        if (item.href.charAt(0) !== "/" || item.href.charAt(1) === "/") return null;

        const kind = typeof item.kind === "string" && /^[a-z0-9-]+$/.test(item.kind) ? item.kind : "default";
        const tpl = template('template[data-palette-row="' + kind + '"]')
            || template('template[data-palette-row="default"]');
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

        if (serverState === "unavailable") {
            const block = stateBlock("unavailable", query);
            if (block) frag.appendChild(block);
        }

        if (serverResults) {
            if (serverResults.top) {
                const row = resultRow(serverResults.top);
                if (row) { appendGroup(frag, "Best match"); frag.appendChild(row); rowCount++; }
            }
            const groups = serverResults.groups || [];
            for (let g = 0; g < groups.length; g++) {
                const group = groups[g];
                const items = (group && group.items) || [];
                const built = [];
                for (let i = 0; i < items.length; i++) {
                    const row = resultRow(items[i]);
                    if (row) built.push(row);
                }
                if (built.length === 0) continue;
                appendGroup(frag, (group && group.label) || "Results");
                for (let i = 0; i < built.length; i++) { frag.appendChild(built[i]); rowCount++; }
            }
        }

        if (!query) {
            const recents = recentDestinations();
            if (recents.length > 0) {
                appendGroup(frag, "Recent");
                for (let i = 0; i < recents.length; i++) { frag.appendChild(recents[i]); rowCount++; }
            }
        }

        const matched = matchDestinations(query);
        if (matched.length > 0) {
            appendGroup(frag, "Go to");
            for (let i = 0; i < matched.length; i++) { frag.appendChild(matched[i]); rowCount++; }
        }

        if (rowCount === 0) {
            // When the search is down we have already said so; "nothing matches"
            // on top of it would be a second sentence about the same silence.
            if (serverState === "ok") {
                const block = stateBlock("no-results", query);
                if (block) frag.appendChild(block);
            }
            const closest = closestDestination(query);
            if (closest) { appendGroup(frag, "Go to"); frag.appendChild(closest); }
        }

        container.appendChild(frag);
    }

    /** Re-draw, keeping the user's place: the row that was selected stays
     *  selected if it is still on the list. */
    function redraw() {
        const field = input();
        const query = field ? field.value.trim() : "";
        const previous = activeIndex >= 0 && rows[activeIndex] ? rows[activeIndex].getAttribute("href") : null;

        render(query);
        reindex();

        let next = 0;
        if (previous) {
            for (let i = 0; i < rows.length; i++) {
                if (rows[i].getAttribute("href") === previous) { next = i; break; }
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
            rows[i].classList.remove("is-active");
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
            rows[i].classList.remove("is-active");
            rows[i].setAttribute("aria-selected", "false");
        }
        if (rows.length === 0) {
            activeIndex = -1;
            if (field) field.removeAttribute("aria-activedescendant");
            return;
        }

        activeIndex = ((index % rows.length) + rows.length) % rows.length;
        const row = rows[activeIndex];
        row.classList.add("is-active");
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
        field.value = "";
        el.hidden = false;
        field.setAttribute("aria-expanded", "true");
        redraw();
        field.focus();
    }

    function close() {
        if (!isOpen()) return;
        const el = root();
        const field = input();

        cancel();
        if (field) {
            field.setAttribute("aria-expanded", "false");
            field.removeAttribute("aria-activedescendant");
        }
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
        if (e.key === "ArrowDown") { e.preventDefault(); select(activeIndex + 1); return; }
        if (e.key === "ArrowUp") { e.preventDefault(); select(activeIndex - 1); return; }
        if (e.key === "Home") { e.preventDefault(); select(0); return; }
        if (e.key === "End") { e.preventDefault(); select(rows.length - 1); return; }

        if (e.key === "Tab") {
            // There is one focusable thing in here and it already has focus.
            // Without this, Tab walks into the page behind the scrim, where
            // nothing is visible and nothing says where the cursor went.
            e.preventDefault();
            const field = input();
            if (field) field.focus();
            return;
        }

        if (e.key === "Enter") {
            const row = activeIndex >= 0 ? rows[activeIndex] : null;
            if (!row) return;
            e.preventDefault();
            if (e.ctrlKey || e.metaKey) {
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
        if (index >= 0 && index !== activeIndex) select(index, false);
    });

    document.addEventListener("click", function (e) {
        if (!isOpen()) return;
        const target = e.target;
        if (!(target instanceof Element)) return;

        if (target.closest("[data-cmdp-close]")) { e.preventDefault(); close(); return; }
        // A row is a real link: let the browser (and Blazor's enhanced
        // navigation) follow it, and just get out of the way.
        if (target.closest(".cmdp-row")) { close(); return; }
        if (!target.closest(".cmdp")) close();
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
    });
})();
