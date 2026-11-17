# Editions and licensing

Workbench has two editions, and a license says which **modules** of Business it includes.

- **Community** is free to self-host. It is a complete system for one company: time, expenses, projects, the Plan, sheets and
  dashboards, billing, the general ledger, purchasing, HR, the audit log, DCAA/CAS readiness and the importers. The modules below
  describe how it is divided so that pricing can be set per module later; today only the features marked "yes" need a license.
- **Business** adds what larger firms need, and is licensed **per person** (full and light seats, below) for the modules in the key.

## Modules

A license key lists the modules it includes; `core` is always included and is free forever. The catalog is `ModuleCatalog` in
`src/Workbench.Domain/Licensing/Licensing.cs`, **and its `Enforced` flags are the one place pricing is tuned**.

| Module | Id | What it covers | Enforced today |
|---|---|---|---|
| Core | `core` | Time, timesheets with the standard approval, basic expenses and receipts, leave, My Work, notifications, customers, agreements and contracts, projects and WBS, the Plan with Gantt and assigning people to tasks, sheets, dashboards, search, the standard project, timesheet and HR-directory reports, users, roles, MFA, the audit log, importers and file exports of your own data | always on |
| Billing | `billing` | Invoices, ready to bill, customer payments, AR aging, unbilled accrual, milestones, retainers and recurring billing, revenue recognition, the commercial invoice, live accounting push (QuickBooks and Xero) | no |
| Accounting | `accounting` | General ledger, period close, recurring journals, chart of accounts, posting rules, payables, purchasing, requests for quote, payment runs, cash and bank reconciliation, fixed assets, sales tax, multi-currency, corporate budgets and planning cycles, indirect rates | no |
| Capture | `capture` | The pipeline: opportunities, accounts, leads, forecast, gates, proposals and pricing, the shredder, deal import | no |
| Workforce | `workforce` | HR records, positions, compensation, benefits, onboarding, payroll, Paylocity, and cross-project capacity, utilization and skills (assigning people to tasks in the Plan stays core) | no |
| Project controls | `projectcontrols` | Earned value, the risk register, ceilings and closeout, schedule health, the monthly narrative | no |
| Materials | `materials` | Parts, inventory, bills of materials, work orders, MRP, kits, quality, engineering changes, sales orders, shipping, landed cost | no |
| Government contracting | `govcon` | DCAA readiness, CPSR, CAS disclosure, CMMC, incurred cost, CLIN funding reports, WAWF, total time accounting, the Costpoint parallel run | **yes**, for its named features |
| Enterprise | `enterprise` | Single sign-on and SCIM, more than one company, integrations, customer links, custom approval workflows, the automation builder, layouts and custom fields, API keys, audit retention, legal holds and access reviews | **yes**, for its named features |
| AI credits | `ai` | Reserved for a future hosted-credits product. Nothing is built or billed | no |

- **The AI assistant is free when your organization supplies its own API key** (Admin › Assistant), on every edition. The `ai`
  id exists only so a future hosted-credits product has a place in keys without a format change. It is not enforced.
- **Enforced = false**: everyone has the module whatever the license says, so nothing changes. Every module except `govcon` and
  `enterprise` ships this way: everything Community had before the split stays free until pricing is decided.
- **Enforced = true**: only a license that lists the module has it. Flip a flag in the table and the whole mechanism follows: the
  module's pages leave the menu and the command palette, opening the URL shows "needs the X module" with what to do (nothing is
  deleted), and **every change to the module's services is refused** (see "One table" below). Reads are never refused.
- **`WholeModule`** (a second field on a module, default true) only matters when `Enforced` is true. `govcon` and `enterprise` are
  enforced with `WholeModule = false`, which means exactly what they meant before the split: only their named **features** (next
  table) need a key that lists the module, and their other pages and services (DCAA readiness, layouts, API keys, the audit
  retention pages…) stay open to everyone. Setting `WholeModule = true` on them gates all of it; that is a pricing decision that has
  not been made.
- **Features** (the table below) belong to a module. A feature's gate is always on, as before, so the Supplier portal (Accounting)
  needs a key that lists `accounting` even though the Accounting module's pages are open to everyone.
- **WB1 keys and the configuration override** (`License:EditionOverride`, a hosted plan) mean **every module**.
- **Keys issued before the split keep working.** A module id from the old catalog in a WB2 key is mapped when the key is read:
  `finance` means `billing` and `accounting`, and `controls` means `enterprise`. Nothing needs reissuing, and the license server's
  output is accepted unchanged. (The old `controls` meant audit retention, legal holds and access reviews, which are now part of
  `enterprise`; pages that earlier drafts called "Controls", earned value and the risk register, are `projectcontrols`.)

### One table

`ModuleMap` (`src/Workbench.Contracts/Licensing/ModuleMap.cs`) says which module each of these belongs to, exactly one each:

