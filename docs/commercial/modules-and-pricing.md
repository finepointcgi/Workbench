# Modules, seats and pricing

!!! danger "DRAFT: owner decisions needed. Not a price list."
    Every number on this page is a **placeholder** marked `[$?]`. The owner has not set prices. Nothing here is a quote,
    an offer or legal advice. Treat the module split and seat types as a **proposed design**; the
    [current shipped behaviour](../editions.md) is two editions, Community and Business, and other work is changing
    the licensing code while this is written. Where the two disagree, the code and [Editions](../editions.md) win until
    this page is updated.

## How the product is licensed today, and where it is going

**Shipped today** ([Editions](../editions.md)): Community is free and has no seat limit. Business is licensed per person and
unlocks multi-company, single sign-on, integrations, customer view links, the supplier portal, custom approval workflows
and incurred cost submissions. Keys are signed offline (`WB1.<payload>.<signature>`), and a lapsed key keeps Business
working for 14 days, then falls back to Community without deleting anything.

**Intended design** (being built separately; confirm against the code before quoting it to a customer): Business is split
into modules, so a firm that is not a government contractor does not pay for, or see, DCAA machinery, and a firm that only
wants finance does not pay for capture. Core stays free. Seats come in two types. Keys carry the modules and seat counts,
and a hosted license server is an option for customers who prefer it to an offline key.

## The modules

| Module | What it contains | Who wants it |
|---|---|---|
| **Core** (free) | Timesheets, expenses and receipts, leave, projects and the project page, the Plan (WBS, Gantt), sheets, boards and dashboards, people and HR basics, standard timesheet/leave/expense approvals, the audit log, importers, the mobile view. One company. | Everyone. This is the "try it for real" tier and the reason a firm can leave. |
| **Finance** | General ledger and period close, billing and receivables, payables and purchasing, cash and bank reconciliation, fixed assets, payroll, revenue recognition, corporate budgets, indirect rates, multi-currency. | Firms that want one system for time through to the books. |
| **Capture / BD** | The pipeline and opportunities, proposal pricing, win hand-off to Finance and the project, resourcing against forecast demand. | Firms that win work by proposal, commercial or government. |
| **Workforce** | Positions, onboarding, compensation and benefits, leave policies, per diem, resourcing and capacity, utilization reports. | Firms where people are the product. |
| **Controls** | Earned value management (EVM) and the project risk register: baselines, variances, estimate-at-completion, risk scoring and review. | Engineering and program-managed firms; required in practice on large government programs. |
| **Government contracting pack** | DCAA accounting-system readiness (SF 1408), cost accounting standards (CAS) support, CPSR evidence, the CMMC / NIST SP 800-171 assessment and SPRS score, incurred cost submissions, WAWF/PIEE billing, ceiling and funding control. | Government prime and sub contractors. Commercial firms never need to see it. |
| **Enterprise** | Single sign-on and SCIM provisioning, multi-company and intercompany, integrations (Jira, GitLab, Slack, Teams, Unanet, Costpoint), custom approval workflows, customer view links, supplier portal, API keys and the MCP assistant interface. | Larger firms and anyone with an identity provider or several legal entities. |

!!! note "Open boundary questions for the owner"
    - **Audit log, DCAA readiness pages and importers are deliberately free today** so a firm can always show an auditor its
      books and get its data in and out. If DCAA readiness moves into the Government pack, keep read access to existing
      answers free so a lapse never locks anyone out. Decide whether the readiness *checklist* (a marketing asset) stays in Core.
    - **Incurred cost submissions** are gated today. They fit the Government pack, not Enterprise.
    - **Custom approval workflows** are gated today. Keep them in Enterprise, or move to Core to make the free tier stickier.
    - **Single sign-on** is the classic "SSO tax". Putting it in Enterprise is normal; the honest alternative is to include
      it in every paid module and keep SCIM and multi-company for Enterprise.

## Comparison by edition

