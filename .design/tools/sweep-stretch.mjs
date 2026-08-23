import pkg from '/home/mtaanquist/projects/Codex/node_modules/playwright-core/index.js';
const { chromium } = pkg;
const routes = ['/', '/piper', '/compare', '/templates', '/templates/workspace', '/templates/extension',
  '/cookbook', '/cookbook/1', '/cookbook/suggest', '/object-explorer', '/projects', '/pipelines',
  '/releases', '/translator', '/account', '/admin', '/admin/templates', '/admin/templates/defaults',
  '/admin/modules', '/admin/catalog', '/admin/cookbook', '/admin/application-versions',
  '/admin/object-explorer', '/admin/translation-memory', '/admin/administration', '/admin/audit',
  '/site-admin/users', '/site-admin/audit', '/site-admin/backup-storage', '/site-admin/connections',
  '/site-admin/workers', '/site-admin/settings'];
const browser = await chromium.launch({ executablePath: '/home/mtaanquist/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome', headless: true, args: ['--no-sandbox'] });
const ctx = await browser.newContext({ storageState: '/tmp/aldt-state.json', viewport: { width: 1500, height: 1000 } });
const page = await ctx.newPage();
let bad = 0;
for (const r of routes) {
  try { await page.goto('http://localhost:5246' + r, { waitUntil: 'networkidle', timeout: 20000 }); }
  catch { console.log(`${r.padEnd(32)} NAV FAIL`); bad++; continue; }
  await page.waitForTimeout(320);
  const m = await page.evaluate(() => {
    const inner = document.querySelector('.app__content-inner');
    const head = document.querySelector('.page-head');
    const fill = document.querySelector('.u-fill');
    const c = document.querySelector('.app__content');
    return {
      head: head ? Math.round(head.getBoundingClientRect().height) : null,
      align: inner ? getComputedStyle(inner).alignContent : null,
      fill: !!fill,
      fillH: fill ? Math.round(fill.getBoundingClientRect().height) : null,
      scrollportH: c ? c.clientHeight : null,
      hOverflow: c ? c.scrollWidth - c.clientWidth : 0,
    };
  });
  const flags = [];
  // A .page-head is a title + sub; anything past ~120px means it was stretched.
  if (m.head !== null && m.head > 120) flags.push(`STRETCHED head=${m.head}`);
  if (m.fill && m.fillH < m.scrollportH - 60) flags.push(`FILL TOO SHORT ${m.fillH}/${m.scrollportH}`);
  if (!m.fill && m.align !== 'start') flags.push(`align=${m.align}`);
  if (m.hOverflow > 1) flags.push(`H-OVERFLOW ${m.hOverflow}`);
  if (flags.length) bad++;
  console.log(`${r.padEnd(32)} head=${String(m.head).padStart(4)} ${m.fill ? 'fill=' + m.fillH : '        '} ${flags.length ? '<< ' + flags.join(' ; ') : 'ok'}`);
}
console.log(`\n${bad} route(s) with findings, ${routes.length} checked`);
await browser.close();
