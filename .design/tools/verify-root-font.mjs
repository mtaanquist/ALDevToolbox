// Do the design system's rem-based type tokens resolve to the sizes the
// handoff documents?
//
// tokens.css:350 says the type scale is "rem against a 16px root". base.css
// sets `html, body { font-size: 14px }`. rem resolves against the ROOT, so if
// that is 14px every one of them lands at 87.5% of its documented value --
// on every page, migrated or not.
import pkg from '/home/mtaanquist/projects/Codex/node_modules/playwright-core/index.js';
const { chromium } = pkg;

const WANT = {
  '--text-2xs': 11, '--text-xs': 12, '--text-sm': 13, '--text-base': 14,
  '--text-md': 16, '--text-lg': 18, '--text-xl': 22, '--text-2xl': 28,
  '--text-3xl': 36,
};

const browser = await chromium.launch({
  executablePath: '/home/mtaanquist/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome',
  headless: true, args: ['--no-sandbox'],
});
const page = await (await browser.newContext()).newPage();
await page.goto('http://localhost:5246/login', { waitUntil: 'networkidle' });
await page.fill('#lg-email', 'hello@taaken.dk');
await page.fill('#lg-password', 'Passw0rd!');
await page.click('button[type=submit]');
await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 15000 });
await page.goto('http://localhost:5246/admin/templates', { waitUntil: 'networkidle' });

const out = await page.evaluate(want => {
  const root = getComputedStyle(document.documentElement);
  // Resolve each token by letting the browser do it: set it as a font-size on
  // a probe element and read back the used pixel value.
  const probe = document.createElement('div');
  document.body.appendChild(probe);
  const sizes = {};
  for (const t of Object.keys(want)) {
    probe.style.fontSize = `var(${t})`;
    sizes[t] = parseFloat(getComputedStyle(probe).fontSize);
  }
  probe.remove();
  const title = document.querySelector('h1');
  return {
    rootFontSize: root.fontSize,
    bodyFontSize: getComputedStyle(document.body).fontSize,
    sizes,
    pageTitle: title ? getComputedStyle(title).fontSize : null,
  };
}, WANT);

console.log(`html font-size: ${out.rootFontSize}   body: ${out.bodyFontSize}`);
console.log(`h1 (design --text-xl, documented 22px): ${out.pageTitle}\n`);
let bad = 0;
for (const [t, want] of Object.entries(WANT)) {
  const got = out.sizes[t];
  const ok = Math.abs(got - want) < 0.51;
  if (!ok) bad++;
  console.log(`  ${t.padEnd(12)} documented ${String(want).padStart(3)}px   actual ${String(got).padStart(6)}px  ${ok ? '' : `  (${Math.round((got / want) * 100)}%)`}`);
}
console.log(bad ? `\n!! ${bad}/${Object.keys(WANT).length} type tokens do not resolve to their documented size` : '\nall type tokens resolve correctly');
await browser.close();
