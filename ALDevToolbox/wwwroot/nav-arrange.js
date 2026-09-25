// Arranging the sidebar (issue #956).
//
// Same constraint as nav-groups.js: the sidebar is server-rendered and not
// interactive, so the arrange mode lives here rather than on a circuit. The
// order is kept in a cookie the server reads at render (Domain/Navigation/
// SidebarOrder.cs), so every page arrives already in the person's order and
// Blazor's enhanced-navigation diff has nothing to undo. Groups move among
// groups and items within their own group, never across.
(function () {
    "use strict";

    const COOKIE = "aldt-nav-order";
    const COOKIE_MAX_AGE = 60 * 60 * 24 * 365;
    let dragged = null;
    let beforeReset = null; // the order a reset replaced, for Undo; lives until the mode ends

    const nav = () => document.querySelector(".app__nav");
    const scroller = () => document.querySelector(".app__nav-scroll");
    const isRow = el => el.hasAttribute("data-group") || el.hasAttribute("data-item");
    const keyOf = row => row.getAttribute("data-group") || row.getAttribute("data-item");
    const rowsIn = list => Array.from(list.children).filter(isRow);
    const itemsOf = group => group.querySelector(":scope > .nav-group__items");

    // Same format SidebarOrder.Parse reads: groups|group:items|group:items
    function serialize() {
        const groups = rowsIn(scroller());
        return [groups.map(keyOf).join(",")]
            .concat(groups.map(g => keyOf(g) + ":" + rowsIn(itemsOf(g)).map(keyOf).join(",")))
            .join("|");
    }

    function save(value) {
        document.cookie = value
            ? `${COOKIE}=${encodeURIComponent(value)}; path=/; max-age=${COOKIE_MAX_AGE}; SameSite=Lax`
            : `${COOKIE}=; path=/; max-age=0; SameSite=Lax`;
    }

    // Puts a list's rows in the order `keys` names; rows it does not name keep
    // their relative order after them. appendChild moves, so Home - which is
    // not a row - stays first.
    function sortList(list, keys) {
        const rank = k => { const i = keys.indexOf(k); return i < 0 ? keys.length : i; };
        rowsIn(list)
            .map((row, i) => ({ row, i }))
            .sort((a, b) => rank(keyOf(a.row)) - rank(keyOf(b.row)) || a.i - b.i)
            .forEach(x => list.appendChild(x.row));
    }

    function applyOrder(value) {
        const list = scroller();
        const [groups, ...items] = value.split("|");
        sortList(list, groups.split(","));
        items.forEach(part => {
            const [group, keys] = part.split(":");
            const row = rowsIn(list).find(g => keyOf(g) === group);
            if (row) sortList(itemsOf(row), (keys || "").split(","));
        });
        refresh();
    }

    // Swaps the bar's "saved as you go" line for "Order reset. Undo" and back.
    function showReset(on) {
        const n = nav();
        n.querySelector("[data-nav-arrange-saved]").hidden = on;
        n.querySelector("[data-nav-arrange-was-reset]").hidden = !on;
    }

    function reset() {
        beforeReset = serialize();
        applyOrder(scroller().getAttribute("data-default-order") || "");
        save("");
        showReset(true);
    }

    function undo() {
        if (beforeReset === null) return;
        applyOrder(beforeReset);
        save(beforeReset);
        beforeReset = null;
        showReset(false);
        nav().querySelector("[data-nav-arrange-reset]")?.focus();
    }

    // Up is off on the first row of a list and down on the last.
    function refresh() {
        document.querySelectorAll(".app__nav-scroll .nav-arrange").forEach(controls => {
            const siblings = rowsIn(controls.parentElement.parentElement);
            const i = siblings.indexOf(controls.parentElement);
            controls.querySelector("[data-nav-move='-1']").disabled = i <= 0;
            controls.querySelector("[data-nav-move='1']").disabled = i >= siblings.length - 1;
        });
    }

    function enter() {
        const n = nav();
        const source = n && n.querySelector(".nav-arrange-source .nav-arrange");
        if (!source || n.classList.contains("is-arranging")) return;
        scroller().querySelectorAll("[data-group], [data-item]").forEach(row => {
            const controls = source.cloneNode(true);
            const label = row.querySelector(".nav-group__label, .nav-item__label");
            const name = label ? label.textContent.trim() : "";
            if (name) {
                controls.setAttribute("aria-label", name);
                controls.querySelectorAll("[data-name]").forEach(b => {
                    const text = b.getAttribute("data-name").replace("{0}", name);
                    b.setAttribute("aria-label", text);
                    b.title = text;
                });
            }
            // The whole row drags, name included; the grip is only the cue.
            row.setAttribute("draggable", "true");
            // A group's controls go straight after its head, so the keyboard
            // reaches them before the group's own items.
            row.insertBefore(controls, row.hasAttribute("data-group") ? itemsOf(row) : null);
        });
        n.classList.add("is-arranging");
        refresh();
        scroller().querySelector(".nav-arrange button:not([disabled])")?.focus();
    }

    function exit(refocus) {
        const n = nav();
        if (!n) return;
        n.querySelectorAll(".app__nav-scroll .nav-arrange").forEach(c => c.remove());
        n.querySelectorAll(".app__nav-scroll [draggable]").forEach(r => r.removeAttribute("draggable"));
        n.classList.remove("is-arranging");
        beforeReset = null;
        showReset(false);
        if (refocus) n.querySelector("[data-nav-arrange]")?.focus();
    }

    function move(button) {
        const row = button.closest(".nav-arrange").parentElement;
        const siblings = rowsIn(row.parentElement);
        const to = siblings.indexOf(row) + Number(button.getAttribute("data-nav-move"));
        if (to < 0 || to >= siblings.length) return;
        row.parentElement.insertBefore(row, to < siblings.indexOf(row) ? siblings[to] : siblings[to].nextSibling);
        save(serialize());
        refresh();
        // Moving the row took focus with it; give it back, or to the other
        // button once this one has run out of room.
        (button.disabled ? button.parentElement.querySelector("button:not([disabled])") : button)?.focus();
    }

    document.addEventListener("click", function (e) {
        const t = e.target.closest ? e.target : null;
        if (!t) return;
        if (t.closest("[data-nav-arrange]")) enter();
        else if (t.closest("[data-nav-arrange-done]")) exit(true);
        else if (t.closest("[data-nav-arrange-reset]")) reset();
        else if (t.closest("[data-nav-arrange-undo]")) undo();
        else if (t.closest(".app__nav-scroll .nav-arrange [data-nav-move]")) move(t.closest("[data-nav-move]"));
    });

    // Escape leaves the mode from anywhere on the page, unless something else
    // (a dialog, the palette) already took it. Focus goes back to the Arrange
    // button only if it was in the sidebar, whose controls are about to go.
    document.addEventListener("keydown", function (e) {
        const n = nav();
        if (e.key !== "Escape" || e.defaultPrevented || !n || !n.classList.contains("is-arranging")) return;
        exit(n.contains(document.activeElement));
    });

    // Native drag and drop on the whole row. The row moves live under the
    // pointer, so the drop needs no marker of its own.
    document.addEventListener("dragstart", function (e) {
        const row = e.target.closest
            ? e.target.closest(".app__nav.is-arranging [data-group][draggable], .app__nav.is-arranging [data-item][draggable]")
            : null;
        if (!row) return;
        dragged = row;
        e.dataTransfer.effectAllowed = "move";
        e.dataTransfer.setData("text/plain", keyOf(dragged)); // Firefox will not start a drag without data
        e.dataTransfer.setDragImage(dragged, 16, 16);
        dragged.classList.add("is-dragging");
    });

    document.addEventListener("dragover", function (e) {
        if (!dragged || !e.target.closest) return;
        let target = e.target.closest("[data-group], [data-item]");
        // A group being dragged is placed against the group under the pointer,
        // even when the pointer is over one of that group's items.
        if (target && dragged.hasAttribute("data-group")) target = target.closest("[data-group]");
        if (!target || target.parentElement !== dragged.parentElement) return;
        e.preventDefault();
        if (target === dragged) return;
        const box = target.getBoundingClientRect();
        target.parentElement.insertBefore(dragged, e.clientY > box.top + box.height / 2 ? target.nextSibling : target);
    });

    document.addEventListener("drop", function (e) {
        if (dragged) e.preventDefault();
    });

    document.addEventListener("dragend", function () {
        if (!dragged) return;
        dragged.classList.remove("is-dragging");
        dragged = null;
        save(serialize());
        refresh();
    });

    // A navigation while arranging (a link in the page body, say) re-renders the
    // nav from the server, which knows nothing of the mode; finish it cleanly,
    // without taking focus from the page that just arrived.
    document.addEventListener("enhancedload", () => exit(false));
})();
