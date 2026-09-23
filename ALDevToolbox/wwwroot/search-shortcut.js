// F3 puts the cursor in the page's own search box (#902).
//
// A page opts in by marking its box `data-page-search`; the list archetype's
// FilterBar marks its search wrapper `data-page-search-host`, so every
// ListPage box is covered without the page doing anything. The first VISIBLE
// match wins, which is what picks the right box on a page whose tabs each
// carry their own filter. Looked up at keypress time, on the document, so
// enhanced navigation can never leave this holding a stale node - the release
// page's old per-page handler died silently when its selector stopped
// matching, and PageSearchShortcutTests now keeps the markers on every
// search box instead.
//
// F3 does nothing on a page without a box: it is "this page's search", and
// Ctrl+K is "anywhere". See .design/command-palette.md.
(function () {
    var SELECTOR = "[data-page-search], [data-page-search-host] input";

    // The palette hotkey's test: our own dialogs keep the keystroke, and the
    // palette is itself a .modal-layer, so an open palette does too.
    function overlayIsOpen() {
        var layers = document.querySelectorAll(".modal-layer");
        for (var i = 0; i < layers.length; i++) {
            if (!layers[i].hidden) return true;
        }
        return document.querySelector("dialog[open]") !== null;
    }

    document.addEventListener("keydown", function (e) {
        if (e.key !== "F3" || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
        // CodeMirror binds F3 to find-next, in the editor and in its search panel.
        if (e.defaultPrevented) return;
        var active = document.activeElement;
        if (active && active.closest && active.closest(".cm-editor")) return;
        if (overlayIsOpen()) return;

        var boxes = document.querySelectorAll(SELECTOR);
        for (var i = 0; i < boxes.length; i++) {
            var box = boxes[i];
            if (box.offsetParent === null || box.disabled) continue;
            e.preventDefault();
            box.focus();
            if (typeof box.select === "function") box.select();
            return;
        }
    });
})();