| What | Keyed by | Used for |
|---|---|---|
| API paths | first path segment after `/api/v1/` | Coverage only: a new path can't be added without a decision. |
| Menu entries | page, else group, else section | Hiding pages of an enforced module (`NavEntry.ModuleId`). |
| Permissions | key prefix (longest wins) | Module seats: a person uses a module's seat when their roles grant any permission in it. |
| Services | Contracts namespace, else the interface | The module gate on every change to the service. |

`ModuleMapTests` fails for any API path, menu entry, permission or service interface that appears in none, so a feature can't ship
unlicensed by accident. When you add a page, route, permission or service, add it to the table (usually it already inherits from
its group or namespace). Moving something between modules is a pricing decision.

**How the service gate works.** For every service interface whose module isn't `core`, `EditionGates.AddWorkbenchModuleGates`
wraps the registered service so every method that **changes** something first calls `FeatureGate.RequireModuleAsync(module)`. A
method is a read, and never gated, when its name starts with a reading verb (`Get`, `List`, `Find`, `Search`, `Preview`, `Export`,
`Check`, `Is`, `Can`, `Has`, `Lookup`, `Validate`, `Explain`, `Reveal`, `Download`, `Calculate`, …: `ModuleMap.IsChange`). A module
that isn't enforced costs one flag check and reads nothing from the database. Methods that aren't `Task`-returning (a few `Get…Url`
helpers and the assistant's event stream) are not gated.

## Seats

A **seat** is any active person who can sign in. There are two kinds, and the key says how many of each:

- **Full seat**: anyone whose roles grant more than the built-in Employee role (any other built-in role, or a custom role with
  at least one permission).
- **Light seat**: a person who holds only the Employee role's access: they enter their own time, expenses and leave. This is
  worked out from their roles, so it needs no setting and changes when their roles do. A light person takes a light seat while
  one is free and a full seat once the light seats are used up; a key with no light seats (every WB1 key) counts everyone as full.

API keys are not people and take no seat. Community has no seat limit unless the operator sets one (below).

### Module seats

Beyond full and light seats, a **module** can have its own seat count. A person **uses a seat of a module when their roles grant
any permission that `ModuleMap` puts in that module** (Admin › Roles lists permissions by area; the Administrator role holds every
permission, so an administrator uses a seat in every module). `core` has no module seats: its seats are the full and light seats
above, and the light seat stays exactly as it was (only the built-in Employee role's access).

- A key's optional **`moduleSeats`** object gives seats per module (see the payload below). **When it is absent, `seats` is the
  limit for every enforced module the key includes**, so every key issued before this existed behaves as it did: nobody can be over
  `seats` in a module without being over `seats` overall.
- Only **enforced** modules the license **includes** are limited. A module that isn't enforced has no seat limit.
- Setup › License shows seats used and allowed per module.
- **Adding a person is refused when it would exceed a module's seats**: an invitation, a created user, reactivating someone, and an
  administrator changing someone's roles so they reach a module they didn't (someone who already uses the module keeps their seat
  when their roles change). The message names the module and says what to do: give the person roles without that module's
  permissions, deactivate someone who has left, or add seats under Setup › License. Pending invitations hold a seat.
- Not refused: accepting an invitation (it already held its seat), roles synced from an identity provider's groups, and open
  sign-up, so a sign-in is never blocked.

### Community cap

`License:CommunityMaxPeople` (default `0`, **unlimited**, so nothing changes unless an operator sets it). When it is greater than
zero and the server runs with **no license key** (Community), adding a person (invitation, created user, reactivation) is refused
once active people plus pending invitations reach the cap, with a message that says to deactivate someone or enter a license key.
It never applies to a server with a key (including one that has since expired), the configuration override or a hosted plan, and
changing roles never counts as adding someone.

Adding a person (an invitation, a created user, or reactivating someone) when no seat is free for them is refused with a message
that says what to do; pending invitations hold a seat until they are accepted, revoked or expire. Deactivating someone frees
their seat at once. Changing someone's roles costs nothing, but it can move them between light and full.

Not enforced: a person who signs in for the first time on a server that allows open sign-up (no invitation needed, the
development default) is counted but not refused. Production servers require invitations by default.

## Where the edition comes from

In this order:

1. `License:EditionOverride = Business` in configuration. For the public demo, development, and trials you run yourself.
   `appsettings.Demo.json` and `appsettings.Development.json` set it. **Leave it unset in production.**
2. The hosting plan. In MultiTenant mode a tenant whose catalog `Plan` is `business` (set by the host administrator) is Business.
3. A license key entered under **Setup › License** (self-hosted).
4. Otherwise Community.

An expired key keeps Business working for 14 days with a warning, then falls back to Community. Nothing is lost. A **trial**
key has no grace period (see below).

## License keys

Both formats are `<prefix>.<payload>.<signature>`, the payload JSON and the signature both base64url (no padding). The signature
is ECDSA P-256 over the **payload bytes exactly as written** (SHA-256, IEEE P1363 `r||s`, 64 bytes), checked offline against the
public key built into the product, so a self-hosted server never contacts anyone. Editing a key breaks the signature.

### WB2 (current)

```json
{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme Federal","issuedAt":"2026-10-01","expires":"2027-10-01","seats":25,"lightSeats":10,"modules":["billing","accounting","capture"],"moduleSeats":{"accounting":10,"billing":5},"trial":false}
```

| Field | Meaning |
|---|---|
| `v` | `2` |
| `licenseId` | GUID string identifying this license for the issuer |
| `licensee` | Who it is for |
| `issuedAt`, `expires` | ISO dates (`yyyy-MM-dd`). Valid through the whole of `expires` |
| `seats` | Full seats |
| `lightSeats` | Light seats; optional, default 0 |
| `modules` | Module ids; `core` is implied, an id this build doesn't know is ignored (so a newer key still opens on an older server). The legacy ids `finance` (= `billing` + `accounting`) and `controls` (= `enterprise`) are still read |
| `moduleSeats` | Optional object, module id → whole number of seats for that module (see Module seats). Absent: `seats` for every module. Ids go through the same legacy mapping as `modules` (an explicit current id wins over a legacy one); an unknown id, a non-number or a negative number is ignored or read as 0. A module in `moduleSeats` but not in `modules` has no effect |
| `trial` | `true` for a time-boxed trial; optional, default false |

Servers that predate `moduleSeats` ignore the field (the payload is signed as written, unknown members are skipped), so a key that
carries it still opens everywhere; it just isn't enforced there.

### WB1 (still accepted)

`{"v":1,"licensee":"…","edition":"business","seats":25,"expires":"2027-11-01","issued":"…"}`. It means **every module**, no light
seats, and keeps working exactly as before. Existing customers need no new key.

### Trials

A key with `trial: true` is a normal key with a short life. Setup › License shows the days left, and administrators see a banner
on every page when 7 days or fewer remain, or once it has lapsed. A lapsed trial falls back to Community **at once**: there is no
14-day grace (the grace period is for customers who forgot to renew), and a lapsed trial key can't be entered. Data is untouched.

### Issuing keys

The signing key pair is made once and kept by the owner of the product. The **private key must never be committed or shared**;
anyone who has it can issue licenses.

```bash
dotnet run tools/license.cs -- keygen ~/.workbench-license      # once; prints the public key
# WB2: 25 full seats, 10 light seats, two modules, until 2027-11-01
dotnet run tools/license.cs -- issue2 ~/.workbench-license/private.pem "Acme Federal" 25 2027-11-01 --light 10 --modules billing,accounting,capture
# the same, with 10 accounting seats and 5 billing seats (the others fall back to the 25)
dotnet run tools/license.cs -- issue2 ~/.workbench-license/private.pem "Acme Federal" 25 2027-11-01 --light 10 --modules billing,accounting,capture --module-seats accounting=10,billing=5
# a 30-day trial of everything (--modules all), 5 seats
dotnet run tools/license.cs -- issue2 ~/.workbench-license/private.pem "Prospect Inc" 5 2026-12-17 --modules all --trial
# the old WB1 format (every module), kept for older customers
dotnet run tools/license.cs -- issue ~/.workbench-license/private.pem "Acme Federal" 25 2027-11-01
```

`issue2` also takes `--id <guid>` (otherwise a new one is made). Module ids are `billing`, `accounting`, `capture`, `workforce`,
`projectcontrols`, `materials`, `govcon`, `enterprise` and `ai` (reserved); `core` is implied.

The public key is `LicenseOptions.DefaultPublicKeyPem` (or `License:PublicKeyPem` in configuration). If the private key is
lost, no new keys can be issued for servers that carry that public key, so back it up somewhere safe.

## What this does not do yet

- **It does not take payment.** Keys are issued by hand and hosted plans are set by the host administrator. Card billing,
  invoices, renewals and a self-service purchase page are separate work.
- **SCIM provisioning is not gated** (it authenticates with its own token, outside the edition check).
- **Open sign-up** (off in production) counts new people but does not refuse them when seats run out.
- **Terms.** The end-user terms and the price list are not written yet. Have a lawyer review them before selling
  anything.

## Source code license

The source is available under the [Functional Source License 1.1, ALv2 Future License](https://github.com/finepointcgi/Workbench/blob/master/LICENSE.md)
(FSL-1.1-ALv2). You may use, change and self-host Workbench for any purpose except offering a product or service that
competes with it. Each version becomes available under the Apache License 2.0 two years after it is released. The
edition gates above are separate: they decide which features a running server unlocks, not what you may do with the
source.
