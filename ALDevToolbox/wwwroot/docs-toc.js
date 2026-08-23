// Current-position marker for the docs on-page contents (#558).
//
// The design system's `.toc-link` has an `.is-active` state -- a teal left
// keyline and a tinted fill -- and nothing was ever setting it. The docs pages
// (/docs/mcp, /docs/extensions-whats-next) are deliberately static SSR, so this
// is a behaviour script in the shell rather than Blazor interop: making a whole
// page interactive to run twenty lines of this is the wrong trade, and
// /docs/mcp is AllowAnonymous.
//
// No-ops when the markup is absent, and re-scans on enhanced navigation like
// the other shell scripts.
//
// Deliberately NOT an IntersectionObserver over the headings, which is the
// obvious shape and gets two cases wrong. Membership of a band means "a heading
// is on screen", and the question is "which section am I reading" -- so
// scrolling to a section lands its heading ABOVE the band and marks the NEXT
// one, and at scroll top no heading is in the band at all, leaving the contents
// with nothing marked. Reading position directly answers the actual question.

(function () {
    // The read line, measured DOWN FROM THE TOP OF THE SCROLLPORT -- not from
    // the top of the viewport, which is 64px higher (the top bar sits outside
    // main.app__content). getBoundingClientRect() answers in viewport
    // coordinates, so comparing it against a bare constant silently bakes that
    // 64px in and the marker runs a section behind.
    //
    // Clicking a contents entry parks its heading at the top of the scrollport,
    // offset by its own scroll-margin-top (32px on an h2). This has to clear
    // that, so a click marks the section asked for rather than the one above --
    // and stay small, because a heading within READ_LINE of the target is above
    // the line too and wins by being later.
    const READ_LINE = 44;

    let links = [];      // [{ link, target }] in document order
    let scroller = null; // the element that actually scrolls -- see findScroller
    let frame = 0;

    // The app scrolls `main.app__content`, not the window: the top bar sits
    // outside the scrollport. So neither a window scroll listener nor
    // window.scrollY sees anything move, which is a silent failure -- the
    // marker just never updates. Walk up from the content instead.
    function findScroller(el) {
        for (var node = el; node && node !== document.body; node = node.parentElement) {
            var oy = getComputedStyle(node).overflowY;
            if ((oy === "auto" || oy === "scroll") && node.scrollHeight > node.clientHeight + 4) return node;
        }
        return null;
    }

    function paint() {
        frame = 0;
        if (links.length === 0) return;

        // The last heading at or above the read line. Before the first one --
        // i.e. still in the page's intro -- mark the first entry rather than
        // nothing, so the contents never reads as "you are nowhere".
        const origin = scroller ? scroller.getBoundingClientRect().top : 0;
        let current = links[0];
        for (const entry of links) {
            if (entry.target.getBoundingClientRect().top - origin > READ_LINE) break;
            current = entry;
        }

        // At the very bottom, mark the last entry regardless. A short final
        // section never reaches the read line, so without this it would be the
        // one heading no amount of scrolling can select. The cost: where the
        // last two headings share the final screen, clicking the second-to-last
        // marks the last instead -- both are visible, so neither answer is
        // wrong, and reachability matters more than the click matching.
        const box = scroller || document.documentElement;
        const scrolled = scroller ? scroller.scrollTop : window.scrollY;
        const atBottom = (scroller ? scroller.clientHeight : window.innerHeight) + scrolled >= box.scrollHeight - 2;
        if (atBottom) current = links[links.length - 1];

        for (const entry of links) {
            const on = entry === current;
            entry.link.classList.toggle("is-active", on);
            if (on) {
                entry.link.setAttribute("aria-current", "true");
            } else {
                entry.link.removeAttribute("aria-current");
            }
        }
    }

    function schedule() {
        if (frame === 0) frame = requestAnimationFrame(paint);
    }

    function scan() {
        links = [];
        const seen = new Set();
        for (const link of document.querySelectorAll(".docs__toc .toc-link[href^='#']")) {
            const id = decodeURIComponent(link.getAttribute("href").slice(1));
            // A contents entry pointing at nothing is the page's bug, not ours;
            // skip it rather than letting it swallow a position.
            if (!id || seen.has(id)) continue;
            const target = document.getElementById(id);
            if (!target) continue;
            seen.add(id);
            links.push({ link: link, target: target });
        }
        scroller = links.length > 0 ? findScroller(links[0].target) : null;
        // Document order, not contents order -- they normally agree, and when
        // they don't the page is what the reader is scrolling through.
        links.sort(function (a, b) {
            return a.target.compareDocumentPosition(b.target) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1;
        });
        paint();
    }

    // Capture, because `scroll` does not bubble and the scrollport is an
    // element rather than the window. Passive: this only reads geometry, so it
    // must never delay a scroll.
    document.addEventListener("scroll", schedule, { capture: true, passive: true });
    window.addEventListener("resize", schedule, { passive: true });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", scan, { once: true });
    } else {
        scan();
    }
    document.addEventListener("enhancedload", scan);
})();
