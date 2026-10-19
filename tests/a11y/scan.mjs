// axe-core scan of every Workbench page: each seeded role, light and dark theme, desktop and phone width.
//
//   cd tests/a11y && npm ci && npx playwright install chromium
//   ASPNETCORE_URLS=http://127.0.0.1:5287 dotnet run --project src/Workbench.Web --no-launch-profile   (Development, fresh DB)
//   node scan.mjs                    # full matrix; exits 1 on any serious or critical violation
//
// Options (environment): A11Y_BASE (http://127.0.0.1:5287), A11Y_ROLES (comma-separated emails), A11Y_THEMES
// (light,dark), A11Y_WIDTHS (1280,375), A11Y_PAGES (comma-separated paths instead of the crawl), A11Y_CONCURRENCY (1),
// A11Y_OUT (report folder, default ./report), A11Y_CHROME (a local Chromium-based browser instead of Playwright's). The report is report/axe.json plus report/summary.md.
import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import fs from 'node:fs/promises';
import path from 'node:path';

const base = (process.env.A11Y_BASE ?? 'http://127.0.0.1:5287').replace(/\/$/, '');
const roles = (process.env.A11Y_ROLES ?? 'dev@workbench.local,pm@example.com,analyst@example.com,viewer@example.com').split(',');
const themes = (process.env.A11Y_THEMES ?? 'light,dark').split(',');
const widths = (process.env.A11Y_WIDTHS ?? '1280,375').split(',').map(Number);
const concurrency = Number(process.env.A11Y_CONCURRENCY ?? 1); // the server, not the browser, is the bottleneck
const outDir = process.env.A11Y_OUT ?? path.join(path.dirname(new URL(import.meta.url).pathname), 'report');
const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

// Pages without route parameters. Detail pages ({id}) are found by following links from these.
const staticPages = [
  '/', '/accounting', '/accounting/accounts', '/accounting/close', '/accounting/incurred-cost', '/accounting/indirect-rates',
  '/accounting/journal', '/accounting/journal/new', '/accounting/posting-rules', '/accounting/reports', '/accounting/revenue',
  '/accounting/revenue/estimates', '/accounting/revenue/reports', '/accounting/revenue/setup', '/admin/users', '/approvals',
  '/assets', '/assets/capitalize', '/assets/depreciation', '/assets/reports', '/billing', '/billing/accrual', '/billing/aging',
  '/billing/contracts', '/billing/invoices', '/billing/new', '/billing/payments', '/cash', '/cash/forecast', '/cash/import',
  '/cash/reconcile', '/cash/reconciliations', '/dashboards', '/expenses', '/leave', '/m', '/m/approvals', '/m/expenses',
  '/m/leave', '/m/timesheet', '/m/today', '/not-found', '/notifications', '/odcs', '/odcs/approvals', '/payroll', '/projects',
  '/purchasing', '/purchasing/approvals', '/purchasing/bills', '/purchasing/bills/new', '/purchasing/match-exceptions',
  '/purchasing/orders', '/purchasing/payments', '/purchasing/receiving', '/purchasing/reports', '/purchasing/requisitions',
  '/purchasing/requisitions/new', '/purchasing/subcontracts', '/purchasing/vendors', '/reports', '/reports/writer',
  '/resourcing', '/setup', '/setup/approval-workflows', '/setup/audit', '/setup/contracts', '/setup/contracts/new',
  '/setup/customers', '/setup/fiscal-calendar', '/setup/import', '/setup/labor-categories', '/setup/org-units',
  '/setup/organization', '/setup/people', '/setup/per-diem', '/setup/projects', '/setup/projects/new', '/sheets', '/time',
  '/time/approvals', '/time/team',
];

