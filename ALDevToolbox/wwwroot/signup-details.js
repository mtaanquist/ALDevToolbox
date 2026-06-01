// Auto-slug helper for the new-org branch of /signup/details.
//
// On a static-SSR page we can't use IJSRuntime, so this is a plain global
// script (loaded once from App.razor) that delegates a single `input` listener
// off `document`. Delegation means it survives enhanced-navigation DOM swaps and
// no-ops on every page that doesn't render the signup-details org fields.
//
// Behaviour: as the user types the organisation name, fill the short-ID (slug)
// field with a slugified copy — until the user edits the slug themselves, at
// which point we leave it alone. The flag lives on the element's dataset so a
// fresh page render (new element) resets it automatically. The server's
// AccountService.Slugify remains the source of truth; this is convenience only.
(function () {
    function slugify(value) {
        return value
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/-+/g, "-")
            .replace(/^-+|-+$/g, "");
    }

    document.addEventListener("input", function (e) {
        const target = e.target;
        if (!target || !target.id) return;

        if (target.id === "su-org-slug") {
            target.dataset.touched = "1";
            return;
        }

        if (target.id === "su-org-name") {
            const slug = document.getElementById("su-org-slug");
            if (!slug || slug.dataset.touched === "1") return;
            slug.value = slugify(target.value);
        }
    });
})();
