# Modules, seats and pricing

!!! danger "DRAFT: owner decisions needed. Not a price list."
    Every number on this page is a **placeholder** marked `[$?]`. The owner has not set prices. Nothing here is a quote,
    an offer or legal advice. Treat the module split and seat types as a **proposed design**; the
    [current shipped behaviour](../editions.md) is two editions, Community and Business, and other work is changing
    the licensing code while this is written. Where the two disagree, the code and [Editions](../editions.md) win until
    this page is updated.

## How the product is licensed

This follows the module catalog in [Editions](../editions.md) (the authoritative text; if the two differ, Editions wins).
In short:

- A license key lists **modules**. `core` is always included and **free forever**.
- Seats come in two kinds, **full** and **light**, and a key may also carry an optional **seat count per module**.
- Every module is *not enforced* yet except two (`govcon` and `enterprise`, for their named features), so nothing a Community
  server had before the split is taken away until pricing is decided. **Which modules are enforced is the pricing decision**:
  it is one flag per module in `ModuleCatalog`.
- **Honest limits.** On a self-hosted server these gates are an **honor system** backed by the source license: the code is
  readable and changeable under the FSL, which forbids only a competing service. **Real enforcement is the hosted service**,
  where the vendor controls the code that runs. Price accordingly (see [rationale](#honest-rationale)).
- The old `finance` module no longer exists: it split into **billing** and **accounting**. Keys issued with `finance` still
  work (they mean billing plus accounting). The old `controls` name for audit retention and legal holds is now part of
  **enterprise**; earned value and risk are **project controls** (`projectcontrols`).

## The modules

| Module | Id | What it contains | Who wants it |
|---|---|---|---|
| **Core** (free forever) | `core` | Time and timesheets with the standard approval, basic expenses and receipts, leave, My Work, notifications, customers, agreements and contracts, projects and WBS, the Plan with Gantt and assigning people to tasks, sheets, dashboards, search, the standard project, timesheet and HR-directory reports, users, roles and MFA, the **audit log**, importers and **file exports of your own data**. Read access continues after any license lapses. | Everyone. The adoption engine, and the reason a firm can leave. |
| **Billing** | `billing` | Invoices, ready to bill, customer payments, AR aging, unbilled accrual, milestones, retainers and recurring billing, revenue recognition, the commercial invoice, live accounting push to QuickBooks and Xero | Firms that invoice for time and want it off a spreadsheet, with or without the full ledger |
| **Accounting** | `accounting` | General ledger, period close, recurring journals, chart of accounts, posting rules, payables, purchasing, requests for quote, payment runs, cash and bank reconciliation, fixed assets, sales tax, multi-currency, corporate budgets and planning cycles, indirect rates | Firms that want the books in the same system |
| **Capture** | `capture` | Pipeline: opportunities, accounts, leads, forecast, gates, proposals and pricing, the shredder, deal import | Firms that win work by proposal, commercial or government |
| **Workforce** | `workforce` | HR records, positions, compensation, benefits, onboarding, payroll, Paylocity, and cross-project capacity, utilization and skills (assigning people to tasks in the Plan stays core) | Firms where people are the product |
| **Project controls** | `projectcontrols` | Earned value, the risk register, ceilings and closeout, schedule health, the monthly narrative | Engineering and program-managed firms |
| **Materials** | `materials` | Parts, inventory, bills of materials, work orders, MRP, kits, quality, engineering changes, sales orders, shipping, landed cost | Manufacturers and firms that build or ship hardware |
| **Government contracting** | `govcon` | DCAA readiness, CPSR, CAS disclosure, CMMC, incurred cost, CLIN funding reports, WAWF, total time accounting, the Costpoint parallel run | Government prime and sub contractors. Commercial firms never need to see it. |
| **Enterprise** | `enterprise` | Single sign-on and SCIM, more than one company, integrations, customer links, custom approval workflows, the automation builder, layouts and custom fields, API keys, audit retention, legal holds and access reviews | Larger firms, and anyone with an identity provider or several legal entities |
| **AI** | `ai` | **Reserved.** Nothing is built or billed. The AI assistant is **free on every edition when the organization supplies its own API key.** The id exists for a possible future hosted-credits product. | Nobody yet |

Notes on how the gates work today, from [Editions](../editions.md):

- A **feature** belongs to a module and its gate is always on. For example the supplier portal needs a key that lists
  `accounting`, even though Accounting's other pages are open while `accounting` is not enforced.
- `govcon` and `enterprise` are enforced for their **named features only** (`WholeModule = false`): DCAA readiness pages, layouts,
  API keys and the audit retention pages stay open to everyone for now. Gating all of it is a pricing decision not yet made.
- Gates apply to **changing** things only. Reads are never refused, so a lapsed license cannot lock anyone out of their records,
  and nothing is deleted.

!!! note "Open boundary questions for the owner"
    - **Which modules to enforce, and when.** Today only `govcon` and `enterprise` are. Flipping `Enforced` on `billing`,
      `accounting` and the rest takes features away from current Community users; decide whether existing Community users
      are grandfathered, and announce it.
    - **`WholeModule` for govcon and enterprise.** If it stays false, DCAA readiness stays free and only the named features
      (for example incurred cost) are paid. That keeps the readiness checklist as a marketing asset. If it moves to true,
      keep read access to existing answers free.
    - **Audit retention, legal holds and access reviews now sit in `enterprise`.** An auditor-facing customer on a lower tier
      will ask for them; the free audit log and file exports stay in core either way. Confirm that is the line you want.
    - **Single sign-on is the "SSO tax."** Enterprise is the usual home; the honest alternative is to include it in every paid
      bundle and keep SCIM and multi-company for Enterprise.
    - **Billing without accounting.** A firm may want invoices and QuickBooks push but not a ledger. They are separate modules
      so that works; price them so the combination is not cheaper than either alone is worth.
    - **Community people cap.** `License:CommunityMaxPeople` (default 0, unlimited) lets an operator, and the hosted free tier,
      limit how many people a keyless server holds. Whether to set one on the hosted free tier is a pricing decision.

## Comparison by module and package

The table shows **Option B (tiered bundles)**, one possible packaging of the same modules. Option A sells each module
separately. The bundle contents below are a **proposal**, not shipped: the product knows only modules, not bundle names.

| | Core | Team | Business | Enterprise |
|---|:---:|:---:|:---:|:---:|
| Core (time, expenses, leave, projects, Plan, sheets, dashboards, audit log, importers, file exports) | ✓ | ✓ | ✓ | ✓ |
| Billing | | ✓ | ✓ | ✓ |
| Accounting | | ✓ | ✓ | ✓ |
| Capture | | | ✓ | ✓ |
| Workforce | | | ✓ | ✓ |
| Project controls | | | ✓ | ✓ |
| Materials | | add-on | add-on | add-on |
| Government contracting | | add-on | add-on | add-on |
| Enterprise (SSO, SCIM, multi-company, integrations, custom approvals, automation, API keys, audit retention) | | | | ✓ |
| AI assistant | with your own API key, every package | | | |
| Seat limit | optional cap `[?]` | `[?]` | `[?]` | `[?]` |
| Support | community | email | email, business hours | priority, [see service levels](service-levels-draft.md) |
| Hosted option | `[?]` | ✓ | ✓ | ✓ |

Government contracting and Materials are add-ons because they are the most differentiated and the parts most customers do not
need. A firm can also be sold **any single module** (Option A): `billing` alone, or `capture` alone, on top of core.

## Seat types

| | Full seat | Light seat |
|---|---|---|
| Defined as | anyone whose roles grant more than the built-in **Employee** role (any other built-in role, or a custom role with at least one permission) | a person who holds **only** the Employee role's access: their own time, expenses and leave |
| Typical person | managers, finance, HR, BD, administrators | field staff, consultants, hourly employees, subcontractor staff |
| Price | `[$? per person per month]` | `[$? per person per month]`, suggested 15 to 30% of a full seat (a placeholder ratio, not a recommendation) |
| Why it exists | | A firm with 400 people and 60 managers should not pay 400 full seats. Time capture is what everyone does, and light seats make the price defensible to a CFO. |

Rules already in the code: the seat type is **worked out from roles**, so it needs no setting and changes when roles do. A light
person takes a light seat while one is free and a full seat once the light seats are used up. API keys take no seat. Pending
invitations hold a seat; deactivating someone frees it at once. Old (WB1) keys have no light seats, so everyone counts as full.

### Module seats

On top of full and light seats, a key may carry a **seat count for each module**. A person **uses a seat of a module when their
roles grant any permission in it** (an administrator holds every permission, so uses a seat in every module). When a key has no
per-module counts, its `seats` number limits every enforced module it includes. Only enforced, included modules are limited.

What this means for pricing: you can sell `accounting` for 10 people and `billing` for 5 on a 25-person license, and the
product refuses to add an eleventh accounting person. The choice for the owner is whether to price **per module per person
using it** (Option A, fits module seats exactly) or **per person overall** (Option B).

## Trials

- A trial is a normal key with `trial: true` and a short life (suggested `[30]` days, a placeholder), usually listing the
  modules being evaluated, or all of them. On the hosted service it is a separate tenant, and converting keeps the tenant.
- The admin sees days remaining in Setup › License, and a banner on every page when 7 days or fewer remain.
- **A lapsed trial falls back to Community at once, with no grace period** (the 14-day grace is for paying customers who forgot
  to renew). A lapsed trial key cannot be entered again. **Data is untouched**, and a customer who does not convert keeps core
  and all of their records.
- Decide: whether sales may extend a trial (needs a new key, or the license server), and whether a trial may hold production
  data. The [terms draft](end-user-terms-draft.md) assumes it may, and the data is the customer's.

## Offline keys and the connected license flow

| | Offline key (self-hosted) | Connected (license server) |
|---|---|---|
| How it works | You are sent a signed key (`WB2.…`; older `WB1.…` keys still work and mean every module). The server checks the signature against the public key built into the product. It never contacts anyone. | The server asks the vendor's license service (outbound HTTPS only) for its current entitlements and caches the answer. |
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

- For: simple to explain ("you pay for what you use"), easy to start small, friendly to consultancies that only want billing or accounting.
- Against: many SKUs; a quote becomes a spreadsheet; discounts are negotiated per module; customers under-buy and then work around gaps.

**Option B: tiered bundles.** Core, Team, Business and Enterprise as above, with Materials and Government contracting as add-ons.

- For: a short price page, predictable revenue per seat, an obvious upgrade path, easier for a reseller or a procurement office.
- Against: customers will feel they pay for modules they do not use; a firm needing one Enterprise feature (SSO) is pushed into the top tier.

**Recommendation to consider (not a decision):** sell Option B publicly, keep Option A internally so sales can quote an exception,
and put SSO in a tier low enough that nobody calls it a tax. Buyers of government software expect to buy a compliance pack
separately; commercial buyers expect a short list. Either option works with the licensing code as designed.

## Pricing worksheet

Fill in the right-hand columns. Everything in brackets is a placeholder.

| Line | Unit | Option A (à la carte) | Option B (bundle) |
|---|---|---|---|
| Core | per person per month | free forever | free forever |
| Full seat, base | per person per month | `[$?]` for core plus one module | Team `[$?]`, Business `[$?]`, Enterprise `[$?]` |
| Light seat | per person per month | `[$?]` | `[$?]` |
| Billing | per person using it per month | `[$?]` | included from Team |
| Accounting | per person using it per month | `[$?]` | included from Team |
| Capture | per person using it per month | `[$?]` | included from Business |
| Workforce | per person using it per month | `[$?]` | included from Business |
| Project controls | per person using it per month | `[$?]` | included from Business |
| Materials | per person using it per month | `[$?]` | `[$?]` add-on |
| Government contracting | per person using it, or per company | `[$?]` | `[$?]` add-on |
| Enterprise | per person per month | `[$?]` | included in Enterprise |
| AI assistant | with the customer's own API key | free | free |
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
4. **Source-available, so self-hosted gates are an honor system.** A customer can read and change the code; the FSL stops
   only a competing service. So price on convenience, support, updates, compliance evidence and the hosted service, which is
   where enforcement is real. Do not price on the belief that a self-hosted gate cannot be removed.
5. **Free Core.** It is the adoption engine. Decide whether Core stays free for hosted use too (it costs you hosting) or
   self-host only.

### Honest rationale

- **Honor system on self-hosted:** most self-hosting customers pay because they want support, updates and a clear conscience,
  not because they cannot switch a flag. That is normal for source-available software, but plan revenue around it.
- **Light seats** are the single biggest lever. Without them the product is expensive exactly where it should be cheapest: the
  hourly workforce that fills in timesheets.
- **Government contracting as an add-on** keeps commercial prices lower and puts the price of compliance on those who need it.
- **Annual terms, billed in advance,** are the norm; month-to-month at a premium is a conversion tool.
- A number picked now will be wrong. Set a launch price, state that early customers keep it for `[?]` years, and review
  after the first `[10]` customers.

## What does not exist yet

- Payment, invoicing and renewals ([Editions](../editions.md) says so). Until that exists, sell by invoice.
- Tax handling, reseller terms, and trial self-service.
- A published refund policy. The [terms draft](end-user-terms-draft.md) proposes one; decide it.
