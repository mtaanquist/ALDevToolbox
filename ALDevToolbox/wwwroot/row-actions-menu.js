// Behaviour for <RowActionsMenu> dropdowns (the Object Explorer row split-button).
//
// The menu markup is native <details class="ra__menu"> so it opens and closes on
// its own. This script adds the polish the design calls for and that <details>
// can't do alone:
//   - only one row menu open at a time,
//   - close on outside click, on scroll, on resize, and on Escape.
//
// One set of document-level listeners serves every row, so the Blazor component
// stays interop-free. Re-running scan() is unnecessary: the listeners are
// delegated and match by class, so they cover menus added on later renders.

(function () {
    function allMenus() {
        return Array.from(document.querySelectorAll("details.ra__menu[open]"));
    }

    function closeAll(except) {
        for (const m of document.querySelectorAll("details.ra__menu[open]")) {
            if (m === except) continue;
            m.removeAttribute("open");
            // Collapse any open submenu so it isn't left open next time.
            const sub = m.querySelector("details.ra__sub[open]");
            if (sub) sub.removeAttribute("open");
        }
    }

    // One-open-at-a-time: when any row menu toggles open, close the others.
    document.addEventListener("toggle", function (e) {
        const t = e.target;
        if (t instanceof HTMLDetailsElement && t.classList.contains("ra__menu") && t.open) {
            closeAll(t);
        }
    }, true);

    document.addEventListener("mousedown", function (e) {
        const open = allMenus();
        if (open.length === 0) return;
        if (e.target.closest && e.target.closest("details.ra__menu")) return;
        closeAll(null);
    });

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape") closeAll(null);
    });

    // Scroll anywhere (capture, so it catches the .content scroll container) or a
    // resize means the absolutely-positioned popup would drift — just close it.
    window.addEventListener("scroll", function () { closeAll(null); }, true);
    window.addEventListener("resize", function () { closeAll(null); });
})();
