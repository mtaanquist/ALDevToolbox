// Off-canvas sidebar for the narrow breakpoint.
//
// shell.css hides .app__nav below 700px and shows it again on
// `.app.is-drawer-open`, so the CSS is complete but inert without something to
// set that class. This is the something. Delegated from the document and
// idempotent, so it survives Blazor's enhanced navigation replacing the body
// without needing the layout to become an interactive component.
(function () {
    function app() { return document.querySelector(".app"); }

    document.addEventListener("click", function (e) {
        var toggle = e.target.closest("[data-drawer-toggle]");
        if (toggle) {
            e.preventDefault();
            var a = app();
            if (a) a.classList.toggle("is-drawer-open");
            return;
        }
        // A tap on the page body (or a nav link, which navigates) closes it —
        // an open drawer covers the content it was opened to reach.
        var a = app();
        if (a && a.classList.contains("is-drawer-open") && !e.target.closest(".app__nav")) {
            a.classList.remove("is-drawer-open");
        }
        if (a && e.target.closest(".app__nav .nav-item")) {
            a.classList.remove("is-drawer-open");
        }
    });

    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape") return;
        var a = app();
        if (a) a.classList.remove("is-drawer-open");
    });
})();
