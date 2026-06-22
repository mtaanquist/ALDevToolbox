// Excel-style multi-cell paste for the row-table editors (catalogue,
// application versions). The listener sits on the editor wrapper and catches
// paste events bubbling up from any cell <input data-row data-col>. A plain
// single-value paste falls through to the browser unchanged; only a TSV block
// (one that contains a tab or a newline — i.e. a range copied from Excel or a
// spreadsheet) is intercepted and handed to .NET, which parses it and
// fills/extends the grid starting at the focused cell.
//
// Loaded once at the shell (App.razor) alongside the other aldt helpers; the
// interactive editor components register/unregister against it by element ref.

(function () {
    window.aldt = window.aldt || {};

    window.aldt.registerGridPaste = function (el, dotNetRef) {
        if (!el || el._gridPasteBound) return;
        const handler = function (e) {
            const target = e.target;
            if (!target || target.tagName !== "INPUT" || target.type !== "text") return;
            const row = target.getAttribute("data-row");
            const col = target.getAttribute("data-col");
            if (row === null || col === null) return;
            const clip = e.clipboardData || window.clipboardData;
            if (!clip) return;
            const text = clip.getData("text");
            if (!text) return;
            // Strip trailing newlines a spreadsheet appends, then only
            // intercept genuine multi-cell content. Single values paste
            // natively so the user keeps normal in-cell paste behaviour.
            const trimmed = text.replace(/\r?\n+$/, "");
            if (trimmed.indexOf("\t") === -1 && trimmed.indexOf("\n") === -1) return;
            e.preventDefault();
            dotNetRef.invokeMethodAsync("OnGridPaste", parseInt(row, 10), parseInt(col, 10), text);
        };
        el.addEventListener("paste", handler);
        el._gridPasteBound = handler;
    };

    window.aldt.unregisterGridPaste = function (el) {
        if (!el || !el._gridPasteBound) return;
        el.removeEventListener("paste", el._gridPasteBound);
        delete el._gridPasteBound;
    };
})();
