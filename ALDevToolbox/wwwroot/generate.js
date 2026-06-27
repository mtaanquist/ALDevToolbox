// Loading-state plumbing for forms that POST to a download endpoint.
//
// The Generate forms in /templates/workspace and /templates/extension submit natively
// (the response is a ZIP stream) so the page itself never navigates. To give
// the user feedback that something is happening, we add .btn--loading to the
// flagged submit button on submit and clear it once the response finishes.
//
// We detect the response via a server-set "aldt-gen" cookie whose value is
// echoed back from a hidden input the form already posts. The poll falls back
// to a generous safety timer so the spinner can't get stuck even if the
// browser blocks the cookie.
//
// On a successful generation we also send the user on to
// /docs/extensions-whats-next — a short walkthrough of opening the project,
// putting it under source control, and pushing it to a remote. Validation
// errors take a different path: the server replies with 400 + plain text,
// which the browser renders as a new page, so the JS below doesn't run in
// that case and the user stays on the error response.

(function () {
    const COOKIE_NAME = "aldt-gen";

    function readCookie(name) {
        const match = document.cookie.match(new RegExp("(?:^|; )" + name + "=([^;]*)"));
        return match ? decodeURIComponent(match[1]) : null;
    }

    function clearCookie(name) {
        document.cookie = name + "=; Path=/; Max-Age=0";
    }

    function attachToForm(form) {
        if (form.dataset.loadingFormBound === "1") return;
        form.dataset.loadingFormBound = "1";
        form.addEventListener("submit", function () {
            const btn = form.querySelector("[data-loading-button]");
            if (!btn) return;

            // Stamp a fresh token into the hidden input so the server's
            // response cookie tells us *this* submission finished.
            const tokenInput = form.querySelector("input[name='GenToken']");
            const token = String(Date.now()) + "-" + Math.random().toString(36).slice(2, 10);
            if (tokenInput) tokenInput.value = token;
            clearCookie(COOKIE_NAME);

            btn.classList.add("btn--loading");
            btn.setAttribute("aria-busy", "true");
            // Disable on the next tick so the in-flight POST isn't aborted.
            setTimeout(function () { btn.disabled = true; }, 0);

            const start = Date.now();
            const timer = setInterval(function () {
                const seen = readCookie(COOKIE_NAME);
                const timedOut = Date.now() - start > 30000;
                if ((seen && seen === token) || timedOut) {
                    clearInterval(timer);
                    clearCookie(COOKIE_NAME);
                    btn.classList.remove("btn--loading");
                    btn.removeAttribute("aria-busy");
                    btn.disabled = false;
                    // Only navigate when the server confirmed the
                    // submission completed (cookie matched our token).
                    // A safety-timer timeout leaves the user where they
                    // are so they can retry without losing form state.
                    if (seen && seen === token) {
                        window.location.href = "/docs/extensions-whats-next";
                    }
                }
            }, 250);
        });
    }

    function scan() {
        document.querySelectorAll("form[data-loading-form]").forEach(attachToForm);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", scan, { once: true });
    } else {
        scan();
    }
    document.addEventListener("enhancedload", scan);

    // Interactive Blazor pages can re-render the form without firing
    // enhancedload. A targeted observer rebinds whenever a new form appears.
    const observer = new MutationObserver(function (mutations) {
        for (const m of mutations) {
            for (const node of m.addedNodes) {
                if (node.nodeType !== 1) continue;
                if (node.matches?.("form[data-loading-form]") || node.querySelector?.("form[data-loading-form]")) {
                    scan();
                    return;
                }
            }
        }
    });
    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    } else {
        document.addEventListener("DOMContentLoaded", function () {
            observer.observe(document.body, { childList: true, subtree: true });
        }, { once: true });
    }
})();
