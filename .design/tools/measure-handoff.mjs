// What does the HANDOFF itself render at?
//
// Our tokens.css and .design/handoff/tokens.css agree byte-for-byte on the type
// scale, so the question is not "did we mistranscribe it" but "what root does
// the design project put underneath it". The prototype fragment sets no
// font-size on html or body, so rendering it with just the handoff sheets --
// no base.css -- reproduces the design project's own conditions.
import pkg from '/home/mtaanquist/projects/Codex/node_modules/playwright-core/index.js';
import { readFileSync } from 'node:fs';
const { chromium } = pkg;

const H = '/home/mtaanquist/projects/ALDevToolbox/.design/handoff/';
const SHEETS = ['tokens.css', 'components.css', 'shell.css', 'pages.css', 'pages-forms.css'];
const fragment = readFileSync(H + 'ComponentsPanel.dc.html', 'utf8');

const browser = await chromium.launch({
  executablePath: '/home/mtaanquist/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome',
  headless: true, args: ['--no-sandbox'],
});
const page = await (await browser.newContext({ viewport: { width: 1600, height: 1200 } })).newPage();

const styles = SHEETS.map(s => `<style>${readFileSync(H + s, 'utf8')}</style>`).join('\n');
await page.setContent(`<!doctype html><html data-theme="light"><head>${styles}</head><body>${fragment}</body></html>`);
await page.waitForTimeout(300);

const out = await page.evaluate(() => {
  const probe = document.createElement('div');
  document.body.appendChild(probe);
  const tok = t => { probe.style.fontSize = `var(${t})`; return parseFloat(getComputedStyle(probe).fontSize); };
  const sizes = Object.fromEntries(
    ['--text-2xs', '--text-xs', '--text-sm', '--text-base', '--text-md', '--text-lg', '--text-xl', '--text-2xl', '--text-3xl']
      .map(t => [t, tok(t)]));
  probe.remove();
  const pick = sel => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const cs = getComputedStyle(el);
    return { fontSize: cs.fontSize, height: Math.round(el.getBoundingClientRect().height) };
  };
  return {
    rootFontSize: getComputedStyle(document.documentElement).fontSize,
    bodyFontSize: getComputedStyle(document.body).fontSize,
    sizes,
    pageTitle: pick('.page-head__title, .page__hero-title, h1'),
    tableHead: pick('.data-table thead th'),
    tableCell: pick('.data-table tbody td'),
    btn: pick('.btn'),
  };
});

console.log(`handoff root font-size: ${out.rootFontSize}   body: ${out.bodyFontSize}\n`);
for (const [t, px] of Object.entries(out.sizes)) console.log(`  ${t.padEnd(12)} ${px}px`);
console.log('\ncomponents as the handoff renders them:');
for (const [k, v] of Object.entries({ pageTitle: out.pageTitle, tableHead: out.tableHead, tableCell: out.tableCell, btn: out.btn })) {
  if (v) console.log(`  ${k.padEnd(10)} font ${v.fontSize.padStart(7)}   height ${v.height}px`);
}
await page.screenshot({ path: '.design/tools/handoff-panel.png', fullPage: true });
await browser.close();
