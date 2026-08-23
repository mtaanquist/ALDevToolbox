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
// putting it under source control, and pushing it to a remote.
//
// Both generator pages validate on the server before they let the POST go, so
// they call window.aldtGenerate.submit(formId) rather than relying on the
// submit listener: a submit the page is about to cancel must not start the
// spinner. form.submit() is deliberate there — it posts without re-firing the
// submit event, so it cannot loop back into the page's own handler. The
// listener stays for any form that opts in with data-loading-form and has no
// interactive validation of its own.
//
// A validation error the page could not catch (an early submit before the
// circuit is live) still reaches the server, which answers with the styled
// error page. The JS below doesn't run in that case and the user lands there.

(function () {
    const COOKIE_NAME = "aldt-gen";

    function readCookie(name) {
        const match = document.cookie.match(new RegExp("(?:^|; )" + name + "=([^;]*)"));
        return match ? decodeURIComponent(match[1]) : null;
    }

    function clearCookie(name) {
        document.cookie = name + "=; Path=/; Max-Age=0";
    }

    // The loading state and the completion poll, split out from the submit
    // listener so an interactive page can start them itself right before it
    // posts the form programmatically.
    function beginSubmission(form) {
        {
            const btn = form.querySelector("[data-loading-button]");
            if (!btn) return;

            function setTreeBusy(scope, busy) {
                // The preview aside is a sibling of the form, not a child, so
                // this looks up from the page rather than within the form.
                const tree = document.querySelector(".tree");
                if (tree) tree.classList.toggle("tree--loading", busy);
            }

            // Stamp a fresh token into the hidden input so the server's
            // response cookie tells us *this* submission finished.
            const tokenInput = form.querySelector("input[name='GenToken']");
            const token = String(Date.now()) + "-" + Math.random().toString(36).slice(2, 10);
            if (tokenInput) tokenInput.value = token;
            clearCookie(COOKIE_NAME);

            btn.classList.add("btn--loading");
            btn.setAttribute("aria-busy", "true");
            // The design system dims the preview tree while a generate runs
            // (PageGenerator.dc.html: `treeClass: s.busy ? 'tree--loading' : ''`).
            // The rule was ported and never applied - #546. It belongs here and
            // not on a keystroke: the tree is already correct while you type,
            // and dimming it then would say the opposite.
            setTreeBusy(form, true);
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
                    setTreeBusy(form, false);
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
        }
    }

    function attachToForm(form) {
        if (form.dataset.loadingFormBound === "1") return;
        form.dataset.loadingFormBound = "1";
        form.addEventListener("submit", function () { beginSubmission(form); });
    }

    // Entry point for a page that validates before posting. Starts the same
    // loading state the listener would, then submits natively.
    window.aldtGenerate = {
        submit: function (formId) {
            const form = document.getElementById(formId);
            if (!form) return;
            beginSubmission(form);
            form.submit();
        }
    };

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
