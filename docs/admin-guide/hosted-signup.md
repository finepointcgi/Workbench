# Hosted signup, trials and plan changes

This page is for the **operator of a hosted (MultiTenant) Workbench**: the company that runs the servers and sells the
service. It covers how a customer starts a trial by themselves, how their plan follows what they buy, what happens when a
trial or subscription ends, and what you, the host administrator, can do from `/host`. A self-hosted Workbench has none of it.

## The flow

1. The visitor opens the base domain (for example `https://workbench.example/signup`). The form asks for the company
   name, the address they want (`their-address.workbench.example`), their name and work email, the company type and a tick
   to accept the terms. There is no password.
2. Workbench creates the organization: its own database, the default chart of accounts and fiscal calendar, **an empty
   organization** (no sample data) and the first administrator. The trial starts at once: **14 days, every module**
   (the "Business" trial), 10 full and 10 light seats.
3. The administrator gets an email with a **one-time sign-in link**. The link first shows a button (so a mail scanner that
   opens it can't use it up); pressing it signs them in and lands on **Admin › Setup**, whose *Getting started* checklist
   is ready. The link works once and expires after 60 minutes. If it is lost, submitting the form again with the same
   address and email sends a new one; older links stop working.
4. During the trial, a banner tells administrators when 7 days or fewer remain. When it ends, the organization becomes
   **read-only** (below) until you subscribe it, extend the trial, or change its status.

What is recorded in the catalog for each signup: the email, where the visitor came from (`?source=` or `?utm_source=` on the
link, else `direct`), when they accepted the terms and which terms version, and the company type. Every signup also writes
`signup.accepted` to the host audit log.

### Safe to repeat

- The same person submitting the form twice (a double click, a retry) never creates a second organization: the second
  submit finds theirs and re-sends the sign-in link.
- Someone else asking for an address that exists is told it is taken (and nothing about whose it is).
- Two people racing for one address: one process-wide lock plus the catalog's unique index on the subdomain let exactly one in.
  Subdomains are **never reused**, even after a failed provisioning (the operator removes the row; see
  [Hosting and operations](hosting.md)).
- Subdomains are 3 to 40 lowercase letters, digits and single hyphens, starting with a letter, and can't be on the reserved
  list (`www`, `admin`, `api`, `billing`, `support`, `login`, `mail`, `status`, and others in `SignupService`), `xn--` names or
  anything containing `--`.

## What the host operator configures

Everything is under `Tenancy:Signup` (environment variables use `__`, e.g. `Tenancy__Signup__Enabled`). Signup is **off by
default**, and has no effect unless `Tenancy:Mode` is `MultiTenant`.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turns on `/signup` on the base domain (and makes `/` open it instead of `/host`) |
| `TrialDays` | `14` | Length of the trial |
| `TrialModules` | all six | Module ids in the trial |
| `TrialSeats`, `TrialLightSeats` | `10`, `10` | Full and light seats in the trial |
| `TermsUrl` | empty | Where the "terms of service" link points. Empty: the checkbox shows without a link |
| `TermsVersion` | `draft-1` | Recorded with each consent. Change it when the terms change |
| `MaxPerIpPerHour` | `5` | Signup attempts per client address per hour |
| `MaxPerEmailPerDay` | `3` | Attempts (or resends) per email address per day |
| `MagicLinkMinutes` | `60` | How long the emailed link works |
| `GraceDays` | `7` | Days of full access after a payment fails or a paid term ends, before read-only |
| `RetentionDays` | `90` | How long a read-only organization's data is kept before a host admin *may* delete it (below) |
| `EntitlementApiKey` | unset | The shared secret the license server uses (below). At least 32 characters. Unset: the endpoint doesn't exist |

You also need what any hosted deployment needs: `Tenancy:BaseDomain`, `Tenancy:TenantConnectionString`, `Tenancy:HostAdmins`,
working outbound email (the sign-in link is an email, see [Email](../operations/email.md)) and TLS with a wildcard certificate.
The company-type field is stored as text; wiring it to the organization's company-type setting is left as a TODO until that
setting lands in the base branch.

## Plans, modules and seats: the entitlement

Each hosted tenant has one **entitlement** row in the catalog: plan, modules, seats and light seats, status
(`active`, `past_due`, `suspended`, `cancelled`), the trial flag and its end, the paid-through date and who last wrote it.
The product reads it (cached for 30 seconds, and at once on the server that changed it) in place of the old catalog `Plan`
field, which still works for a tenant that has no entitlement:

| Entitlement | What the organization gets |
|---|---|
| `active`, in term or trial | **Business** with the listed modules and seats (the same model as a WB2 license key, see [Editions](../editions.md)) |
| `past_due`, or the paid term ended | Full access with a warning to administrators until `GraceDays` have passed, then read-only |
| trial ended | Read-only |
| `suspended` or `cancelled` | Read-only |

(`License:EditionOverride`, used in development and the demo, still wins over everything. Leave it unset in production.)

### Read-only mode

A read-only organization keeps its people, sign-in and every record. What changes:

- Every **save** that changes business data is refused with a message that says why (HTTP 402 from the API). Reading is never
  blocked. People can still sign in, and the security log and one-time links keep working.
- A banner on every page tells everyone why, and gives administrators **Export all my data**.
- Background jobs skip the organization.
- Business features are off (the same as an expired license), but **nothing is hidden or deleted**.

**Export:** `GET /api/v1/organization/export` (organization administrators only, always available, written to the security log)
downloads one zip with a JSON-lines file per table and a `manifest.json` with row counts. Password hashes, authenticator and
recovery secrets, one-time link and API key hashes are left out. Check the manifest for a table with an `error`.

**Retention:** Workbench **never deletes a tenant's data by itself.** The host page shows the date after which a read-only
organization's data may be deleted (`RetentionDays` after the lapse). Deleting is a deliberate act by the host administrator
(drop the tenant's database and the catalog row) and is not yet a button: it is a documented manual step. Lifting the lapse
(subscribing, extending the trial, setting the status back to `active`) restores full access at once.

## The license server calls Workbench

The license server (its own guide, `docs/admin-guide/license-server.md`, arrives with that server) holds the subscriptions and talks to Stripe. It tells each hosted
Workbench what a tenant is entitled to by calling the host site (the base domain, never a tenant's address):

```
PUT  https://workbench.example/api/v1/host/entitlements/{slug}     set (idempotent)
GET  https://workbench.example/api/v1/host/entitlements/{slug}     read one
GET  https://workbench.example/api/v1/host/entitlements             read all
X-Host-Key: <Tenancy:Signup:EntitlementApiKey>
```

The body is a subset of the license server's `Subscription`; every field is optional and a missing one is left as it is:

```json
{
  "plan": "business",
  "modules": ["finance", "capture"],
  "seats": 25,
  "lightSeats": 10,
  "status": "active",
  "trial": false,
  "trialEndsUtc": null,
  "paidThroughUtc": "2026-12-16T00:00:00Z",
  "externalRef": "sub_1Abc...",
  "eventAtUtc": "2026-11-16T12:00:00Z",
  "reason": "Stripe invoice.paid"
}
```

| License server (`Subscription`) | Hosted entitlement |
|---|---|
| `Modules` (comma list), `Seats`, `LightSeats` | `modules`, `seats`, `lightSeats` (unknown ids are dropped; `core` is implied) |
| `Status` `Active` / `Trialing` / `PastDue` / `Canceled` | `status` `active` (with `trial: true` for `Trialing`) / `past_due` / `cancelled` |
| `TermEndUtc` | `paidThroughUtc` (or `trialEndsUtc` while trialing) |
| `StripeSubscriptionId` | `externalRef` |
| the Stripe event's time | `eventAtUtc`: an update older than the last one applied is **ignored**, so redelivered or reordered events can't undo a newer one |

Answers: `200` with `{ tenantId, slug, entitlement }`, `400` for a bad status, `401` for a missing or wrong key, `404` when the
tenant doesn't exist (or the key isn't configured, or the call came to a tenant's address). The same body twice leaves the same
state. Each change is written to the host audit log with actor `license-server`.

The license server needs to know which tenant a subscription belongs to (the slug). As of this writing it has no such field and
no client for this call; adding both (a `HostTenant` on the customer or subscription, and a push after each Stripe event) is the
work on its side. Until then a host administrator sets entitlements in `/host`.

## Host administration

`/host` (host administrators only, on the base domain only; a tenant's Admin role grants nothing here) lists every tenant
with its plan, entitlement status (read-only is flagged), trial end and **seats in use against seats bought** (full, and light).
**Manage** opens: seats, light seats, status, modules, a reason, and **Extend trial** (by N days, from today when it has already
ended, which lifts a lapse). **Suspend** and **Reactivate** are the existing switch that makes a tenant unreachable (403) rather
than read-only: use it for abuse or a security incident, not for non-payment. Every action is in the host audit log with who did
it and, when given, why (`entitlement.create`, `entitlement.update`, `tenant.suspend`, `tenant.reactivate`).

## Security considerations

- **Bot protection** is a hook: `ISignupBotProtection`. The default is a hidden honeypot field plus a signed timestamp (a form
  posted in under 3 seconds, or after 4 hours, is refused). A refused form looks like a success so a bot learns nothing. To add
  a CAPTCHA, register your own implementation (it can read its response field from the form) before the default. No vendor is built in.
- **Rate limits** by client address and by email count every attempt and are kept in memory **per server instance**. Behind
  several instances, add a shared limit at the load balancer. Configure forwarded headers so the address is the visitor's, not the proxy's
  (see [Hosting and operations](hosting.md)).
- Signup can be used to **send one email to any address**, so it is limited per address and the email says only that a link was
  asked for. Subdomain probing is possible by design (the form says "taken").
- The sign-in link is a 256-bit random token; only its hash is stored; it works once, expires, and its GET page does not consume it.
  It signs in as the first administrator only, for the address that signed up.
- The `X-Host-Key` is a shared secret: keep it in a secret store, rotate it by changing the setting on both servers, and expose
  the host site's `/api/v1/host/entitlements` only to the license server's address if you can. It is compared in constant time.
- A read-only tenant can still be read by its own users and exported by its administrators; it is not a lock-out.
- The export is a complete copy of the tenant's data: it is written to the security log, and only organization administrators can call it.
- Records of consent are in the catalog (terms version and time); keep the terms' text per version yourself.

## Not covered

No payment page (Stripe checkout is the license server's), no automatic deletion, no per-signup abuse scoring, and email delivery
is whatever the deployment's email setting does: check it with a real mailbox before opening signup to the public.
