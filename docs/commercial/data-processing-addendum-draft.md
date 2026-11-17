# Data processing addendum (draft)

!!! danger "DRAFT: have a lawyer review before use"
    Not legal advice. This addendum is a template for the **Hosted** service (and for support access to a Self-Hosted
    installation). Privacy law differs by place (GDPR, UK GDPR, CCPA/CPRA and other US state laws, Canada, Australia).
    Placeholders are in `[square brackets]`. A lawyer must confirm which laws apply to the customers you will sell to and
    write the transfer mechanism (for example Standard Contractual Clauses) if any data leaves its region.

This addendum forms part of the [software license and subscription agreement](end-user-terms-draft.md) between
`[Vendor]` ("Vendor") and the Customer. If they conflict about personal data, this addendum controls.

## 1. Roles and scope

- For **Customer Data that contains personal data** (employees, contractors, timesheets, pay, bank details, applicants,
  contacts at customers and suppliers), **Customer is the controller** (or business) and **Vendor is the processor** (or
  service provider).
- For account and billing data about Customer's administrators, and for service security and operations data, Vendor is a
  controller (or business) in its own right and handles it under its privacy notice `[link]`.
- **Self-Hosted:** Vendor holds no Customer Data. This addendum applies only if Vendor staff are given access to it for support,
  and then only to that access.

## 2. What is processed

| | |
|---|---|
| Subject matter | Hosting and supporting Workbench for Customer |
| Duration | The Term, plus the export and deletion periods in the agreement |
| Types of personal data | Names, work emails, employee and contractor identifiers, timesheets, leave, expenses, pay rates, bank and tax identifiers, HR records, sign-in and security-event records `[confirm against what Customer actually loads]` |
| Special categories | None by design. Customer should not load health, biometric or similar data. `[Benefits and leave records may touch this; lawyer to decide.]` |
| People | Customer's employees, contractors, applicants and the contacts at its customers and suppliers |

## 3. Vendor's duties

Vendor will:

1. process personal data **only on Customer's documented instructions** (the agreement, the product's own settings, and
   support requests), and tell Customer if it believes an instruction breaks the law;
2. make sure people with access are bound by confidentiality and trained;
3. apply the security measures in Section 5;
4. help Customer answer requests from the people the data is about (access, correction, deletion, export), using the
   product's own exports where possible, and pass on any request it receives directly `[reasonable cost for extra work]`;
5. help Customer with impact assessments and regulator enquiries where the law requires, taking account of what Vendor
   knows;
6. not sell the data, not use it for advertising, and not use it to train AI models;
7. keep records of its processing and let Customer audit compliance under Section 8.

## 4. Sub-processors

4.1 Customer gives general authorization for the sub-processors in the table, which Vendor must bind to equivalent duties
and for whom it stays responsible.

| Sub-processor | What it does | Location | Notes |
|---|---|---|---|
| `[Cloud infrastructure provider]` | Servers, PostgreSQL, storage | `[region(s)]` | `[confirm]` |
| `[Backup storage provider]` | Encrypted backups | `[region]` | `[confirm]` |
| `[Email delivery provider]` | Notification and invitation email | `[region]` | `[or none if Customer supplies SMTP]` |
| `[Monitoring / error tracking]` | Logs and metrics | `[region]` | `[confirm logs hold no Customer Data]` |
| `[Payment processor]` | Billing of Customer's administrators only | `[region]` | Not Customer Data |
| `[Support tooling]` | Tickets | `[region]` | `[confirm]` |

4.2 Vendor will give Customer `[30]` days' notice of a new sub-processor, by email to the administrator contact and by updating
`[URL]`. Customer may object on reasonable data-protection grounds in that time; if they cannot be resolved Customer may
terminate the affected service and receive a pro-rata refund.

## 5. Security measures