// One example of each parameterised route, taken from the first matching link the crawl sees.
const g = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const detailPatterns = [
  `/accounting/incurred-cost/${g}`, `/accounting/journal/${g}`, `/accounting/revenue/projects/${g}`, `/assets/${g}`,
  `/billing/contracts/${g}`, `/billing/invoices/${g}`, `/billing/invoices/${g}/print`, `/billing/statements/${g}`,
  `/cash/reconcile/${g}`, `/dashboards/${g}`, `/expenses/${g}`, `/m/expenses/${g}`, `/payroll/runs/${g}`, `/projects/${g}`,
  `/projects/${g}/odcs`, `/projects/${g}/(plan|team|time|costs|budget|financials|evm|schedule|work|files)`,
  `/purchasing/bills/${g}`, `/purchasing/orders/${g}`, `/purchasing/orders/${g}/print`, `/purchasing/payments/${g}`,
  `/purchasing/requisitions/${g}`, `/purchasing/subcontracts/${g}`, `/purchasing/vendors/${g}`, `/reports/standard/[a-z0-9-]+`,
  `/reports/saved/${g}`, `/setup/approval-workflows/[A-Za-z]+`, `/setup/contracts/${g}`, `/setup/import/history/${g}`,
  `/setup/projects/${g}`, `/sheets/${g}`, `/time/history/${g}`,
].map(p => new RegExp(`^${p}$`));

async function signIn(context, email) {
  const page = await context.newPage();
  await page.goto(`${base}/account/dev-signin`);
  await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/account/dev-signin')), page.click(`button[value="${email}"]`)]);
  if (new URL(page.url()).pathname.startsWith('/account/')) throw new Error(`Sign-in as ${email} was refused: ${page.url()}`);
  await page.close();
}

// Blazor Server prerenders, then the circuit renders again; wait for that to settle.
async function settle(page) {
  await page.waitForLoadState('load');
  await page.waitForTimeout(400);
  await page.waitForFunction(() => !document.querySelector('.mud-progress-linear-indeterminate, .mud-skeleton'), null, { timeout: 8000 }).catch(() => {});
  await page.waitForTimeout(600);
}

async function setTheme(page, theme) {
  if (theme !== 'dark') return true;
  const toggle = page.locator('button[aria-label="Switch to dark theme"], button[aria-label="Toggle dark mode"]');
  if (await toggle.count() === 0) return false; // the phone pages (/m) and print layouts are light only
  await toggle.first().click();
  await page.waitForTimeout(400);
  return true;
}

async function discover(context) {
  const found = new Map();
  const page = await context.newPage();
  for (const p of staticPages) {
    if (detailPatterns.every(r => found.has(r.source))) break;
    try {
      await page.goto(base + p);
      await settle(page);
      const hrefs = await page.$$eval('a[href]', as => as.map(a => new URL(a.href).pathname));
      for (const h of hrefs) {
        for (const r of detailPatterns) if (!found.has(r.source) && r.test(h)) found.set(r.source, h);
      }
    } catch { /* a page that fails to load is reported by the scan itself */ }
  }
  await page.close();
  return [...found.values()];
}

async function pool(items, n, fn) {
  const queue = [...items];
  await Promise.all(Array.from({ length: n }, async () => {
    while (queue.length) await fn(queue.shift());
  }));
}

