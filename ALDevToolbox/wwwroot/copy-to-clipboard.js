// Copy-to-clipboard for two kinds of control, on one delegated listener:
//
//   [data-copy-url]    - copies the URL it holds, resolved against the current
//                        page so a relative path becomes absolute.
//   [data-copy-target] - copies the text of the element its CSS selector names.
//                        This is the design system's `.code-block` affordance:
//                        the pages carrying code snippets (/docs/mcp,
//                        /docs/extensions-whats-next) are static SSR, and making
//                        a whole page interactive to run four lines of clipboard
//                        code is the wrong trade.
//
// Both show a brief "Copied" swap on the control's [data-copy-label] (or the
// control itself) and add `.is-copied`, which the design system styles.
//
// One delegated document listener serves every such control, so components stay
// interop-free. Loaded once in the shell (App.razor) like the other behaviour
// scripts, so it survives Blazor's enhanced navigation (which doesn't execute
// <script> tags that arrive in a page response).

(function () {
    // Long enough to read, short enough that the button is back to "Copy"
    // before anyone wants to press it again.
    const RESET_MS = 1200;

    function flash(el) {
        const label = el.querySelector("[data-copy-label]") || el;
        // Remember the pristine label the first time only: a second click while
        // the swap is still showing would otherwise capture "Copied" as the text
        // to restore, and the button would read "Copied" forever. Cancelling the
        // pending timer keeps the last click in charge of the reset.
        if (el._copyTimer) clearTimeout(el._copyTimer);
        if (el._copyLabel === undefined) el._copyLabel = label.textContent;
        label.textContent = "Copied";
        el.classList.add("is-copied");
        el._copyTimer = setTimeout(function () {
            label.textContent = el._copyLabel;
            el.classList.remove("is-copied");
            el._copyTimer = null;
        }, RESET_MS);
    }

    document.addEventListener("click", async function (e) {
        const el = e.target.closest("[data-copy-url], [data-copy-target]");
        if (!el) return;
        e.preventDefault();

        let text;
        const raw = el.getAttribute("data-copy-url");
        if (raw) {
            try {
                text = new URL(raw, location.href).href;
            } catch (err) {
                return;
            }
        } else {
            const selector = el.getAttribute("data-copy-target");
            if (!selector) return;
            const source = document.querySelector(selector);
            if (!source) return;
            // innerText, not textContent: the snippets are syntax-highlighted
            // with <span> elements, and textContent would run the lines together
            // wherever the markup wraps.
            text = source.innerText;
        }

        try {
            await navigator.clipboard.writeText(text);
        } catch (err) {
            return; // clipboard blocked (insecure context, denied permission) - fail quietly
        }

        flash(el);
    });
})();
