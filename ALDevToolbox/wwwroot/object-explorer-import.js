// Progressive enhancement for the Import Release form (/admin/object-explorer/new).
//
// The form is statically rendered and posts a multipart body (up to 1 GB) straight
// to the Kestrel endpoint so the upload never touches the Blazor circuit. This
// module layers three conveniences on top without changing that contract:
//
//   1. Kind-aware fields — Publisher shows for third-party/customer, Customer
//      shows only for customer. Plain server render leaves them all visible, so
//      no-JS submits still work; we just hide the irrelevant ones.
//   2. Upload progress — we re-submit via XHR so upload.onprogress can drive a
//      real percentage bar, then flip to an indeterminate "Ingesting…" state once
//      the bytes are up (the server parses .app files synchronously before it
//      redirects, and there's no byte-level signal for that phase).
//   3. The endpoint redirects on both success and validation error; XHR follows
//      it, so we land the user on xhr.responseURL. Antiforgery failures come back
//      as a 4xx with plain text instead, which we surface inline.
//
// Loaded once at the shell (App.razor) because Blazor's enhanced-nav diff doesn't
// execute <script> tags from page responses; the scan()/observer pattern mirrors
// generate.js so it binds whenever the form appears.

(function () {
    function setFieldVisible(field, visible) {
        if (!field) return;
        field.hidden = !visible;
        // Clear the control when we hide it so a value set under one Kind
        // doesn't silently post after switching to a Kind that doesn't use it.
        if (!visible) {
            const control = field.querySelector("input, select");
            if (control) control.value = "";
        }
    }

    function toggleKindFields(form) {
        const kind = form.querySelector("select[name='Kind']");
        if (!kind) return;
        const value = kind.value;
        const nonFirstParty = value === "third_party" || value === "customer";
        // Parent + publisher only matter for apps that sit on a first-party base;
        // customer name only for customer bundles. First-party shows none of them.
        setFieldVisible(form.querySelector("[data-oe-field-parent]"), nonFirstParty);
        setFieldVisible(form.querySelector("[data-oe-field-publisher]"), nonFirstParty);
        setFieldVisible(form.querySelector("[data-oe-field-customer]"), value === "customer");
    }

    function submitWithProgress(form) {
        const progress = form.querySelector("[data-oe-import-progress]");
        const bar = form.querySelector("[data-oe-import-bar]");
        const label = form.querySelector("[data-oe-import-label]");
        const pct = form.querySelector("[data-oe-import-pct]");
        const hint = form.querySelector("[data-oe-import-hint]");
        const submit = form.querySelector("[data-oe-import-submit]");
        const submitLabel = submit ? submit.querySelector("span") : null;
        const originalSubmitText = submitLabel ? submitLabel.textContent : null;

        const xhr = new XMLHttpRequest();
        xhr.open("POST", form.action);

        if (progress) progress.hidden = false;
        if (submit) {
            submit.disabled = true;
            submit.classList.add("btn--loading");
        }
        if (submitLabel) submitLabel.textContent = "Importing…";
        if (label) label.textContent = "Uploading…";
        if (hint) hint.textContent = "";
        if (bar) bar.value = 0;
        if (pct) pct.textContent = "0%";

        function switchToIngesting() {
            if (label) label.textContent = "Ingesting…";
            if (pct) pct.textContent = "";
            // Removing the value attribute puts <progress> into its
            // indeterminate (animated) state — we have no byte signal for
            // the server-side parse/resolve phase.
            if (bar) bar.removeAttribute("value");
            if (hint) {
                hint.textContent = "Parsing .app files and resolving references. "
                    + "This can take a few minutes for a full DVD — keep this tab open.";
            }
        }

        function fail(message) {
            if (label) label.textContent = "Failed";
            if (hint) hint.textContent = message;
            if (bar) bar.value = 0;
            if (submit) {
                submit.disabled = false;
                submit.classList.remove("btn--loading");
            }
            if (submitLabel && originalSubmitText !== null) submitLabel.textContent = originalSubmitText;
        }

        xhr.upload.addEventListener("progress", function (evt) {
            if (!evt.lengthComputable) return;
            const percent = Math.round((evt.loaded / evt.total) * 100);
            if (bar) bar.value = percent;
            if (pct) pct.textContent = percent + "%";
            if (percent >= 100) switchToIngesting();
        });
        xhr.upload.addEventListener("load", switchToIngesting);

        xhr.addEventListener("load", function () {
            // Success and server-side validation both redirect (XHR follows it,
            // so status is the followed page's 200). A 4xx means no redirect
            // happened — antiforgery rejection returns plain text we can show.
            if (xhr.status >= 400) {
                fail(xhr.responseText || "Import failed (" + xhr.status + "). Reload the form and try again.");
                return;
            }
            window.location.href = xhr.responseURL || form.action;
        });
        xhr.addEventListener("error", function () {
            fail("Upload failed — check your connection and try again.");
        });
        xhr.addEventListener("abort", function () {
            fail("Upload cancelled.");
        });

        xhr.send(new FormData(form));
    }

    function bind(form) {
        if (form.dataset.oeImportBound === "1") return;
        form.dataset.oeImportBound = "1";

        const kind = form.querySelector("select[name='Kind']");
        if (kind) kind.addEventListener("change", function () { toggleKindFields(form); });
        toggleKindFields(form);

        form.addEventListener("submit", function (e) {
            if (typeof XMLHttpRequest === "undefined") return; // fall back to native post
            e.preventDefault();
            submitWithProgress(form);
        });
    }

    function scan() {
        document.querySelectorAll("form[data-oe-import-form]").forEach(bind);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", scan, { once: true });
    } else {
        scan();
    }
    document.addEventListener("enhancedload", scan);

    const observer = new MutationObserver(function (mutations) {
        for (const m of mutations) {
            for (const node of m.addedNodes) {
                if (node.nodeType !== 1) continue;
                if (node.matches?.("form[data-oe-import-form]") || node.querySelector?.("form[data-oe-import-form]")) {
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
