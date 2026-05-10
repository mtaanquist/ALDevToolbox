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
    const COOKIE_KEY = "aldt-theme";
    // One year — this is a cosmetic preference, not a security cookie.
    const COOKIE_MAX_AGE = 60 * 60 * 24 * 365;

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

    // Mirror localStorage to a cookie so server-rendered responses can emit
    // <html data-theme="..."> directly, matching whatever the browser already
    // has applied. Otherwise Blazor's enhanced-nav HTML diff strips the
    // attribute on every navigation because the response wouldn't carry it.
    function writeCookie(theme) {
        const base = `${COOKIE_KEY}=`;
        if (theme === "light" || theme === "dark") {
            document.cookie = `${base}${theme}; path=/; max-age=${COOKIE_MAX_AGE}; SameSite=Lax`;
        } else {
            document.cookie = `${base}; path=/; max-age=0; SameSite=Lax`;
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
        writeCookie(theme);
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

    function refresh() {
        const t = currentTheme();
        // Migration: users who set a theme before the cookie pathway existed
        // have localStorage but no cookie. Write it now so the server's next
        // render produces the matching <html data-theme="...">.
        writeCookie(t);
        applyTheme(t);
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

    // Blazor's enhanced navigation diffs the <html> element against the
    // server-rendered response, which has no data-theme attribute (the inline
    // FOUC script in App.razor only runs on cold loads, not on enhanced
    // navs). The diff strips the attribute on every navigation, even though
    // localStorage still holds the user's choice. enhancedload fires *after*
    // the strip, but interactive Blazor navigation doesn't emit enhancedload
    // at all — the InteractiveServer runtime patches the DOM through its own
    // pipeline. Watching the attribute itself catches both pathways: any
    // outside-our-code change to data-theme triggers a re-apply from
    // localStorage. Re-applying the same value is a no-op so we don't loop.
    const htmlObserver = new MutationObserver(() => {
        const desired = currentTheme();
        const actual = document.documentElement.getAttribute("data-theme");
        if (desired === "system" && actual !== null) {
            document.documentElement.removeAttribute("data-theme");
        } else if ((desired === "light" || desired === "dark") && actual !== desired) {
            document.documentElement.setAttribute("data-theme", desired);
        }
    });
    htmlObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["data-theme"],
    });

    // The toggle row itself can re-mount during InteractiveServer navigation
    // (Blazor patches the layout subtree). When that happens we need to
    // re-sync the active-button highlight against the persisted choice; the
    // theme attribute on <html> is handled by htmlObserver above.
    const toggleObserver = new MutationObserver((mutations) => {
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
        toggleObserver.observe(document.body, { childList: true, subtree: true });
    } else {
        document.addEventListener("DOMContentLoaded", () => {
            toggleObserver.observe(document.body, { childList: true, subtree: true });
        }, { once: true });
    }
})();
