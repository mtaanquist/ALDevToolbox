// Collapsible sidebar groups.
//
// The sidebar is server-rendered and not interactive, so collapsing a group is
// done here rather than over the circuit — a nav twirl should not cost a round
// trip. Same storage shape as theme.js: localStorage is the record, and it is
// mirrored to a cookie so the server can stamp `is-collapsed` into the response
// itself. Without that mirror the group would render open and then snap shut,
// and Blazor's enhanced-navigation HTML diff would strip the class again on
// every navigation because the response never carried it.
(function () {
    "use strict";

    const KEY = "aldt-nav-collapsed";
    const COOKIE_MAX_AGE = 60 * 60 * 24 * 365;

    function read() {
        try {
            const raw = localStorage.getItem(KEY);
            return new Set(raw ? raw.split(",").filter(Boolean) : []);
        } catch {
            // Private mode, or site data blocked. A sidebar that forgets is a
            // small loss; one that throws on every page is not.
            return new Set();
        }
    }

    function write(keys) {
        const value = Array.from(keys).join(",");
        try {
            if (value) {
                localStorage.setItem(KEY, value);
            } else {
                localStorage.removeItem(KEY);
            }
        } catch {
            /* ignore - the cookie below still carries it for this session */
        }
        document.cookie = `${KEY}=${encodeURIComponent(value)}; path=/; max-age=${COOKIE_MAX_AGE}; SameSite=Lax`;
    }

    function apply() {
        const collapsed = read();
        document.querySelectorAll(".nav-group[data-group]").forEach(function (group) {
            const key = group.getAttribute("data-group");
            const isCollapsed = collapsed.has(key);
            group.classList.toggle("is-collapsed", isCollapsed);
            const head = group.querySelector(".nav-group__head");
            if (head) head.setAttribute("aria-expanded", String(!isCollapsed));
        });
    }

    // Delegated, so it keeps working across enhanced navigations without
    // rebinding per page.
    document.addEventListener("click", function (e) {
        const head = e.target.closest ? e.target.closest(".nav-group__head") : null;
        if (!head) return;
        const group = head.closest(".nav-group[data-group]");
        if (!group) return;

        const key = group.getAttribute("data-group");
        const collapsed = read();
        if (collapsed.has(key)) {
            collapsed.delete(key);
        } else {
            collapsed.add(key);
        }
        write(collapsed);
        apply();
    });

    // The server has usually stamped the right classes already; re-applying is a
    // no-op. This covers the first visit, where there is a localStorage value but
    // no cookie yet, and any navigation that replaced the nav markup.
    apply();
    document.addEventListener("enhancedload", apply);
})();
