// Does the legacy sheet actually beat the design layer for these classes?
//
// collisions.py answers that by parsing, which means guessing at specificity.
// This asks the browser instead: link every sheet in App.razor's order, render
// the markup a migrated page renders, and read the computed value. No app or
// database needed -- the cascade is a property of the sheets, not the server.
import pkg from '/home/mtaanquist/projects/Codex/node_modules/playwright-core/index.js';
import { readFileSync } from 'node:fs';
const { chromium } = pkg;

const WWW = '/home/mtaanquist/projects/ALDevToolbox/ALDevToolbox/wwwroot/';
const ORDER = ['tokens.css', 'components.css', 'shell.css', 'pages.css',
  'pages-forms.css', 'base.css', 'auth.css', 'tools.css', 'admin.css'];

// Each case: the markup, the element to measure, and the properties in dispute.
const CASES = [
  ['.card', '<div class="card" id=t><div class="card__body">x</div></div>',
    ['border-radius', 'box-shadow', 'border-top-width']],
  ['.field', '<div class="page"><div class="field" id=t><label class="field__label">L</label><input class="input"></div></div>',
    ['display', 'gap']],
  ['.data-table th', '<table class="data-table"><thead><tr><th id=t>H</th></tr></thead><tbody><tr><td>c</td></tr></tbody></table>',
    ['background-color', 'font-size', 'padding-top', 'text-transform', 'letter-spacing']],
  ['.data-table td', '<table class="data-table"><thead><tr><th>H</th></tr></thead><tbody><tr><td id=t>c</td></tr></tbody></table>',
    ['padding-top', 'padding-left', 'border-bottom-width', 'height']],
  ['.module-card', '<div class="module-list"><label class="module-card" id=t><span class="module-card__title">T</span><span class="module-card__text">d</span><span class="module-card__check"></span></label></div>',
    ['display', 'gap', 'padding-top', 'padding-right', 'border-radius']],
  ['.audit', '<div class="audit" id=t><div class="audit__row">r</div></div>', ['display', 'flex-direction']],
  ['.section-label', '<p class="section-label" id=t>S</p>', ['font-size', 'font-weight', 'letter-spacing']],
  ['.badge--danger', '<span class="badge badge--danger" id=t>b</span>', ['color']],
  ['.logo-preview', '<div class="logo-preview" id=t>p</div>', ['background-color', 'border-top-width', 'padding-top']],
  ['.ra__menu', '<div class="ra"><div class="ra__menu" id=t>m</div></div>', ['display', 'position', 'z-index']],
  ['.extension-editor__body', '<div class="extension-editor"><div class="extension-editor__body" id=t>b</div></div>',
    ['display', 'gap', 'padding-top']],
  ['.dep-editor__fields', '<div class="dep-editor__fields" id=t>f</div>', ['gap', 'padding-top']],
];

// Render each case twice: with every sheet, and with the design layer only.
// A difference means a legacy sheet is winning.
async function measure(page, sheets) {
  const links = sheets.map(s => `<style>${readFileSync(WWW + s, 'utf8')}</style>`).join('\n');
  const body = CASES.map(([name, html]) => `<section data-case="${name}">${html.replace('id=t', `id="t${CASES.findIndex(c => c[0] === name)}"`)}</section>`).join('\n');
  // Wrap in `.page`, because every migrated page root carries it and the
  // design-layer bridge in base.css is gated on exactly that. Measuring
  // outside it tells you what an UNMIGRATED page gets, which is not the
  // question -- the first version of this probe made that mistake.
  await page.setContent(`<!doctype html><html data-theme="light"><head>${links}</head><body class="app"><div class="app__content"><div class="page">${body}</div></div></body></html>`);
  return page.evaluate(cases => cases.map(([name, , props], i) => {
    const el = document.getElementById('t' + i);
    const cs = getComputedStyle(el);
    return [name, Object.fromEntries(props.map(p => [p, cs.getPropertyValue(p)]))];
  }), CASES.map(([n, h, p]) => [n, h, p]));
}

const browser = await chromium.launch({
  executablePath: '/home/mtaanquist/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome',
  headless: true, args: ['--no-sandbox'],
});
const page = await (await browser.newContext()).newPage();

const all = await measure(page, ORDER);
const design = await measure(page, ORDER.filter(s => !['base.css', 'auth.css', 'tools.css', 'admin.css'].includes(s)));

let n = 0;
for (let i = 0; i < all.length; i++) {
  const [name, got] = all[i], [, want] = design[i];
  const diff = Object.keys(got).filter(p => got[p] !== want[p]);
  if (!diff.length) { console.log(`ok   ${name}`); continue; }
  n++;
  console.log(`!!   ${name}`);
  for (const p of diff) console.log(`       ${p}: design "${want[p]}"  ->  actual "${got[p]}"`);
}
console.log(n ? `\n${n}/${CASES.length} components do not get the design layer's value` : '\nall clean');
await browser.close();
