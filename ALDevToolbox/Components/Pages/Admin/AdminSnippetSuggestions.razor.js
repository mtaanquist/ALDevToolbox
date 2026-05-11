// Scroll the suggestion identified by `id` into view. Used by the Review
// links on /admin/snippets, which navigate here with ?focus={id}. The Razor
// page already attaches a `data-suggestion-id` attribute on each row so the
// lookup survives DOM restructuring.
export function focusSuggestion(id) {
    const el = document.querySelector(`[data-suggestion-id="${id}"]`);
    if (el) {
        el.scrollIntoView({ behavior: "smooth", block: "start" });
    }
}