Vendor keeps appropriate technical and organizational measures. The information-security management system behind them is the
ISO/IEC 27001-aligned policy set in the repository under
[`docs/iso`](https://github.com/finepointcgi/Workbench/tree/master/docs/iso) (it is not published on this site). **These are
the policies Vendor intends to operate; confirm each is actually in force, and evidenced, before this addendum is signed.**

| Area | Measure | Policy / reference |
|---|---|---|
| Access control | Role-based access; least privilege; named accounts; MFA for staff and, where Customer enables it, for its users; single sign-on and SCIM; periodic access reviews | `access-control-policy`, [Permissions](../security/permissions.md) |
| Encryption | TLS 1.2+ in transit; encryption at rest for databases and backups; secrets (bank numbers, tax IDs, keys) additionally encrypted by the application with keys held outside the database | `cryptography-policy`, [Data protection keys](../operations/data-protection-keys.md) |
| Logging | Security events and an append-only audit log; retention limits; exports for Customer's own review | `logging-monitoring-policy`, [Audit log](../admin-guide/audit-and-support.md) |
| Backups and recovery | Encrypted backups, tested restores, point-in-time recovery; RPO `[?]`, RTO `[?]` | `backup-policy`, [Backup and restore](../operations/backup-restore.md) |
| Development | Reviewed changes, automated tests, dependency and secret scanning, an SBOM for each release | `secure-development-policy` |
| Incident response | A written plan, on-call `[?]`, post-incident review | `incident-response-plan` |
| Suppliers | Security review of sub-processors | `supplier-security-policy` |
| Separation | One database per customer in the Hosted service | [Hosting](../admin-guide/hosting.md) |
| Staff | Background checks `[?]`, confidentiality, security training `[?]` | `[HR policy: not yet written]` |
| Third-party assurance | `[SOC 2 Type I/II report, ISO 27001 certificate: none yet]` | [SOC 2 readiness](soc2-readiness.md) |

## 6. Personal data breach

6.1 Vendor will notify Customer's security contact **without undue delay and within `[48]` hours** of becoming aware of a breach
affecting Customer's personal data. (GDPR gives controllers 72 hours to notify the regulator, so the processor's period must
be shorter than that. Customer may have shorter contractual duties, for example 72 hours under DFARS 252.204-7012 for
cyber incidents affecting covered defense information. A lawyer should align these.)
6.2 The notice will say what is known: what happened, what data and how many people, likely effect, what Vendor has done and
is doing, and a contact. Vendor will keep Customer updated, help it meet its own notification duties, and not tell regulators or
the people affected on Customer's behalf unless the law requires.

## 7. Deletion and return

7.1 Customer may export its data at any time with the product's own exports, and Vendor will provide a one-time full database
export on request `[no charge]`.
7.2 After the Term ends Customer has `[60]` days to export. Vendor then deletes Customer's database and files from live
systems within `[30]` days and from backups within `[90]` days, and certifies deletion in writing on request, except where the law
requires Vendor to keep something (and then only that, and only for as long as required).
7.3 Records Vendor keeps as its own controller (invoices, support history) are outside this section.

## 8. Audit

Vendor will give Customer the information needed to show compliance with this addendum, including `[its latest SOC 2 report or
ISO certificate when it has one, and answers to a reasonable security questionnaire once a year]`. An on-site audit is
available only if the law requires it or after a breach, on `[30]` days' notice, in business hours, at Customer's cost, and
subject to confidentiality.

## 9. International transfers

Customer Data is stored in `[region(s)]`. Vendor will not move it to another country without Customer's agreement, except for
support access from `[country]` `[describe]`. Where a transfer needs a legal mechanism, the parties will use
`[Standard Contractual Clauses / UK addendum / other: lawyer to attach]`.

## 10. Liability

Liability under this addendum is subject to the limits in the agreement `[or a stated higher cap for data breach: decide]`.

!!! info "Open for the owner"
    Hosting regions, sub-processor list, the breach-notice period, the retention and deletion periods, whether any customer
    will need Controlled Unclassified Information (CUI) hosted (that requires a FedRAMP-Moderate-equivalent environment under
    DFARS 7012, which is a very different undertaking), and which privacy laws you will promise to meet.
