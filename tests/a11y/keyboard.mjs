// Browser checks for behaviour axe can't see: the idle sign-out warning (WCAG 2.2.1) and focus staying on a button that
// disables itself while it works (2.4.3). Run against the same dev server as scan.mjs:
//
//   node keyboard.mjs                # exits 1 on any failure
//
// Options (environment): A11Y_BASE (http://127.0.0.1:5287), A11Y_CHROME (a local Chromium-based browser).
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';

const base = (process.env.A11Y_BASE ?? 'http://127.0.0.1:5287').replace(/\/$/, '');
const failures = [];
const check = (ok, message) => {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${message}`);
  if (!ok) failures.push(message);
};

async function signIn(context, email) {
  const page = await context.newPage();
  await page.goto(`${base}/account/dev-signin`);
  await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/account/dev-signin')), page.click(`button[value="${email}"]`)]);
  await page.close();
}

async function settle(page) {
  await page.waitForLoadState('load');
  await page.waitForFunction(() => window.Blazor && document.querySelector('main'), null, { timeout: 15000 });
  await page.waitForTimeout(1000);
}

const browser = await chromium.launch(process.env.A11Y_CHROME ? { executablePath: process.env.A11Y_CHROME } : {});
const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
await signIn(context, 'dev@workbench.local');

// 2.4.3: a focused button that is disabled while it works gets focus back when it's enabled again; one that stays
// disabled hands focus to the next control instead of leaving it on <body>.
{
  const page = await context.newPage();
  await page.goto(`${base}/sheets`);
  await settle(page);
  const result = await page.evaluate(async () => {
    const wait = ms => new Promise(r => setTimeout(r, ms));
    const host = document.querySelector('main');
    const save = Object.assign(document.createElement('button'), { textContent: 'Save (test)' });
    const next = Object.assign(document.createElement('button'), { textContent: 'Next (test)' });
    host.append(save, next);
    const name = () => document.activeElement === document.body ? 'body' : document.activeElement?.textContent;
    save.focus();
    save.disabled = true;
    await wait(300);
    const during = name();
    save.disabled = false;
    await wait(50);
    const restored = name();
    save.focus();
    save.disabled = true;
    await wait(2500);
    const stuck = name();
    save.remove();
    next.remove();
    return { during, restored, stuck };
  });
  check(result.restored === 'Save (test)', `focus returns to a re-enabled busy button (while busy: ${result.during}; after: ${result.restored})`);
  check(result.stuck === 'Next (test)', `focus moves to the next control when the button stays disabled (got ${result.stuck})`);
  await page.close();
}

// 2.2.1: two minutes before the idle sign-out a dialog offers "Stay signed in"; Escape or the button keeps the session.
for (const theme of ['light', 'dark']) {
  const page = await context.newPage();
  await page.clock.install();
  await page.goto(`${base}/sheets`);
  await settle(page);
  if (theme === 'dark') {
    await page.locator('button[aria-label="Switch to dark theme"], button[aria-label="Toggle dark mode"]').first().click();
  }

  const idleSeconds = Number(await page.getAttribute('meta[name="wb-session"]', 'data-idle-seconds'));
  check(idleSeconds > 0, `${theme}: the page carries the idle timeout (${idleSeconds}s)`);
  // Pretend the user has been away until 90 seconds before the sign-out.
  await page.clock.fastForward((idleSeconds - 90) * 1000);
  await page.clock.runFor(1500);
  const dialog = page.locator('dialog.wb-session-dialog[open]');
  check(await dialog.count() === 1, `${theme}: the idle warning opens before the sign-out`);
  if (await dialog.count() === 1) {
    const focused = await page.evaluate(() => document.activeElement?.textContent?.trim());
    check(focused === 'Stay signed in', `${theme}: focus starts on "Stay signed in" (got ${focused})`);
    check(/signed out in 1:2\d minutes/.test(await dialog.textContent()), `${theme}: the warning says how long is left`);
    const axe = await new AxeBuilder({ page }).include('dialog.wb-session-dialog').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
    const blocking = axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical');
    check(blocking.length === 0, `${theme}: axe finds no serious or critical issues in the warning${blocking.length ? ': ' + blocking.map(v => v.id).join(', ') : ''}`);

    // Tab stays inside the dialog; Enter on "Stay signed in" closes it and pings the session.
    await page.keyboard.press('Tab');
    await page.keyboard.press('Tab');
    const inside = await page.evaluate(() => !!document.activeElement?.closest('dialog'));
    check(inside, `${theme}: Tab stays inside the warning`);
    await page.focus('dialog.wb-session-dialog button[data-action="stay"]');
    const ping = page.waitForRequest(r => r.url().includes('/account/session/ping'), { timeout: 5000 }).catch(() => null);
    await page.keyboard.press('Enter');
    check(await ping !== null, `${theme}: "Stay signed in" pings the session`);
    await page.clock.runFor(1500);
    check(await dialog.count() === 0, `${theme}: the warning closes`);

    // Escape answers it too.
    await page.clock.fastForward((idleSeconds - 90) * 1000);
    await page.clock.runFor(1500);
    await page.keyboard.press('Escape');
    await page.clock.runFor(1500);
    check(await dialog.count() === 0 && !page.url().includes('/account/'), `${theme}: Escape keeps the session`);
  }

  await page.close();
}

// Nobody answers: the page signs out at the deadline (its own context, so the other checks keep their session).
{
  const away = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await signIn(away, 'viewer@example.com');
  const page = await away.newPage();
  await page.clock.install();
  await page.goto(`${base}/sheets`);
  await settle(page);
  const idleSeconds = Number(await page.getAttribute('meta[name="wb-session"]', 'data-idle-seconds'));
  const signedOut = page.waitForURL(u => u.pathname.startsWith('/account/'), { timeout: 15000 }).then(() => true, () => false);
  await page.clock.fastForward((idleSeconds + 1) * 1000);
  await page.clock.runFor(1500);
  check(await signedOut, 'an unanswered warning signs out at the deadline');
  await away.close();
}

await browser.close();
console.log(failures.length ? `${failures.length} failed` : 'all passed');
process.exit(failures.length ? 1 : 0);
