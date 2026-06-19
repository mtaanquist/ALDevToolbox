// Helper for Blazor Server pages that own a search-style text input.
//
// In default InteractiveServer mode, binding `value="@_state"` on a
// text input and updating `_state` synchronously from `@oninput`
// pushes a render diff back to the browser on every keystroke. If the
// user types faster than the round-trip, the diff arrives at the
// client AFTER the next keystroke has already landed in the DOM —
// the diff then sets the input's value attribute to the server's
// stale view, dropping or reordering characters. The visible
// symptom is "sales" becoming "slaes" or losing trailing letters.
//
// The fix is to make the input uncontrolled: omit the `value`
// attribute from the Blazor markup so no render ever resets the DOM
// value, and seed the initial value once via this helper after the
// first render. The DOM is then the source of truth for the input's
// text; the server only listens for `@oninput` events and uses the
// payload to drive search.
//
// Usage from a .razor component:
//
//     <input @ref="_searchInputRef" type="search" @oninput="OnSearch" />
//     ...
//     protected override async Task OnAfterRenderAsync(bool firstRender) {
//         if (firstRender && !string.IsNullOrEmpty(_search)) {
//             await JS.InvokeVoidAsync("aldt.seedInput", _searchInputRef, _search);
//         }
//     }

(function () {
    window.aldt = window.aldt || {};
    window.aldt.seedInput = function (el, value) {
        if (!el || typeof value !== "string") return;
        // Only seed when the input is currently empty — if the user
        // already started typing before the JS interop call landed,
        // their keystrokes win. Stops a slow circuit handshake from
        // overwriting an in-progress query.
        if (el.value && el.value.length > 0) return;
        el.value = value;
    };

    // Unconditionally writes a value to an uncontrolled input. Unlike
    // seedInput (which only fills an empty box), this overwrites whatever
    // the user typed — needed to *clear* a box on a Reset action, where
    // seedInput's empty-guard would otherwise refuse to wipe the text.
    window.aldt.setInputValue = function (el, value) {
        if (!el || typeof value !== "string") return;
        el.value = value;
    };

    // Sets document.title from an interactive component. PageTitle alone
    // doesn't reach the static-SSR HeadOutlet on pages rendered with
    // `prerender: false` (the head has no initial title and the
    // interactive update doesn't propagate back to it), so those pages
    // call this directly once their data has loaded.
    window.aldt.setTitle = function (value) {
        if (typeof value !== "string") return;
        document.title = value;
    };
})();
