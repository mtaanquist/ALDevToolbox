// Theme toggle wiring. Pure browser-side; no Blazor JS interop required.
//
// Reads/writes "aldt-theme" in localStorage with one of:
//   - "light"  → force light, set <html data-theme="light">
//   - "dark"   → force dark, set <html data-theme="dark">
//   - missing  → follow OS preference, no data-theme attribute
//
// The synchronous FOUC-prevention script lives inline in App.razor so the
// attribute is set before app.css loads. This file handles click delegation
// and active-button sync, including after Blazor enhanced navigation.

(function () {
    const STORAGE_KEY = "aldt-theme";

    function currentTheme() {
        const stored = localStorage.getItem(STORAGE_KEY);
        return stored === "light" || stored === "dark" ? stored : "system";
    }

    function applyTheme(theme) {
        if (theme === "light" || theme === "dark") {
            document.documentElement.setAttribute("data-theme", theme);
        } else {
            document.documentElement.removeAttribute("data-theme");
        }
    }

    function syncButtons() {
        const value = currentTheme();
        document.querySelectorAll("[data-theme-button]").forEach((btn) => {
            const isActive = btn.getAttribute("data-theme-button") === value;
            btn.classList.toggle("theme-toggle__btn--active", isActive);
            btn.setAttribute("aria-pressed", isActive ? "true" : "false");
        });
    }

    function setTheme(theme) {
        if (theme === "system") {
            localStorage.removeItem(STORAGE_KEY);
        } else {
            localStorage.setItem(STORAGE_KEY, theme);
        }
        applyTheme(theme);
        syncButtons();
    }

    // Click delegation survives Blazor enhanced navigation, which can replace
    // the top-bar DOM nodes when a new page renders.
    document.addEventListener("click", (event) => {
        const btn = event.target.closest("[data-theme-button]");
        if (!btn) return;
        const next = btn.getAttribute("data-theme-button");
        if (next === "light" || next === "dark" || next === "system") {
            setTheme(next);
        }
    });

    // Re-sync the active-button highlight after navigation rerenders the bar.
    function syncWhenReady() {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", syncButtons, { once: true });
        } else {
            syncButtons();
        }
    }
    syncWhenReady();
    document.addEventListener("enhancedload", syncButtons);

    // Interactive Blazor navigations (the New Workspace / New Extension pages
    // opt into InteractiveServer) diff the layout subtree without firing
    // enhancedload, so the active class would be wiped on every nav back to
    // them. A targeted MutationObserver re-applies the highlight whenever the
    // toggle reappears in the DOM.
    const observer = new MutationObserver((mutations) => {
        for (const m of mutations) {
            for (const node of m.addedNodes) {
                if (node.nodeType !== 1) continue;
                if (node.matches?.("[data-theme-button]") || node.querySelector?.("[data-theme-button]")) {
                    syncButtons();
                    return;
                }
            }
        }
    });
    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    } else {
        document.addEventListener("DOMContentLoaded", () => {
            observer.observe(document.body, { childList: true, subtree: true });
        }, { once: true });
    }
})();