Two ways to package the same modules. The table shows **Option B (tiered bundles)**; Option A (à la carte modules) is the
same rows with each column sold separately. See [the alternatives](#the-two-main-alternatives).

| | Core | Team | Business | Enterprise |
|---|:---:|:---:|:---:|:---:|
| Time, expenses, leave, projects, Plan, sheets, dashboards, audit log, importers | ✓ | ✓ | ✓ | ✓ |
| Seat limit | none | `[?]` | `[?]` | `[?]` |
| Finance | | ✓ | ✓ | ✓ |
| Capture / BD | | | ✓ | ✓ |
| Workforce | | | ✓ | ✓ |
| Controls (EVM, risk) | | | ✓ | ✓ |
| Government contracting pack | | add-on | add-on | add-on |
| Single sign-on, SCIM | | | | ✓ |
| Multi-company, integrations, API keys | | | | ✓ |
| Support | community | email | email, business hours | priority, [see service levels](service-levels-draft.md) |
| Hosted option | `[?]` | ✓ | ✓ | ✓ |

The Government pack is an add-on in every tier on purpose: it is the most differentiated part of the product and the part
commercial firms should not pay for.

## Seat types

| | Full seat | Light seat |
|---|---|---|
| Counts toward | any active person who can sign in and use more than time entry | people who only enter time, expenses and leave and see their own approvals |
| Typical person | managers, finance, HR, BD, administrators | field staff, consultants, hourly employees, subcontractor staff |
| Price | `[$? per person per month]` | `[$? per person per month]`, suggested 15–30% of a full seat (a placeholder ratio, not a recommendation) |
| Why it exists | | A firm with 400 people and 60 managers should not pay 400 full seats. Time capture is the part everyone does, and light seats are what make the price defensible to a CFO. |

Rules the code already follows and the light seat should keep: pending invitations hold a seat; deactivating a person frees it
at once; changing roles costs nothing; **API keys are not people and take no seat**. The edge to decide: what a light seat may
*see* (own timesheet, own expenses, own leave, own pay stub) versus *do* (approve someone else's time makes them a full seat).

## Trials

- A trial is a normal license key or hosted tenant with an expiry date, marked as a trial, carrying every module the
  customer is evaluating. Suggested length: `[30]` days (placeholder).
- At expiry it behaves like any lapsed license: reads keep working, changes to paid features stop after the grace period,
  nothing is deleted. A customer who does not convert keeps Core and all of their data.
- Trials of the hosted service are separate tenants. Converting keeps the tenant; it does not require a migration.
- Decide: whether a trial can be extended by the sales team without a new key (needs the license server) and whether a trial
  may be used on production data. The [terms draft](end-user-terms-draft.md) assumes **yes it may, and the data is the customer's**.

## Offline keys and the connected license flow

| | Offline key (self-hosted) | Connected (license server) |
|---|---|---|
| How it works | You are sent a signed key (`WB1.…`). The server checks the signature against the public key built into the product. It never contacts anyone. | The server asks the vendor's license service (outbound HTTPS only) for its current entitlements and caches the answer. |
| Good for | Air-gapped enclaves, CUI environments, firms that cannot allow outbound calls. | Firms that want self-service seat changes, usage true-ups and automatic renewal without a new key. |
| Changing seats or modules | Issue and enter a new key. | Change it in the vendor portal; the server picks it up on its next check. |
| If the service is unreachable | Not applicable. | Last good entitlements keep working for a grace period `[? days]`, then the server behaves as a lapsed license. |
| What is sent | Nothing. | License identifier, edition, seat count and version. **No business data.** (To be confirmed against the shipped implementation.) |
| Hosted (SaaS) | The vendor sets the tenant's plan; the customer never handles a key. | |

Both flows must end the same way when a license lapses: a warning, a grace period, then Core, with nothing deleted. That
promise is also in the [terms draft](end-user-terms-draft.md).

## The two main alternatives

**Option A: per-seat with module add-ons.** Every module has its own per-seat price; a customer buys the modules it wants
for the people who use them.

- For: simple to explain ("you pay for what you use"), easy to start small, friendly to consultancies that only want Finance.
- Against: many SKUs; a quote becomes a spreadsheet; discounts are negotiated per module; customers under-buy and then work around gaps.

**Option B: tiered bundles.** Core, Team, Business and Enterprise as above, with the Government pack as an add-on.

- For: a short price page, predictable revenue per seat, an obvious upgrade path, easier for a reseller or a procurement office.
- Against: customers will feel they pay for modules they do not use; a firm needing one Enterprise feature (SSO) is pushed into the top tier.

**Recommendation to consider (not a decision):** sell Option B publicly, keep Option A internally so sales can quote an exception,
and put SSO in a tier low enough that nobody calls it a tax. Buyers of government software expect to buy a compliance pack
separately; commercial buyers expect a short list. Either option works with the licensing code as designed.

## Pricing worksheet

Fill in the right-hand columns. Everything in brackets is a placeholder.

| Line | Unit | Option A (à la carte) | Option B (bundle) |
|---|---|---|---|
| Core | per person per month | free | free |
| Full seat, base | per person per month | `[$?]` for Core + one module | Team `[$?]`, Business `[$?]`, Enterprise `[$?]` |
| Light seat | per person per month | `[$?]` | `[$?]` |
| Finance | per full seat per month | `[$?]` | included from Team |
| Capture / BD | per full seat per month | `[$?]` | included from Business |
| Workforce | per full seat per month | `[$?]` | included from Business |
| Controls | per full seat per month | `[$?]` | included from Business |
| Government contracting pack | per full seat per month, or per company | `[$?]` | `[$?]` add-on |
| Enterprise | per full seat per month | `[$?]` | included in Enterprise |
| Minimum purchase | seats or dollars per year | `[?]` | `[?]` |
| Annual prepay discount | percent | `[?]` | `[?]` |
| Volume breaks | seats | `[?]` | `[?]` |
| Hosted uplift (SaaS vs self-hosted) | percent or per seat | `[?]` | `[?]` |
| Implementation / import services | fixed or hourly | `[$?]` | `[$?]` |
| Premium support | percent of license | `[?]` | `[?]` |

### Questions that decide the numbers

1. **What is it replacing?** Costpoint, Unanet, Smartsheet, Asana and Microsoft Project each have a per-seat price the
   customer already pays. Look up the current published prices yourself before setting yours; this page does not quote them
   because they change and were not verified here.
2. **Per seat or per company?** Per seat fits time-and-expense-heavy firms. A small firm with ten people and a large government
   contract may prefer a flat price per company; a minimum purchase protects against that being too cheap.
3. **Self-hosted versus hosted.** Self-hosting costs the customer operations; hosting costs you operations and brings the
   [SOC 2](soc2-readiness.md) and [service level](service-levels-draft.md) obligations. Price the hosted version to cover both.
4. **Source-available.** Because the source is available under the FSL, price on convenience, support, compliance evidence
   and the keyed modules, not on secrecy. A customer can read the code; they cannot offer a competing service.
5. **Free Core.** It is the adoption engine. Decide whether Core stays free for hosted use too (it costs you hosting) or
   self-host only.

### Honest rationale

- **Light seats** are the single biggest lever. Without them the product is expensive exactly where it should be cheapest: the
  hourly workforce that fills in timesheets.
- **The Government pack as an add-on** keeps commercial prices lower and puts the price of compliance on those who need it.
- **Annual terms, billed in advance,** are the norm; month-to-month at a premium is a conversion tool.
- A number picked now will be wrong. Set a launch price, state that early customers keep it for `[?]` years, and review
  after the first `[10]` customers.

## What does not exist yet

- Payment, invoicing and renewals ([Editions](../editions.md) says so). Until that exists, sell by invoice.
- Tax handling, reseller terms, and trial self-service.
- A published refund policy. The [terms draft](end-user-terms-draft.md) proposes one; decide it.
