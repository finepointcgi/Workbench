# Editions and licensing

Workbench has two editions, and a license says which **modules** of Business it includes.

- **Community** is free to self-host. It is a complete system for one company: time, expenses, projects, the Plan, sheets and
  dashboards, billing, the general ledger, purchasing, HR, the audit log, DCAA/CAS readiness and the importers.
- **Business** adds what larger firms need, and is licensed **per person** (full and light seats, below) for the modules in the key.

## Modules

A license key lists the modules it includes; `core` is always included. The catalog is `ModuleCatalog` in
`src/Workbench.Domain/Licensing/Licensing.cs`, **and its `Enforced` flags are the one place pricing is tuned**.

| Module | Id | What it covers | Enforced today |
|---|---|---|---|
| Core | `core` | Time, expenses, leave, projects, the Plan, sheets and dashboards | always on |
| Finance | `finance` | Billing, general ledger, payables, purchasing, supplier portal | no |
| Capture | `capture` | The pipeline: leads, opportunities, accounts, forecast, proposal pricing | no |
| Workforce | `workforce` | HR administration, positions, benefits, onboarding, payroll | no |
| Controls | `controls` | Audit log, DCAA readiness and the other compliance pages | no |
| Government contracting | `govcon` | Incurred cost submissions | **yes** |
| Enterprise | `enterprise` | More than one company, single sign-on, integrations, customer links, custom approval workflows | **yes** |

- **Enforced = false**: everyone has the module whatever the license says, so nothing changes. Finance, Capture, Workforce and
  Controls ship this way: everything Community has today stays free until pricing is decided.
- **Enforced = true**: only a license that lists the module has it. Flip a flag in the table and the whole mechanism follows:
  the module's pages leave the menu and the command palette, opening the URL shows "needs the X module" with what to do
  (nothing is deleted), and `FeatureGate.RequireModuleAsync` refuses changes to it. A test flips Finance to prove it.
- **Features** (the table below) belong to a module. A feature's gate is always on, as before, so the Supplier portal (Finance)
  needs a key that lists `finance` even though the Finance module's pages are open to everyone.
- **WB1 keys and the configuration override** (`License:EditionOverride`, a hosted plan) mean **every module**.

## What needs Business

| Feature | Module | What is gated |
|---|---|---|
| More than one company | enterprise | Creating a second company (a first one always exists) |
| Single sign-on | enterprise | Saving the organization's identity provider |
| Integrations | enterprise | Creating or changing connectors, and saving Jira, GitLab, Slack and Teams connections |
| Customer view links | enterprise | Creating a link |
| Supplier portal | finance | Adding a contact, sending a sign-in link, reactivating a contact |
| Custom approval workflows | enterprise | Publishing a workflow or adding a record workflow (standard timesheet, leave and expense approvals stay free) |
| Incurred cost submission | govcon | Generating or regenerating a package |

Gates apply to **changing things only**. Reading what is already there is never gated, so a lapsed license cannot lock anyone
out of their own records, and nothing is deleted. The list lives in one place, `EditionGates.cs`; to gate another action, add a
line there and a row to `FeatureCatalog` in `Licensing.cs` (naming its module). Hiding the pages of an enforced module is a
navigation matter only: the data stays where it is. Pages of `enterprise` and `govcon` stay listed, since they only block
changing things. `NavEntry.ModuleId` says which module a page belongs to (the Finance and Business development sections, HR
administration, the audit log and the compliance pages; everything else is core).

The audit log, DCAA readiness pages and the importers are deliberately free: a firm has to be able to show an auditor its
books and get its data in and out whatever it pays.

## Seats

A **seat** is any active person who can sign in. There are two kinds, and the key says how many of each:

- **Full seat**: anyone whose roles grant more than the built-in Employee role (any other built-in role, or a custom role with
  at least one permission).
- **Light seat**: a person who holds only the Employee role's access: they enter their own time, expenses and leave. This is
  worked out from their roles, so it needs no setting and changes when their roles do. A light person takes a light seat while
  one is free and a full seat once the light seats are used up; a key with no light seats (every WB1 key) counts everyone as full.

API keys are not people and take no seat. Community has no seat limit.

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
{"v":2,"licenseId":"0f8b1c52-6e1a-4d0e-9a3b-5c1d2e3f4a5b","licensee":"Acme Federal","issuedAt":"2026-10-01","expires":"2027-10-01","seats":25,"lightSeats":10,"modules":["finance","capture"],"trial":false}
```

| Field | Meaning |
|---|---|
| `v` | `2` |
| `licenseId` | GUID string identifying this license for the issuer |
| `licensee` | Who it is for |
| `issuedAt`, `expires` | ISO dates (`yyyy-MM-dd`). Valid through the whole of `expires` |
| `seats` | Full seats |
| `lightSeats` | Light seats; optional, default 0 |
| `modules` | Module ids; `core` is implied, an id this build doesn't know is ignored (so a newer key still opens on an older server) |
| `trial` | `true` for a time-boxed trial; optional, default false |

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
dotnet run tools/license.cs -- issue2 ~/.workbench-license/private.pem "Acme Federal" 25 2027-11-01 --light 10 --modules finance,capture
# a 30-day trial of everything (--modules all), 5 seats
dotnet run tools/license.cs -- issue2 ~/.workbench-license/private.pem "Prospect Inc" 5 2026-12-17 --modules all --trial
# the old WB1 format (every module), kept for older customers
dotnet run tools/license.cs -- issue ~/.workbench-license/private.pem "Acme Federal" 25 2027-11-01
```

`issue2` also takes `--id <guid>` (otherwise a new one is made).

The public key is `LicenseOptions.DefaultPublicKeyPem` (or `License:PublicKeyPem` in configuration). If the private key is
lost, no new keys can be issued for servers that carry that public key, so back it up somewhere safe.

## Keeping a key current: offline or connected

An install is **offline by default**: with `License:ServerUrl` empty it never contacts anyone, and runs on the key it was given
(a long-lived offline key is the normal setup for defense and air-gapped sites). A **connected** install sets `License:ServerUrl`
to the vendor's license server and keeps its key fresh automatically; if the server cannot be reached, nothing changes, because
the key's own expiry (and the 14-day grace) still governs. See [License refresh](admin-guide/license-refresh.md).

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
