// Copies the URL held in a clicked control's [data-copy-url] to the clipboard,
// resolved against the current page so a relative path becomes absolute. Shows a
// brief "Copied" swap on the element's [data-copy-label] (or the element itself).
//
// One delegated document listener serves every such control, so components stay
// interop-free. Loaded once in the shell (App.razor) like the other behaviour
// scripts, so it survives Blazor's enhanced navigation (which doesn't execute
// <script> tags that arrive in a page response).

(function () {
    document.addEventListener("click", async function (e) {
        const el = e.target.closest("[data-copy-url]");
        if (!el) return;
        e.preventDefault();

        const raw = el.getAttribute("data-copy-url");
        if (!raw) return;

        let url;
        try {
            url = new URL(raw, location.href).href;
        } catch (err) {
            return;
        }

        try {
            await navigator.clipboard.writeText(url);
        } catch (err) {
            return; // clipboard blocked (insecure context, denied permission) - fail quietly
        }

        const label = el.querySelector("[data-copy-label]") || el;
        const original = label.textContent;
        label.textContent = "Copied";
        el.classList.add("is-copied");
        setTimeout(function () {
            label.textContent = original;
            el.classList.remove("is-copied");
        }, 1200);
    });
})();
