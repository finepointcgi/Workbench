# Roles and permissions

Workbench decides what someone can do from **permissions**: named capabilities such as *Post journal entries* or *View pay
rates*. A **role** is a bundle of permissions, and a person can hold several roles: they can do anything any of their roles
allows. Administrators manage roles under **Admin › Roles** and give them to people from **Admin › Users and security**
(or from a role's People tab).

## Built-in and custom roles

- **Built-in roles** are the ten organization roles Workbench has always had: Admin, Finance, Project manager, Timekeeper,
  Resource manager, HR, Materials, Business development, Pricing and Employee. They grant exactly the access those roles
  always did (`BuiltInRoleParityTests` checks every combination of them), so nothing changed for anyone on upgrade. They
  can be viewed and cloned, but not changed or deleted. Their permissions come from the catalog below (in code), so a
  permission added in a later release reaches the right built-in roles without a data change.
- **Employee** is held by everyone and grants no extra permissions: a person's own time, expenses, travel, leave,
  requisitions, profile and what's shared with them need no permission.
- **Admin** holds every permission and can't be changed, and there must always be an active user holding it: nobody can
  remove their own Admin role, and the last active administrator can't lose it or be deactivated. So nobody can lock
  everyone out.
- **Custom roles** are created, edited, cloned and deleted under Admin › Roles. Clone a built-in role to start from it.
  Deleting a custom role takes it away from everyone who held it (each change audit logged); a role still used by an
  approval workflow, an SSO group mapping, an API key, a pending invite or approvals waiting on it can't be deleted.
- **Nobody gives out more than they have.** Creating or changing a role, or giving someone a role, is refused when it
  would grant a permission the person doing it doesn't hold. A user administrator therefore can't make themselves (or
  anyone else) an accountant. API keys can't manage roles at all.

Who holds which role is stored as `UserRoles` rows; a custom role's permissions as `RolePermissions` rows; the built-in
role rows (`Roles`, fixed ids) are seeded by the migration. The `OrgRoles` flags remain as a compatibility view of the
built-in roles (the API's role fields, SCIM, older tests); access is never decided by them.

## What follows roles

- **Pages and menus.** Every menu entry declares the permission(s) that reveal it (`NavCatalog`), and the menu, the command
  palette (Ctrl+K) and search show only what the person can use. The area layouts (Setup, the Finance pages, Admin) check
  the same entry's permission on every navigation and show a 403 that names the permission. The page's services check
  the permission again on the server, so hiding is never the only protection.
- **Approval workflows.** An approver rule of type *Org role* can target a built-in org role (as before) or a custom role;
  everyone holding the role can decide the step. Self-approval rules apply unchanged.
- **SSO and SCIM.** IdP group mappings grant built-in and custom roles, both for just-in-time sign-up and, with *The
  provider manages roles*, at every sign-in. "MFA for administrators" applies to anyone holding an Administration
  permission. SCIM-created users start as Employees, as before.
- **Invites** grant built-in and custom roles when the person first signs in.
- **API keys** act as service users holding the roles chosen for the key (built-in and custom); the key's roles are put
  back at each request if someone changed them elsewhere.
- **Saved reports** can be shared with built-in or custom roles.
- **Access reviews** snapshot each user's roles, their effective permissions and their role changes in the review period;
  the CSV export lists them, plus the changes to role definitions in the period.
- **The dev and demo sign-in** labels people with every role they hold.

## Where a role applies (scopes)

Giving someone a role, an administrator can limit it to some **companies**, **org units**, **projects** or **contracts**:
"Project controller for org unit ENG only", "Finance for company B only". An unlimited role applies everywhere, as before.
Admin can't be limited.

- **Companies**: the role applies while the person works in one of those companies (the company switcher, or their only
  company). It composes with the multi-company access lists: they still only see companies they may use, and a
  company-limited role adds nothing while they work in another one. Checks on a record follow the record's company.
- **Org units**: people in the unit or any unit below it, and projects (and their contracts) whose manager is in it.
  Projects have no org unit of their own.
- **Projects** and **contracts**: those records (a contract covers its projects; a project its contract).
- Scoped permissions apply where a check names a record: project financials, budgets and contract funding, project setup,
  the projects hub, timekeeping reports (whose hours), HR records (full and job-level access) and the report writer's
  project rows. A page-level check with no record in view only counts unlimited roles (and company roles in the current
  company), so it fails closed. Project visibility rules (which projects someone sees at all) stay separate and apply
  on top.
- Users, the access review, dev sign-in and role members show a limited role as "Finance (company B)". Limiting or
  widening an assignment is audited like any other role change.

## Sensitive fields

Some fields are permissions of their own, under **Sensitive fields**. A role that lacks one gets the field masked (shown
as hidden, or 0 for a required number) or the read refused, everywhere it's read: pages, the API, exports built from those
reads and the assistant's tools, which all go through the same permission decorators. Built-in roles hold each field
exactly where they saw it before.

| Field | Permission | Masked or refused | Always visible to |
|---|---|---|---|
| Salary ranges, compensation history and pay change amounts, merit budgets | See salaries | Masked | — |
| Pay rates in payroll | View pay rates | Masked (unchanged) | — |
| Employee cost rates and labor cost (reports, report writer) | See cost rates and labor cost | Masked (unchanged) | — |
| Burdened cost and burdened financials; burden columns of the GL reconciliation | See burdened cost | Refused; reconciliation columns masked | The project's manager |
| EEO and veteran self-identification; EEO-1 and VETS-4212 | See EEO data | Refused | The employee (their own) |
| Fee, fee rate, margin, wrap rate and price in proposal pricing; the cost volume export | See fee and margin | Masked; export refused | — |
| Vendor TINs and W-9s | See and set vendor tax ids | Masked (unchanged) | — |
| Vendor bank details | Vendor bank details | Masked (unchanged) | — |

Workbench stores no employee SSNs; "tax ids" are vendor TINs. Someone who can't see the fee can still save a price
model's other settings; its fee terms are kept as they were.

Developers mark a contract property with `[property: SensitiveField(Permission.X)]` and return reads through
`FieldMasker.Mask(dto, permissions)` in the permission decorator; nested records and lists are handled. A relationship that
reveals a field (the project's manager, the employee) is decided in the decorator.

## Segregation of duties

Admin › Roles warns (it doesn't refuse: small organizations sometimes must combine duties) when a role, or the set of roles
someone holds, combines permissions that are usually kept apart:

- **Manage vendors** with **Pay vendors**: Someone who can create vendors and pay them could pay a vendor they made up.
- **Vendor bank details** with **Pay vendors**: Someone who can change vendor bank details and send payments could redirect payments.
- **Enter and approve AP bills** with **Pay vendors**: Entering or approving bills and paying them should be separate.
- **Manage purchase orders** with **Enter and approve AP bills**: Ordering goods and approving the vendor's bill should be separate.
- **Manage payroll profiles** with **Run payroll**: Someone who can change pay and run payroll could pay themselves more.
- **Change compensation** with **Run payroll**: Deciding compensation and running payroll should be separate.
- **Bill customers** with **Record customer payments**: Billing customers and recording their payments should be separate.
- **Post journal entries** with **Manage bank accounts**: Posting journal entries and reconciling the bank should be separate.
- **Change cost rates** with **Run payroll**: Setting cost rates and running payroll should be separate.

Existing rules still apply on top of roles: nobody approves their own document unless the workflow step allows it, a
vendor bank change is approved by someone other than its requester, and so on.

## Audit

Every change to a role (created, changed, cloned, deleted) writes an audit row (`AuditEvent`, entity type `Role`, with the
name, description and permissions before and after) and a security event (`role.created`, `role.changed`,
`role.deleted`). Every change to who holds a role (from Admin, SSO, SCIM, invites, API keys or a role deletion) writes an
audit row (entity type `UserRoles`, entity id = the user, the roles before and after) and the `user.roles_changed`
security event.

## For developers

- Check a permission on the server with `AccessControl.RequireAsync(Permission.X, "do the thing", ct)` (a recorded 403
  that names the permission), `HasAsync`, or `GetPermissionsAsync()` for several. In the UI, `CurrentUserInfo.Can(...)`.
  Don't decide access with `OrgRoles` flags or `IsAdmin`.
- To notify the people who handle something, use `RoleStore.EmailsWithPermissionAsync(db, Permission.X, ct)`.
- To add a permission: a constant and a `PermissionDefinition` (area, name, description, the built-in roles that hold it)
  in `Contracts/Identity/Permissions.cs`, a row below and an entry in `BuiltInRoleParityTests`. Never rename a key:
  custom roles store them. Give a new page's menu entry `NavRule.Need(Permission.X)`.

- **Run-time permissions.** Features whose permissions are defined by administrators (custom fields, automations,
  configurable forms and dashboards) register them through `IPermissionRegistry` (`RegisterAsync`, `UnregisterAsync`,
  `ListBySourceAsync`); everything reads the full catalog through `IPermissionCatalog` (`ListAsync`, `FindAsync`). A key is
  lower-case and dotted (`custom_field.clearance.view`, `automation.3f2a.run`), at most 100 characters and never a
  built-in key. `GrantedTo` gives it to built-in roles (as role rows); Admin always holds it; custom roles grant it from
  Admin › Roles, where it's listed under its own area with its source. Unregistering removes it from every role.
  Registering, updating and unregistering are audited (`RegisteredPermission`) and recorded (`permission.registered`,
  `permission.unregistered`). The registry doesn't check who calls it: call it from your own permission-checked service.
  Check a registered permission like any other: `AccessControl.RequireAsync(key, "...", ct)`.

## The permission catalog

⚠ marks sensitive permissions (pay, rates, tax ids, bank details, security). "Opens" lists the menu pages the permission
reveals. Admin holds every permission.

### Administration

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Administer users ⚠ | `admin.users` | Invite people, deactivate and reactivate accounts, sign people out everywhere, email password reset links, reset someone's MFA (authenticator app), link users to employees and assign roles. | Admin | Users and security |
| Manage roles ⚠ | `admin.roles` | Create, edit, clone and delete custom roles and choose their permissions. | Admin | Roles |
| Manage sign-in and security ⚠ | `admin.security` | Sign-in settings (single sign-on, MFA, SCIM, sessions), the security event log and access reviews. | Admin | Users and security |
| Manage retention and legal holds | `admin.retention` | Record retention policies and legal holds. | Admin | — |
| DCAA readiness | `admin.compliance` | The DCAA accounting system readiness checklist. | Admin | DCAA readiness |
| Manage integrations ⚠ | `admin.integrations` | API keys, webhooks, connectors and the Costpoint sync. | Admin | Integrations |
| Administer the assistant | `admin.assistant` | AI assistant settings, usage and audit. | Admin | Assistant |
| Handle support requests | `admin.support` | See and answer everyone's support requests, and hear about new ones. | Admin | Support requests |
| Change organization settings | `admin.organization` | Organization-wide settings such as the timesheet cadence. | Admin | — |
| Own every workspace | `admin.all_workspaces` | Owner access to every workspace and sheet, and the right to change anyone's dashboards, saved reports and schedules. | Admin | — |
| Manage customization | `admin.customization` | Custom fields, picklists, status labels, document numbering, form layouts, shared list views and role home pages. | Admin | Customization, Layouts |
| Manage automations | `admin.automations` | Build, test, turn on and off automations, and see their run log. | Admin | Automation |

### Approvals

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| View approval workflows | `approvals.workflows.view` | See approval workflows and the approvers set for each org unit. | Admin, Finance | Approval workflows |
| Change approval workflows ⚠ | `approvals.workflows.manage` | Change how documents are routed for approval. | Admin | — |
| Manage anyone's delegations | `approvals.delegations.manage` | Create and revoke approval delegations for other people. | Admin | — |
| Approver of last resort | `approvals.act_as_admin` | Decide approval steps that allow administrators, and approve timesheets and leave for people with no supervisor. | Admin | — |

### Organization setup

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| View organization setup | `setup.organization.view` | The setup checklist and organization settings (read only). | Admin, Finance | Getting started, Organization |
| Manage employees and org units | `setup.people` | Add and change employees, org units and labor categories. | Admin, Finance | People, Org units, Labor categories |
| Manage the fiscal calendar | `setup.fiscal_calendar` | Set up fiscal years and close or reopen fiscal periods. | Admin, Finance | Fiscal calendar |
| Manage customers and contracts | `setup.contracts` | Customers, contracts, CLINs, contract modifications and funding, and contracts from won opportunities and proposals. | Admin, Finance | Customers, Contracts |
| View and change bill rates ⚠ | `setup.bill_rates` | Contract bill rates. | Admin, Finance | — |
| Set up every project | `setup.projects` | Projects and WBS, charge assignments and plan sheets for every project. | Admin, Finance | New project, Projects and WBS |
| Import data and run the cut-over | `setup.imports` | Imports, the go-live checklist and reconciliation with Costpoint. | Admin, Finance | Import, Go-live migration |
| Read the audit log | `setup.audit_log` | The audit trail of changes to rates, setup, budgets and approvals. | Admin, Finance | Audit log |
| Manage per diem rates | `setup.per_diem` | Import and change per diem rate tables. | Admin, Finance | Per diem rates |
| Manage currencies | `setup.currencies` | Currencies and exchange rates. | Admin, Finance | Currencies and rates |
| Manage companies | `setup.companies` | Companies, intercompany accounts and company periods. | Admin, Finance | Companies |

### Time and labor

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| See everyone's time | `time.view_all` | Everyone's timesheets, hours, daily entries, leave requests and utilization. | Admin, Timekeeper, Finance | Timesheet flags |
| Team time for everyone | `time.team` | The Team time page and timesheet approvals list for the whole organization. | Admin, Timekeeper | Team time, Approvals, Timesheet flags, Timesheet approvals, ODC approvals, Bill approvals |
| Change cost rates ⚠ | `labor.cost_rates.manage` | Add employee cost rates. | Admin, Finance | — |
| Total time accounting | `time.total_time` | The labor cost method, uncompensated overtime and effective labor rates. | Admin, Finance | Total time accounting |
| Manage leave balances | `leave.balances.manage` | See everyone's leave balances, adjust them, run accruals and the leave year end. | Admin, Finance | — |
| Manage leave policies and holidays | `leave.policies.manage` | Leave policies, eligibility and the holiday calendar. | Admin | — |

### Projects

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| See every project | `projects.view_all` | Every project in lists, the projects hub, search and reports, not just the ones you're on (project visibility). | Admin, Resource manager, Finance | — |
| Create projects | `projects.create` | Create new projects (project requests). | Admin, Project manager, Finance | New project |
| Run the projects you manage | `projects.manage_own` | Set up the projects you're the manager of: WBS, charge assignments, plan sheet, draft budgets and estimates at completion. | Admin, Project manager | Resourcing, Project costs, Project status, Contract funding |
| See every project's financials | `projects.financials.view` | Financials, budgets, project costs and financial exports of every project, and contract funding. Project managers always see their own. | Admin, Finance | Project costs, Project status, Contract funding |
| Staff every project | `projects.resourcing.manage` | Change anyone's allocations and assignments (Assign people) on every project and see capacity and utilization. A project's manager assigns people on their own projects without it. | Admin, Resource manager | Resourcing, My allocations |
| See every project's staffing | `projects.resourcing.view` | Allocations, assignments and open demand on every project. | Admin, Finance | — |
| Manage earned value | `projects.evm.manage` | Approve and reject baseline changes and reopen closed earned value periods. | Admin, Finance | — |
| Manage ceilings and closeout | `projects.ceilings.manage` | Hours, cost and billing ceilings, overrides, alerts and contract and project closeout. | Admin, Finance | — |

### Accounting

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Manage the chart of accounts | `gl.accounts.manage` | Add and change general ledger accounts. | Admin, Finance | Chart of accounts |
| Post journal entries | `gl.journals.post` | Draft, post and reverse manual journal entries. | Admin, Finance | Journal entries |
| Manage posting rules | `gl.posting.manage` | General ledger posting setup. | Admin, Finance | Posting rules |
| Close fiscal periods | `gl.close` | The period close checklist and closing accounting periods. | Admin, Finance | Period close |
| See financial statements | `gl.reports.view` | Balance sheet, income statement, trial balance, consolidated statements, intercompany balances and the general ledger in the report writer. | Admin, Finance | Financial statements, Consolidated statements |
| Manage recurring journals | `gl.recurring.manage` | Recurring journal templates and runs. | Admin, Finance | Recurring journals |
| Revalue foreign currency | `gl.revaluation` | Currency revaluation and the foreign currency report. | Admin, Finance | Currency revaluation, Foreign currency report |
| Manage corporate budgets | `planning.budgets.manage` | Budget versions, budget vs actual and planning cycles. | Admin, Finance | Budgets, Budget vs actual, Planning cycles |
| Revenue recognition | `revenue.manage` | Revenue setup, estimates at completion and revenue recognition runs and reports. | Admin, Finance | Revenue, Revenue estimates, Revenue setup, Revenue by project, Revenue reports |
| Manage indirect rates | `compliance.indirect_rates` | Fringe, overhead and G&A pools and rates. | Admin, Finance | Indirect rates |
| Incurred cost submissions | `compliance.incurred_cost` | Prepare incurred cost (ICE) submissions. | Admin, Finance | Incurred cost |
| CAS disclosure and purchasing review | `compliance.cas_cpsr` | The CAS disclosure statement and purchasing system review files. | Admin, Finance | Purchasing system review, CAS disclosure statement |
| Manage sales and use tax | `tax.sales_tax.manage` | Tax rates, jurisdictions, exemption certificates and returns. | Admin, Finance | Sales and use tax return, Sales and use tax |
| Manage fixed assets | `assets.manage` | The asset register, categories, transfers, tax books and asset reports. | Admin, Finance | Asset register, Capitalize, Asset categories, Tax depreciation, Asset transfers, Asset reports |
| Run depreciation | `assets.depreciation.run` | Run and post depreciation. | Admin, Finance | Depreciation |

### Billing and receivables

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Bill customers | `billing.invoices.manage` | Invoices, billing milestones, government billing, progress payments, the unbilled accrual and customer statements. | Admin, Finance | Invoices, New invoice, Contracts and milestones, Unbilled accrual, Invoice, Customer statement |
| Record customer payments | `billing.payments.record` | Record and apply customer payments. | Admin, Finance | Customer payments |
| See receivables | `billing.reports.view` | AR aging and receivables reports. | Admin, Finance | AR aging |

### Payables and purchasing

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Manage vendors | `ap.vendors.manage` | Add and change vendors and subcontracts. | Admin, Finance | Vendors, Subcontracts |
| Enter and approve AP bills | `ap.bills.manage` | Every AP bill, credit and debit memo: enter, approve, post, void and apply them, and release subcontract retainage. | Admin, Finance | Approvals, Bills, ODC approvals, Bill approvals |
| Pay vendors | `ap.payments.manage` | Payment runs (which net vendor credits), vendor refunds and bank payment files. | Admin, Finance | Payment runs |
| See AP reports | `ap.reports.view` | Payables aging and 1099 reports. | Admin, Finance | AP reports |
| Manage purchase orders | `purchasing.orders.manage` | Every purchase order and requisition, receiving and the three-way match (and its overrides). | Admin, Finance | Receiving, Purchase orders, Match exceptions |
| Sourcing | `purchasing.sourcing.manage` | Requests for quote, the supplier portal and EDI. | Admin, Finance | Requests for quote, Supplier portal, EDI |

### Cash

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Manage bank accounts | `cash.manage` | Bank accounts, statement imports and bank reconciliation. | Admin, Finance | Cash and bank accounts, Import statements, Reconcile, Reconciliation history |
| See the cash position | `cash.forecast.view` | The cash position and cash forecast. | Admin, Finance | Cash forecast |

### Payroll

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Run payroll | `payroll.run` | Run, post and void payroll. | Admin, Finance | Payroll |
| Set up payroll | `payroll.setup` | Pay groups, payroll providers and payroll file layouts. | Admin, Finance | — |
| Manage payroll profiles | `payroll.profiles.manage` | Employees' payroll profiles, with pay rates masked. | Admin, Finance | — |

### Travel and cards

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Manage everyone's travel | `travel.manage` | Everyone's trips, travel advances and per diem. | Admin, Finance | — |
| Manage corporate cards | `cards.manage` | Card programs, statement imports, everyone's card charges and card reconciliation. | Admin, Finance | Corporate cards, Card programs |

### People (HR)

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| Manage personnel records ⚠ | `hr.records.manage` | Everyone's personnel records, documents and EEO data, terminated employees and personnel requests. | Admin, HR | Personnel requests |
| Manage positions | `hr.positions.manage` | Add and change approved positions. | Admin, HR | Positions |
| Change compensation ⚠ | `hr.compensation.manage` | Propose and decide compensation changes. | Admin, HR | Compensation |
| See workforce and pay data ⚠ | `hr.workforce.view` | Positions with salary ranges, compensation (and deciding compensation changes), benefits administration and deductions, and workforce reports. | Admin, HR, Finance | Positions, Compensation, Benefits administration, HR reports |
| Administer benefits | `hr.benefits.manage` | Benefit plans, enrollment windows, life events and deductions. | Admin, HR | Benefits administration |
| Onboarding and offboarding | `hr.onboarding.manage` | Onboarding and offboarding checklists for everyone. | Admin, HR | Onboarding and offboarding |
| Restricted HR reports ⚠ | `hr.reports.restricted` | EEO-1, VETS-4212, clearance and other restricted HR reports. | Admin, HR | HR reports |

### Business development

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| See the pipeline | `bd.pipeline.view` | The whole capture pipeline, accounts and contacts, leads, forecast and gate settings. | Admin, Business development, Finance | Pipeline, Opportunities, Accounts and contacts, Leads, Forecast and win/loss, Gates and settings |
| Capture new business | `bd.capture` | Create leads and opportunities, review leads and change go/no-go gates. | Admin, Business development | — |
| Edit every opportunity | `bd.opportunities.edit_all` | Change any opportunity and withdraw anyone's gate review, not just the ones you manage. | Admin | — |
| Choose the B&P charge code | `bd.bp_charge_code` | The bid and proposal charge code used by the pipeline. | Admin, Finance | — |
| Price proposals ⚠ | `pricing.prices` | Build, compare and lock proposal prices (shows cost rates, indirect rates and fee). | Admin, Pricing, Finance | Proposal pricing |

### Materials

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| See materials | `materials.view` | Parts, inventory, work orders, quality records and inventory reports. | Admin, Materials, Finance | Parts, Part, Inventory, Inventory reports, Pick list, Shipment |
| Run materials | `materials.manage` | Change parts, inventory, BOMs, work orders, MRP, quality, engineering changes, kits and shipping. | Admin, Materials | Receiving, Transactions, Bills of materials, Work orders, MRP, Quality, Engineering changes, Receive on a phone, Materials setup, Shipping, Pick on a phone, Kits, Backflush, Capacity schedule, Physical inventory, Landed cost, Count on a phone |
| Post inventory value | `materials.costing` | Revalue inventory, see work-in-process cost, post landed cost and physical inventory adjustments. | Admin, Finance | — |
| Apply material review dispositions | `quality.dispositions.apply` | Apply NCR dispositions (they can move inventory and cost). | Admin, Materials, Finance | — |
| See sales orders | `sales.orders.view` | Sales orders, pick lists and shipments. | Admin, Materials, Finance, Business development | Sales orders |
| Enter sales orders | `sales.orders.manage` | Enter and change sales orders. | Admin, Materials, Finance | — |
| Credit holds | `sales.credit_hold` | Put sales orders on or off credit hold. | Admin, Finance | — |

### Sheets

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| See restricted columns ⚠ | `sheets.restricted_columns.view` | Sheet columns marked "Finance and owners only" in every workspace. | Admin, Finance | — |
| Publish public forms | `sheets.forms.public` | Share a sheet's form by public link, so anyone with the link can add a row without signing in. Only in workspaces the person owns. | Admin, Finance, Project manager | — |

### Sensitive fields

| Permission | Key | What it allows | Built-in roles | Opens |
|---|---|---|---|---|
| See cost rates and labor cost ⚠ | `labor.cost.view` | Employee cost rates, labor cost in reports and the labor distribution. | Admin, Finance | — |
| See and set vendor tax ids ⚠ | `ap.vendors.tax_ids` | Set a vendor's TIN, see the full TIN and vendor W-9s. | Admin, Finance | — |
| Vendor bank details ⚠ | `ap.vendors.bank` | See and change where vendors are paid. | Admin, Finance | — |
| View pay rates ⚠ | `payroll.pay_rates.view` | Reveal what employees are paid. | Admin, Finance | — |
| See salaries ⚠ | `fields.salaries` | Salary ranges on positions and the amounts in compensation history and pay changes. Without it they show as hidden. | Admin, HR, Finance | — |
| See burdened cost ⚠ | `fields.burdened_cost` | Fringe, overhead and G&A applied to project cost (burdened cost and burdened financials). Project managers always see their own projects'. | Admin, Finance | — |
| See EEO data ⚠ | `fields.eeo` | Employees' EEO and veteran self-identification and the EEO-1 and VETS-4212 reports. Employees always see their own. | Admin, HR | — |
| See fee and margin ⚠ | `fields.pricing_fee` | Fee, fee rate, margin and price in proposal pricing, and the cost volume export. Without it pricing shows cost only. | Admin, Pricing, Finance | — |


