// Behaviour for the design system's `.ra` row-action menus (RowActionsMenu, and
// the kebabs on the Pipelines and Releases browsers).
//
// The system's markup is a `.ra` wrapper holding a trigger and an absolutely
// positioned `.ra__menu`, shown by `.ra.is-open`. This script owns the whole of
// that state:
//   - clicking a `[data-ra-toggle]` opens its menu and closes every other,
//   - close on outside click, on scroll, on resize, and on Escape,
//   - close after picking an entry, so the menu isn't left open behind whatever
//     the entry did (a navigation, or a dialog - and a menu left lit over a
//     modal's scrim is exactly what PR 15a had to fix from the other side).
//
// One set of document-level listeners serves every row, so the Blazor
// components stay interop-free. They are delegated and match by attribute, so
// they cover menus added on later renders without a rescan.
//
// Until PR 15b this was a native <details>, which needed none of the opening
// half. It was only ever half a fallback - everything below except the toggle
// was already required - and `.ra__menu` meant the <details> here and the popup
// in components.css, which collided app-wide (#529).
(function () {
    function close(ra) {
        ra.classList.remove("is-open");
        const toggle = ra.querySelector("[data-ra-toggle]");
        if (toggle) toggle.setAttribute("aria-expanded", "false");
    }

    function closeAll(except) {
        for (const ra of document.querySelectorAll(".ra.is-open")) {
            if (ra !== except) close(ra);
        }
    }

    document.addEventListener("click", function (e) {
        const toggle = e.target.closest && e.target.closest("[data-ra-toggle]");
        if (toggle) {
            const ra = toggle.closest(".ra");
            if (!ra) return;
            const opening = !ra.classList.contains("is-open");
            closeAll(ra);
            ra.classList.toggle("is-open", opening);
            toggle.setAttribute("aria-expanded", opening ? "true" : "false");
            // Otherwise the document-level handler below sees this same click
            // bubble up and closes what we just opened.
            e.stopPropagation();
            return;
        }

        // A click inside the popup is a pick: let it through, then close. The
        // entry may be a link (navigating away) or a button that opens a
        // dialog; either way the menu has served its purpose.
        const inMenu = e.target.closest && e.target.closest(".ra__menu");
        if (inMenu) {
            const ra = inMenu.closest(".ra");
            if (ra) close(ra);
            return;
        }

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
