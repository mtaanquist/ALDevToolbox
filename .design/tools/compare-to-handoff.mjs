// Same markup, two stacks: the handoff's sheets alone, and ours as App.razor
// loads them. Answers "how far off are we, and in what" without needing the
// app running, because the difference is a property of the stylesheets.
import pkg from '/home/mtaanquist/projects/Codex/node_modules/playwright-core/index.js';
import { readFileSync } from 'node:fs';
const { chromium } = pkg;

const H = '/home/mtaanquist/projects/ALDevToolbox/.design/handoff/';
const W = '/home/mtaanquist/projects/ALDevToolbox/ALDevToolbox/wwwroot/';

const HANDOFF = ['tokens.css', 'components.css', 'shell.css', 'pages.css', 'pages-forms.css'].map(s => H + s);
const OURS = ['tokens.css', 'components.css', 'shell.css', 'pages.css', 'pages-forms.css',
  'base.css', 'auth.css', 'tools.css', 'admin.css'].map(s => W + s);

const MARKUP = `
<div class="page">
  <h1 class="page-head__title">Page title</h1>
  <div class="card"><div class="card__body">
    <table class="data-table">
      <thead><tr><th>Head cell</th><th>Another</th></tr></thead>
      <tbody><tr><td>Body cell</td><td>Value</td></tr><tr><td>Row two</td><td>Value</td></tr></tbody>
    </table>
  </div></div>
  <button class="btn">Button</button>
  <span class="section-label">Section label</span>
</div>`;

const PROBES = {
  'page title': '.page-head__title',
  'table head': '.data-table thead th',
  'table cell': '.data-table tbody td',
  'button': '.btn',
  'section label': '.section-label',
  'card': '.card',
};

async function measure(page, sheets) {
  const styles = sheets.map(s => `<style>${readFileSync(s, 'utf8')}</style>`).join('\n');
  await page.setContent(`<!doctype html><html data-theme="light"><head>${styles}</head><body class="app"><div class="app__content"><div class="app__content-inner">${MARKUP}</div></div></body></html>`);
  await page.waitForTimeout(200);
  return page.evaluate(probes => {
    const root = parseFloat(getComputedStyle(document.documentElement).fontSize);
    const out = { _root: { font: root + 'px', h: 0 } };
    for (const [name, sel] of Object.entries(probes)) {
      const el = document.querySelector(sel);
      if (!el) continue;
      const cs = getComputedStyle(el);
      out[name] = { font: cs.fontSize, h: Math.round(el.getBoundingClientRect().height) };
    }
    return out;
  }, PROBES);
}

const browser = await chromium.launch({
  executablePath: '/home/mtaanquist/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome',
  headless: true, args: ['--no-sandbox'],
});
const page = await (await browser.newContext({ viewport: { width: 1400, height: 900 } })).newPage();

const hand = await measure(page, HANDOFF);
const ours = await measure(page, OURS);

console.log('component        handoff          ours             delta');
for (const k of Object.keys(hand)) {
  if (!ours[k]) continue;
  const hf = parseFloat(hand[k].font), of = parseFloat(ours[k].font);
  const pct = Math.round((of / hf) * 100);
  const same = hf === of && hand[k].h === ours[k].h;
  console.log(
    `${k.padEnd(16)} ${(hand[k].font + ' / ' + hand[k].h + 'px').padEnd(16)} ` +
    `${(ours[k].font + ' / ' + ours[k].h + 'px').padEnd(16)} ` +
    `${same ? 'match' : `type ${pct}%${hand[k].h !== ours[k].h ? `, height ${ours[k].h - hand[k].h > 0 ? '+' : ''}${ours[k].h - hand[k].h}px` : ', height match'}`}`);
}
await browser.close();
