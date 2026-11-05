// Captures the screenshots used by the user and admin guide (docs/assets/screenshots).
//
// Start a fresh, seeded Workbench first (Development, so the demo sign-in page exists):
//   rm -f tools/docs-screenshots/docs.db*      # start from the demo data every time
//   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5210 \
//     ConnectionStrings__Workbench="Data Source=$PWD/tools/docs-screenshots/docs.db" \
//     dotnet run --project src/Workbench.Web --no-launch-profile
// Then, from this folder:
//   npm install && npx playwright install chromium
//   node shoot.mjs                 # every shot
//   node shoot.mjs time expenses   # only shots whose name starts with one of these
//
// WORKBENCH_URL overrides the server address (default http://localhost:5210).

import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import shots from './shots.mjs';

const base = (process.env.WORKBENCH_URL ?? 'http://localhost:5210').replace(/\/$/, '');
const out = join(dirname(fileURLToPath(import.meta.url)), '../../docs/assets/screenshots');
const only = process.argv.slice(2);

await mkdir(out, { recursive: true });
const browser = await chromium.launch();
const contexts = new Map();

// One signed-in browser context per demo user, reused across their shots.
async function contextFor(email) {
  if (contexts.has(email)) return contexts.get(email);
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'light', reducedMotion: 'reduce' });
  const page = await context.newPage();
  await page.goto(`${base}/account/dev-signin`);
  await page.click(`button[value="${email}"]`);
  await page.waitForLoadState('networkidle');
  await page.close();
  contexts.set(email, context);
  return context;
}

async function settle(page, ms = 900, keepTour = false) {
  await page.waitForLoadState('networkidle').catch(() => {});
  await page.waitForTimeout(ms);
  if (keepTour) return;
  // Close a welcome tour or dialog that opened anyway.
  for (const label of ['Skip tour', 'Skip', 'No thanks', 'Not now']) {
    const button = page.getByRole('button', { name: label, exact: true });
    if (await button.count() && await button.first().isVisible().catch(() => false)) {
      await button.first().click().catch(() => {});
      await page.waitForTimeout(300);
    }
  }
}

let failed = 0;
for (const shot of shots) {
  if (only.length && !only.some(prefix => shot.name.startsWith(prefix))) continue;
  const context = await contextFor(shot.user ?? 'dev@workbench.local');
  const page = await context.newPage();
  if (shot.viewport) await page.setViewportSize(shot.viewport);
  try {
    await page.goto(`${base}/${shot.path ?? ''}`);
    await settle(page, 1200, shot.keepTour);
    if (shot.run) {
      await shot.run(page);
      await settle(page, shot.wait ?? 700);
    }
    // Blazor focuses the page heading after navigation; drop the focus ring so it doesn't show in the shot.
    if (!shot.keepFocus) await page.evaluate(() => document.activeElement?.blur?.());
    await page.mouse.move(0, 0);
    const file = join(out, `${shot.name}.png`);
    if (shot.selector) {
      await page.locator(shot.selector).first().screenshot({ path: file });
    } else {
      await page.screenshot({ path: file, fullPage: shot.fullPage ?? false });
    }
    console.log(`ok   ${shot.name}`);
  } catch (error) {
    failed++;
    console.log(`FAIL ${shot.name}: ${error.message.split('\n')[0]}`);
  } finally {
    await page.close();
  }
}

await browser.close();
process.exit(failed ? 1 : 0);
