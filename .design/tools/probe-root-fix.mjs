// What would actually change if the root font-size were corrected to 16px?
//
// rem resolves against the ROOT element, not body -- so `html { font-size:
// 16px }` + `body { font-size: 14px }` makes the design tokens land on their
// documented sizes while leaving the inherited default exactly where it is.
// This measures the blast radius before anyone commits to it: every element
// whose used font-size or box changes, per route, migrated and legacy alike.
import pkg from '/home/mtaanquist/projects/Codex/node_modules/playwright-core/index.js';
const { chromium } = pkg;

const FIX = 'html { font-size: 16px !important; } body { font-size: 14px !important; }';

const ROUTES = [
  ['/admin/templates', 'migrated'],
  ['/admin/administration/users', 'migrated'],
  ['/admin/catalog', 'migrated'],
  ['/admin/audit', 'migrated'],
  ['/new-workspace', 'migrated'],
  ['/admin', 'migrated'],
  ['/piper', 'legacy'],
  ['/object-explorer', 'legacy'],
  ['/account', 'legacy'],
  ['/cookbook', 'legacy'],
];

const snapshot = () => {
  const out = [];
  for (const el of document.querySelectorAll('.app__content *')) {
    const cs = getComputedStyle(el);
    const r = el.getBoundingClientRect();
    out.push([cs.fontSize, Math.round(r.width), Math.round(r.height)].join('|'));
  }
  return out;
};

const browser = await chromium.launch({
  executablePath: '/home/mtaanquist/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome',
  headless: true, args: ['--no-sandbox'],
});
const ctx = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
const page = await ctx.newPage();
await page.goto('http://localhost:5246/login', { waitUntil: 'networkidle' });
await page.fill('#lg-email', 'hello@taaken.dk');
await page.fill('#lg-password', 'Passw0rd!');
await page.click('button[type=submit]');
await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 15000 });

for (const [route, kind] of ROUTES) {
  await page.goto('http://localhost:5246' + route, { waitUntil: 'networkidle' });
  await page.mouse.move(4, 4);
  await page.waitForTimeout(400);
  const before = await page.evaluate(snapshot);
  const tag = await page.addStyleTag({ content: FIX });
  await page.waitForTimeout(200);
  const after = await page.evaluate(snapshot);
  await page.evaluate(t => t.remove(), tag);

  let font = 0, box = 0;
  for (let i = 0; i < Math.min(before.length, after.length); i++) {
    if (before[i] === after[i]) continue;
    const [bf, bw, bh] = before[i].split('|'), [af, aw, ah] = after[i].split('|');
    if (bf !== af) font++;
    if (bw !== aw || bh !== ah) box++;
  }
  console.log(`${kind.padEnd(9)} ${route.padEnd(32)} ${String(before.length).padStart(4)} elements   ${String(font).padStart(4)} resize type   ${String(box).padStart(4)} change box`);
}
await browser.close();
