# Editions and licensing

Workbench has two editions.

- **Community** is free to self-host. It is a complete system for one company: time, expenses, projects, the Plan, sheets and
  dashboards, billing, the general ledger, purchasing, HR, the audit log, DCAA/CAS readiness and the importers.
- **Business** adds what larger firms need, and is licensed **per person**: every active user takes a seat.

## What needs Business

| Feature | What is gated |
|---|---|
| More than one company | Creating a second company (a first one always exists) |
| Single sign-on | Saving the organization's identity provider |
| Integrations | Creating or changing connectors, and saving Jira, GitLab, Slack and Teams connections |
| Customer view links | Creating a link |
| Supplier portal | Adding a contact, sending a sign-in link, reactivating a contact |
| Custom approval workflows | Publishing a workflow or adding a record workflow (standard timesheet, leave and expense approvals stay free) |
| Incurred cost submission | Generating or regenerating a package |

Gates apply to **changing things only**. Reading what is already there is never gated, so a lapsed license cannot lock anyone
out of their own records, and nothing is deleted. The list lives in one place, `EditionGates.cs`; to gate another action, add a
line there and a row to `FeatureCatalog` in `Licensing.cs`.

The audit log, DCAA readiness pages and the importers are deliberately free: a firm has to be able to show an auditor its
books and get its data in and out whatever it pays.

## Seats

A **seat** is any active person who can sign in, whatever their role: an employee who only enters time takes one, and so does an
administrator. One price per person, one count. API keys are not people and do not take a seat. Community has no seat limit.

A Business license covers a number of seats. Adding a person (an invitation, a created user, or reactivating someone) when all
seats are in use is refused with a message that says what to do; pending invitations hold a seat until they are accepted,
revoked or expire. Deactivating someone frees their seat at once. Changing someone's roles costs nothing.

Not enforced: a person who signs in for the first time on a server that allows open sign-up (no invitation needed, the
development default) is counted but not refused. Production servers require invitations by default.

## Where the edition comes from

In this order:

1. `License:EditionOverride = Business` in configuration. For the public demo, development, and trials you run yourself.
   `appsettings.Demo.json` and `appsettings.Development.json` set it. **Leave it unset in production.**
2. The hosting plan. In MultiTenant mode a tenant whose catalog `Plan` is `business` (set by the host administrator) is Business.
3. A license key entered under **Setup › License** (self-hosted).
4. Otherwise Community.

An expired key keeps Business working for 14 days with a warning, then falls back to Community. Nothing is lost.

## License keys

A key looks like `WB1.<payload>.<signature>`. The payload (licensee, edition, seats, expiry) is signed with ECDSA P-256 and the
server checks the signature offline against the public key built into the product, so a self-hosted server never contacts
anyone. Editing a key breaks the signature.

### Issuing keys

The signing key pair is made once and kept by the owner of the product. The **private key must never be committed or shared**;
anyone who has it can issue licenses.

```bash
dotnet run tools/license.cs -- keygen ~/.workbench-license      # once; prints the public key
dotnet run tools/license.cs -- issue ~/.workbench-license/private.pem "Acme Federal" 25 2027-11-01
```

The public key is `LicenseOptions.DefaultPublicKeyPem` (or `License:PublicKeyPem` in configuration). If the private key is
lost, no new keys can be issued for servers that carry that public key, so back it up somewhere safe.

## What this does not do yet

- **It does not take payment.** Keys are issued by hand and hosted plans are set by the host administrator. Card billing,
  invoices, renewals and a self-service purchase page are separate work.
- **SCIM provisioning is not gated** (it authenticates with its own token, outside the edition check).
- **Open sign-up** (off in production) counts new people but does not refuse them when seats run out.
- **Terms.** The software license (what people may do with the source), the end-user terms and the price list are not
  written. Choose and have a lawyer review them before selling anything. A source-available license that forbids offering the
  software to others as a hosted service (for example BSL) fits a free self-hosted edition best; an unrestricted open-source
  license would let a competitor host it and undercut the paid edition.
