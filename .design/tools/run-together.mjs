// Detector for the Razor whitespace bug, shared by the sweep scripts.
//
// Razor strips the whitespace between an interpolation (or an element) and a
// following @if block, so `@row.DepPublisher <span class="tag">` renders as
// "MicrosoftNot in catalogue". It has shipped twice: "#42in Default" on the
// SiteAdmin audit list (PR 8b) and the dependency publisher on the module
// editor (PR 8c-3).
//
// Reading for it does not work -- the markup looks correct, and a text pattern
// like /[a-z][A-Z]/ drowns in CamelCase brand names (GitHub, DevOps, DeepL).
// So measure instead: take each text node that ends in a non-space and is
// followed by an element, and check whether the text's last glyph touches that
// element's box. Capitalisation, language and vocabulary are irrelevant.
//
// Flex and grid parents are skipped: there each run of text is its own item and
// the gap does the spacing, so adjacency carries no meaning. That is also why
// the durable fix for a label-plus-tag pair is a flex row with a gap rather
// than remembering to write <text> </text>.
//
// Verify it still fires before trusting a clean run -- see selfTest below.
// Syntax-highlighted code is excluded: a token stream is SUPPOSED to run
// "]" straight into "," with no space, and CodeMirror emits exactly that.
export const DETECT = `() => {
  const hits = [];
  for (const host of document.querySelectorAll('.app__content *')) {
    if (host.closest('.cm-editor, .code-block, pre')) continue;
    const d = getComputedStyle(host).display;
    if (d === 'flex' || d === 'grid' || d === 'inline-flex' || d === 'inline-grid') continue;
    for (const n of host.childNodes) {
      if (n.nodeType !== 3 || !/\\S$/.test(n.textContent)) continue;
      const next = n.nextSibling;
      if (!next || next.nodeType !== 1) continue;
      if (getComputedStyle(next).display === 'none') continue;
      const r = document.createRange(); r.selectNodeContents(n);
      const end = r.getBoundingClientRect(), box = next.getBoundingClientRect();
      if (!end.width || !box.width) continue;
      if (box.left - end.right < 1 && Math.abs(box.top - end.top) < 12) {
        hits.push(n.textContent.trim().slice(-20) + ' ][ ' + next.textContent.trim().slice(0, 20));
      }
    }
  }
  return hits.slice(0, 5);
}`;

export const detect = page => page.evaluate('(' + DETECT + ')()');

/// Plants the bug in a live page and confirms the detector reports it, then
/// reloads to discard the damage. Call once per sweep: a detector that has
/// never been seen to fire is not evidence of anything.
export async function selfTest(page, hostSelector) {
    const found = await page.evaluate(`(() => {
        const host = document.querySelector(${JSON.stringify(hostSelector)});
        if (!host) return null;
        host.appendChild(document.createTextNode('Microsoft'));
        const tag = document.createElement('span');
        tag.className = 'tag';
        tag.textContent = 'Not in catalogue';
        host.appendChild(tag);
        return (${DETECT})();
    })()`);
    await page.reload({ waitUntil: 'networkidle' });
    return found === null ? 'skipped (no host)' : (found.length ? 'fires' : '!! DETECTOR IS DEAD');
}