async function writeReport() {
  await fs.mkdir(outDir, { recursive: true });
  await fs.writeFile(path.join(outDir, 'axe.json'), JSON.stringify({ base, roles, themes, widths, results, skipped }, null, 2));

  // Summary: one row per rule, with the pages and selectors it hit.
  const byRule = new Map();
  for (const r of results) for (const v of r.violations) {
    const e = byRule.get(v.id) ?? { id: v.id, impact: v.impact, help: v.help, scans: 0, pages: new Set(), targets: new Set() };
    e.scans++; e.pages.add(r.page);
    for (const n of v.nodes) e.targets.add(`${r.page} :: ${n.target}`);
    byRule.set(v.id, e);
  }
  const rank = { critical: 0, serious: 1, moderate: 2, minor: 3 };
  const rules = [...byRule.values()].sort((a, b) => rank[a.impact] - rank[b.impact] || b.scans - a.scans);
  const errors = results.filter(r => r.error);
  const lines = [
    `# axe-core scan`, '', `${results.length} scans (${roles.length} roles × ${themes.join('/')} × ${widths.join('/')}px), ${skipped.length} skipped (light-only layouts), ${errors.length} failed to load.`, '',
    '| Rule | Impact | Scans | Pages |', '|---|---|---|---|',
    ...rules.map(e => `| ${e.id} | ${e.impact} | ${e.scans} | ${[...e.pages].slice(0, 8).join(', ')}${e.pages.size > 8 ? ` +${e.pages.size - 8}` : ''} |`),
    '', '## Targets', '',
    ...rules.flatMap(e => [`### ${e.id} (${e.impact}) — ${e.help}`, ...[...e.targets].slice(0, 60).map(t => `- \`${t}\``), '']),
    ...(errors.length ? ['## Load failures', ...errors.map(e => `- ${e.role} ${e.width} ${e.theme} ${e.page}: ${e.error}`)] : []),
  ];
  await fs.writeFile(path.join(outDir, 'summary.md'), lines.join('\n'));
  const blocking = rules.filter(e => e.impact === 'serious' || e.impact === 'critical');
  console.log(`${results.length} scans; ${rules.length} rules violated; ${blocking.length} serious/critical. Report: ${outDir}`);
  for (const e of rules) console.log(`  ${e.impact.padEnd(8)} ${e.id} (${e.scans} scans, ${e.pages.size} pages)`);
  return blocking;
}

const browser = await chromium.launch(process.env.A11Y_CHROME ? { executablePath: process.env.A11Y_CHROME } : {});
const results = [];
const skipped = [];
for (const role of roles) {
  // Sign in once per role and share the cookie: the sign-in gate throttles repeated sign-ins.
  const discoveryContext = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await signIn(discoveryContext, role);
  const storageState = await discoveryContext.storageState();
  const pages = process.env.A11Y_PAGES ? process.env.A11Y_PAGES.split(',') : [...staticPages, ...await discover(discoveryContext)];
  await discoveryContext.close();
  console.log(`${role}: ${pages.length} pages`);

  const jobs = [];
  for (const width of widths) for (const theme of themes) for (const p of pages) jobs.push({ width, theme, p });
  const contexts = {};
  for (const width of widths) {
    contexts[width] = [];
    for (let i = 0; i < concurrency; i++) {
      const c = await browser.newContext({ storageState, viewport: { width, height: width < 600 ? 812 : 900 }, isMobile: width < 600, hasTouch: width < 600 });
      contexts[width].push(c);
    }
  }
  let next = 0;
  let done = 0;
  await pool(jobs, concurrency, async ({ width, theme, p }) => {
    if (++done % 50 === 0) console.log(`  ${role}: ${done}/${jobs.length}`);
    const context = contexts[width][next++ % concurrency];
    const page = await context.newPage();
    try {
      // ?site=full keeps the phone width on the page asked for rather than redirecting / to /m.
      const url = base + p + (width < 600 && p === '/' ? '?site=full' : '');
      const response = await page.goto(url);
      await settle(page);
      if (new URL(page.url()).pathname.startsWith('/account/')) throw new Error(`signed out (landed on ${new URL(page.url()).pathname})`);
      if (!await setTheme(page, theme)) { skipped.push({ role, width, theme, page: p, reason: 'no dark theme' }); return; }
      const axe = await new AxeBuilder({ page }).withTags(tags).analyze();
      results.push({ role, width, theme, page: p, finalPath: new URL(page.url()).pathname, status: response?.status(),
        violations: axe.violations.map(v => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.map(n => ({ target: n.target.join(' '), summary: n.failureSummary, html: n.html.slice(0, 200) })) })) });
    } catch (e) {
      results.push({ role, width, theme, page: p, error: String(e).slice(0, 300), violations: [] });
    } finally {
      await page.close();
    }
  });
  for (const list of Object.values(contexts)) for (const c of list) await c.close();
  await writeReport();
}
await browser.close();

const blocking = await writeReport();
process.exit(blocking.length || results.some(r => r.error) ? 1 : 0);
