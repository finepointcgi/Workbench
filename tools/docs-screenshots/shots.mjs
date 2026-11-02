// Every screenshot in the guide: name (the file under docs/assets/screenshots), the demo user it's taken as, the page,
// and optionally steps to run before the shot (run), a single element to capture (selector) or a full-page capture.

const admin = 'dev@workbench.local';
const employee = 'analyst@example.com';
const pm = 'pm@example.com';
const hr = 'hr@example.com';
const materials = 'mia.materials@example.com';
const bd = 'bd@example.com';
const pricing = 'pricing@example.com';

const openFirst = (text) => async (page) => {
  await page.getByText(text).first().click();
};

const clickTab = (name) => async (page) => {
  await page.getByRole('tab', { name, exact: false }).first().click();
};

export default [
  // Getting started. The tour shot must be the employee's first visit to Home.
  { name: 'getting-started-welcome-tour', user: employee, path: '', keepTour: true },
  { name: 'getting-started-signin', user: employee, path: 'account/signin' },
  { name: 'getting-started-home-employee', user: employee, path: '' },
  { name: 'getting-started-home-pm', user: pm, path: '' },
  { name: 'getting-started-new-menu', user: pm, path: '', run: async (page) => { await page.getByRole('button', { name: /New/ }).first().click(); } },
  { name: 'getting-started-search', user: pm, path: '', run: async (page) => { await page.keyboard.press('Control+k'); await page.keyboard.type('time'); } },
  { name: 'getting-started-help-panel', user: employee, path: 'time', run: async (page) => { await page.getByRole('button', { name: /help/i }).first().click(); } },
  { name: 'getting-started-help-center', user: employee, path: 'help' },
  { name: 'getting-started-glossary', user: employee, path: 'help/glossary' },
  { name: 'getting-started-notifications', user: pm, path: 'notifications' },
  { name: 'getting-started-mobile', user: employee, path: 'm', viewport: { width: 390, height: 844 } },

  // My work.
  { name: 'time-timesheet', user: employee, path: 'time' },
  { name: 'time-team', user: pm, path: 'time/team' },
  { name: 'time-flags', user: pm, path: 'time/flags' },
  { name: 'expenses-list', user: employee, path: 'expenses' },
  { name: 'expenses-report', user: employee, path: 'expenses', run: openFirst('DC program review') },
  { name: 'receipts-library', user: employee, path: 'receipts' },
  { name: 'travel-list', user: employee, path: 'travel' },
  { name: 'leave-balances', user: employee, path: 'leave' },
  { name: 'requisitions-list', user: employee, path: 'purchasing/requisitions' },
  { name: 'approvals-inbox', user: pm, path: 'approvals' },

  // Projects.
  { name: 'projects-list', user: pm, path: 'projects' },
  { name: 'projects-overview', user: pm, path: 'projects', run: openFirst('Enterprise Modernization') },
  { name: 'projects-plan', user: pm, path: 'projects', run: async (page) => { await openFirst('Enterprise Modernization')(page); await page.waitForTimeout(800); await clickTab('Plan')(page); } },
  { name: 'projects-plan-grid', user: pm, path: 'projects', run: async (page) => { await openFirst('Enterprise Modernization')(page); await page.waitForTimeout(800); await clickTab('Plan')(page); await page.waitForTimeout(800); await page.mouse.move(800, 600); await page.mouse.wheel(0, 760); } },
  { name: 'projects-budget', user: pm, path: 'projects', run: async (page) => { await openFirst('Enterprise Modernization')(page); await page.waitForTimeout(800); await clickTab('Budget')(page); } },
  { name: 'projects-financials', user: pm, path: 'projects', run: async (page) => { await openFirst('Enterprise Modernization')(page); await page.waitForTimeout(800); await clickTab('Financials')(page); } },
  { name: 'projects-new', user: pm, path: 'setup/projects/new' },
  { name: 'resourcing', user: pm, path: 'resourcing' },
  { name: 'project-costs', user: pm, path: 'odcs' },
  { name: 'pricing-list', user: pricing, path: 'pricing' },
  { name: 'pricing-proposal', user: pricing, path: 'pricing', run: openFirst('PRP-2027-014') },

  // Sheets and dashboards.
  { name: 'sheets-list', user: pm, path: 'sheets' },
  { name: 'sheets-plan-grid', user: pm, path: 'sheets', run: openFirst('Integrated Master Schedule') },
  { name: 'sheets-board', user: pm, path: 'sheets', run: async (page) => { await openFirst('Integrated Master Schedule')(page); await page.waitForTimeout(1000); await clickTab('Board')(page); } },
  { name: 'sheets-calendar', user: pm, path: 'sheets', run: async (page) => { await openFirst('Integrated Master Schedule')(page); await page.waitForTimeout(1000); await clickTab('Calendar')(page); } },
  { name: 'sheets-gantt', user: pm, path: 'sheets', run: async (page) => { await openFirst('Integrated Master Schedule')(page); await page.waitForTimeout(1000); await clickTab('Gantt')(page); } },
  { name: 'sheets-automation', user: pm, path: 'sheets', run: async (page) => { await openFirst('Integrated Master Schedule')(page); await page.waitForTimeout(1000); await clickTab('Automation')(page); } },
  { name: 'dashboards-list', user: pm, path: 'dashboards' },
  { name: 'dashboards-view', user: pm, path: 'dashboards', run: openFirst('Program Office overview') },

  // People.
  { name: 'people-directory', user: hr, path: 'hr' },
  { name: 'people-org-chart', user: hr, path: 'hr/org-chart' },
  { name: 'people-my-profile', user: employee, path: 'hr/me' },
  { name: 'people-positions', user: hr, path: 'hr/positions' },
  { name: 'people-onboarding', user: hr, path: 'hr/onboarding' },
  { name: 'people-benefits', user: hr, path: 'hr/benefits' },
  { name: 'people-compensation', user: hr, path: 'hr/compensation' },
  { name: 'people-reports', user: hr, path: 'hr/reports' },

  // Business development.
  { name: 'bd-pipeline', user: bd, path: 'bd' },
  { name: 'bd-opportunities', user: bd, path: 'bd/opportunities' },
  { name: 'bd-accounts', user: bd, path: 'bd/accounts' },
  { name: 'bd-forecast', user: bd, path: 'bd/forecast' },

  // Finance.
  { name: 'finance-journal', user: pm, path: 'accounting/journal' },
  { name: 'finance-close', user: pm, path: 'accounting/close' },
  { name: 'finance-month-review', user: pm, path: 'accounting/month-review' },
  { name: 'finance-accounts', user: pm, path: 'accounting/accounts' },
  { name: 'finance-statements', user: pm, path: 'accounting/reports' },
  { name: 'billing-invoices', user: pm, path: 'billing' },
  { name: 'billing-new', user: pm, path: 'billing/new' },
  { name: 'billing-aging', user: pm, path: 'billing/aging' },
  { name: 'billing-contracts', user: pm, path: 'billing/contracts' },
  { name: 'ap-bills', user: pm, path: 'purchasing/bills' },
  { name: 'ap-payment-runs', user: pm, path: 'purchasing/payments' },
  { name: 'ap-orders', user: pm, path: 'purchasing/orders' },
  { name: 'ap-vendors', user: pm, path: 'purchasing/vendors' },
  { name: 'ap-match-exceptions', user: pm, path: 'purchasing/match-exceptions' },
  { name: 'cash-accounts', user: pm, path: 'cash' },
  { name: 'cash-reconcile', user: pm, path: 'cash/reconcile' },
  { name: 'cash-forecast', user: pm, path: 'cash/forecast' },
  { name: 'cash-cards', user: pm, path: 'cards' },
  { name: 'assets-register', user: pm, path: 'assets' },
  { name: 'assets-depreciation', user: pm, path: 'assets/depreciation' },
  { name: 'payroll', user: pm, path: 'payroll' },
  { name: 'revenue', user: pm, path: 'accounting/revenue' },
  { name: 'revenue-estimates', user: pm, path: 'accounting/revenue/estimates' },
  { name: 'incurred-cost', user: pm, path: 'accounting/incurred-cost' },
  { name: 'indirect-rates', user: pm, path: 'accounting/indirect-rates' },
  { name: 'planning-budgets', user: pm, path: 'planning/budgets' },
  { name: 'planning-bva', user: pm, path: 'planning/budget-vs-actual' },

  // Materials.
  { name: 'materials-parts', user: materials, path: 'materials' },
  { name: 'materials-inventory', user: materials, path: 'materials/inventory' },
  { name: 'materials-boms', user: materials, path: 'materials/boms' },
  { name: 'materials-work-orders', user: materials, path: 'materials/work-orders' },
  { name: 'materials-mrp', user: materials, path: 'materials/mrp' },
  { name: 'materials-quality', user: materials, path: 'materials/quality' },
  { name: 'materials-sales-orders', user: materials, path: 'materials/sales-orders' },
  { name: 'materials-shipping', user: materials, path: 'materials/shipping' },

  // Reports.
  { name: 'reports-catalog', user: pm, path: 'reports' },
  { name: 'reports-writer', user: pm, path: 'reports/writer' },
  { name: 'reports-project-status', user: pm, path: 'reports/standard/project-status' },
  { name: 'reports-funding', user: pm, path: 'reports/standard/funding-status' },

  // Admin.
  { name: 'admin-setup', user: admin, path: 'setup' },
  { name: 'admin-organization', user: admin, path: 'setup/organization' },
  { name: 'admin-fiscal-calendar', user: admin, path: 'setup/fiscal-calendar' },
  { name: 'admin-org-units', user: admin, path: 'setup/org-units' },
  { name: 'admin-labor-categories', user: admin, path: 'setup/labor-categories' },
  { name: 'admin-customers', user: admin, path: 'setup/customers' },
  { name: 'admin-contracts', user: admin, path: 'setup/contracts' },
  { name: 'admin-projects', user: admin, path: 'setup/projects' },
  { name: 'admin-people', user: admin, path: 'setup/people' },
  { name: 'admin-companies', user: admin, path: 'setup/companies' },
  { name: 'admin-per-diem', user: admin, path: 'setup/per-diem' },
  { name: 'admin-users', user: admin, path: 'admin/users' },
  { name: 'admin-users-invites', user: admin, path: 'admin/users', run: clickTab('Invites') },
  { name: 'admin-users-reviews', user: admin, path: 'admin/users', run: clickTab('Access reviews') },
  { name: 'admin-users-events', user: admin, path: 'admin/users', run: clickTab('Security events') },
  { name: 'admin-users-signin', user: admin, path: 'admin/users', run: clickTab('Sign-in') },
  { name: 'admin-roles', user: admin, path: 'admin/roles' },
  { name: 'admin-assistant', user: admin, path: 'admin/assistant' },
  { name: 'admin-audit', user: admin, path: 'setup/audit' },
  { name: 'admin-support', user: admin, path: 'admin/support' },
  { name: 'admin-customization', user: admin, path: 'admin/customization' },
  { name: 'admin-layouts', user: admin, path: 'admin/customization/layouts' },
  { name: 'admin-automation', user: admin, path: 'admin/automation' },
  { name: 'admin-approval-workflows', user: admin, path: 'setup/approval-workflows' },
  { name: 'admin-integrations', user: admin, path: 'admin/integrations' },
  { name: 'admin-edi', user: admin, path: 'admin/integrations/edi' },
  { name: 'admin-import', user: admin, path: 'setup/import' },
  { name: 'admin-migration', user: admin, path: 'setup/migration' },
  { name: 'admin-dcaa', user: admin, path: 'admin/dcaa-readiness' },
  { name: 'admin-cpsr', user: admin, path: 'admin/cpsr-readiness' },
  { name: 'admin-cas', user: admin, path: 'admin/cas-disclosure' },
];
