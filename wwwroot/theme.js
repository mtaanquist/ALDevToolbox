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

    // Blazor's enhanced navigation diffs the <html> element against the
    // server-rendered response, which has no data-theme attribute (the inline
    // FOUC script in App.razor only runs on cold loads). Without re-applying
    // the attribute on each navigation, the user's choice would visually revert
    // to the OS preference even though localStorage still holds it. refresh()
    // both restores the theme and the active-button highlight.
    function refresh() {
        applyTheme(currentTheme());
        syncButtons();
    }

    function syncWhenReady() {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", refresh, { once: true });
        } else {
            refresh();
        }
    }
    syncWhenReady();
    document.addEventListener("enhancedload", refresh);

    // Interactive Blazor navigations (the New Workspace / New Extension pages
    // opt into InteractiveServer) diff the layout subtree without firing
    // enhancedload. A targeted MutationObserver catches those — re-applying
    // both the data-theme attribute and the active-button highlight whenever
    // the toggle reappears in the DOM.
    const observer = new MutationObserver((mutations) => {
        for (const m of mutations) {
            for (const node of m.addedNodes) {
                if (node.nodeType !== 1) continue;
                if (node.matches?.("[data-theme-button]") || node.querySelector?.("[data-theme-button]")) {
                    refresh();
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
