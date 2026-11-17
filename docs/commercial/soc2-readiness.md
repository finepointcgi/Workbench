# SOC 2 readiness

!!! danger "DRAFT: planning document, not an audit opinion or legal advice"
    SOC 2 is an attestation by an independent CPA firm about **a service organization's** controls, not a certification of a
    product. Nothing here says Workbench, or any vendor operating it, has a SOC 2 report. This page maps what already exists
    to the criteria, lists what is missing, and describes the path honestly. Confirm each mapping with the auditor you pick;
    they decide what counts as evidence.

## Why this matters, and for whom

- **A customer who self-hosts** runs Workbench inside its own controls. Its auditor looks at *its* environment. The product
  supplies features and evidence ([evidence catalog](https://github.com/finepointcgi/Workbench/blob/master/docs/iso/evidence-catalog.md)).
- **A customer of the hosted service** will ask the vendor for a SOC 2 report. Buyers in finance, healthcare and larger
  professional-services firms routinely require it. It is the main reason to do this work, and it applies **only if the vendor
  runs the hosted service**.
- Government customers care more about NIST SP 800-171 and CMMC (see [CMMC assessment](../admin-guide/cmmc-assessment.md))
  than SOC 2, and CUI hosting is a different programme (see [open decisions](#open-decisions)).

## Scope

The **trust services criteria** (TSC) are organized into categories. A report always covers **Security** (the "common
criteria", CC1 to CC9); the others are optional.

| Category | Recommend | Why |
|---|---|---|
| **Security** | Required | Always in scope |
| **Availability** | Include if the [service levels](service-levels-draft.md) promise an uptime number | Customers who buy a hosted time and billing system ask about it |
| **Confidentiality** | Include | The product holds pay rates, bank details, bids and contract data |
| Processing integrity | Defer | Plausible for payroll and billing accuracy, but adds testing work; revisit after the first report |
| Privacy | Defer | Covered contractually by the [data processing addendum](data-processing-addendum-draft.md); a bigger programme |

The **system description** should cover the hosted service only: the application, the PostgreSQL databases, the infrastructure
provider, people, procedures and data flows. Self-hosted installations are excluded.

## Mapping: criteria to the ISO policy set and product features

The repository holds a templated ISO/IEC 27001 set under
[`docs/iso`](https://github.com/finepointcgi/Workbench/tree/master/docs/iso). The policies there are **templates with
placeholders**; they count for nothing until approved and operated. "Status" below describes the product feature or the
template, never whether Vendor operates it.

| TSC | What it asks (short) | ISO policy / template | Product feature (evidence source) | Status |
|---|---|---|---|---|
| **CC1** control environment | Integrity, board oversight, structure, competence, accountability | `information-security-policy`, `management-review`, `isms-scope` | None (organizational) | Template only |
| **CC2** communication | Internal and external communication of objectives and responsibilities | `information-security-policy`, `incident-response-plan` | In-app help and support requests; `SECURITY.md` for external reporters | Partial |
| **CC3** risk assessment | Identify and assess risks, fraud, change | `risk-assessment-method`, `risk-register` (starter risks) | Threat model (`docs/security/threat-model.md`) | Template only |
| **CC4** monitoring | Ongoing evaluation, deficiencies reported | `internal-audit-programme`, `management-review` | Security events (E1), audit log (E2) | Template only |
| **CC5** control activities | Policies and technology controls | All policies | Role permissions, [approval workflows](../admin-guide/approval-workflows.md) | Partial |
| **CC6.1-6.3** logical access | Authorization, provisioning, removal, role review | `access-control-policy` | Roles and permissions; **SSO and SCIM** provisioning; invitations; deactivation; **access reviews** (E3); last-admin protection | Feature exists |
| **CC6.1** authentication | Strong auth | `access-control-policy`, `cryptography-policy` | MFA enforcement by identity provider, session timeouts, re-authentication for sensitive actions, lockout (built-in accounts) | Feature exists |
| **CC6.1, CC6.7** encryption | Protect data at rest and in transit | `cryptography-policy` | TLS and HSTS; Data Protection encryption of bank numbers, tax IDs and secrets; encrypted backups; infrastructure at-rest encryption is the host's | Feature plus infrastructure |
| **CC6.4-6.5** physical, disposal | Facilities and media | `asset-management-policy` | Inherit from the cloud provider (needs its SOC 2 report) | Inherited |
| **CC6.6, CC6.8** boundary, malware | Network and endpoint protection | `logging-monitoring-policy`, `secure-development-policy` | Rate limiting (logged), dependency and secret scanning in CI | Partial |
| **CC7.1-7.2** monitoring | Detect anomalies and events | `logging-monitoring-policy` | Security event log and export; health checks and [alerts](../operations/alerts.md); metrics and traces | Feature exists; alert routing is Company |
| **CC7.3-7.5** incidents | Respond, recover | `incident-response-plan`, `business-continuity-plan` | [Incident response](../operations/incident-response.md) runbook; security event evidence | Template only |
| **CC8.1** change management | Authorized, tested, approved changes | `secure-development-policy` | Pull-request review (CODEOWNERS), CI build and tests, SBOM, migrations as a separate step, tagged releases | Feature exists (repo practice) |
| **CC9.1-9.2** risk mitigation, vendors | Business disruption, suppliers | `supplier-security-policy`, `supplier-security-checklist`, `business-continuity-plan` | None | Template only |
| **A1.1-1.3** availability | Capacity, backups, recovery | `backup-policy`, `business-continuity-plan` | `scripts/backup-all.sh`, point-in-time recovery, restore tests, [monitoring](../operations/monitoring.md), [performance](../operations/performance.md) | Feature exists; targets Company |
| **C1.1-1.2** confidentiality | Identify, protect, dispose of confidential data | `cryptography-policy`, `access-control-policy`, `asset-management-policy` | Masked sensitive fields with logged reveal; **retention policies and legal holds**; data export and deletion on termination | Feature exists |

!!! note "What ISO covers and SOC 2 asks beyond"
    The ISO set is a management system. SOC 2 asks the same questions but tests the **operation** of specific controls over
    a period. Having the document is nothing; the sampled evidence (tickets, reviews, logs, training records) is everything.

## Gaps and the evidence to collect

| Gap | What to do | Evidence an auditor will sample |
|---|---|---|
| ISMS policies are unapproved templates | Fill placeholders, approve, communicate, have staff acknowledge | Signed policies, acknowledgement records, version history |
| No named owners, no org chart or board-level oversight | Name security owner and reviewers; hold management reviews | Management review minutes (`management-review.md`) |
| No formal risk assessment run | Score the starter register, add Vendor-specific risks, review at least yearly | Completed `risk-register`, review notes |
| **Hosted operations are not yet defined** (infrastructure, regions, sub-processors) | Decide hosting; the infrastructure provider's own SOC 2 report becomes a "carve-out" or "inclusive" dependency | Provider SOC 2 reports, supplier reviews |
| Staff controls (HR) | Background checks, onboarding and offboarding checklists, annual security training | Onboarding tickets, training completion records, signed agreements |
| Vendor access to customer tenants | Define who can reach production, how (just-in-time, logged), and review it | Access reviews of the host administrators (`/host`, E13) and cloud accounts |
| Endpoint and laptop management | Disk encryption, MDM, patching, antivirus or EDR | MDM reports, screenshots, exception lists |
| Alert routing and on-call | Alerts reach a person, someone responds, incidents are logged | Alert history, incident tickets and post-incident reviews |
| Backup restores | Restore into a test environment on a schedule and measure RPO/RTO | Dated restore test records (`backup-policy`) |
| Vulnerability management | Scan production dependencies and images, patch to a stated timeline | Scan reports, ticket closure times against `SECURITY.md` targets |
| Penetration test | Independent test at least yearly (the auditor will ask) | Report and remediation tracking |
| Change management is a repo practice, not a documented one | Document the release process; require review and a passing build on the default branch | PR history, branch protection settings, release notes |
| Business continuity and disaster recovery tests | Run and record at least one exercise | Exercise report, lessons learned |
| Customer-facing commitments | Terms, DPA and service levels signed off by a lawyer | The finalized [terms](end-user-terms-draft.md), [DPA](data-processing-addendum-draft.md) and [service levels](service-levels-draft.md) |
| Evidence for product features | Use the product's own exports (access reviews, security events, retention runs) on the schedule in the evidence catalog | Quarterly access review CSVs, monthly event review notes |

### Product features an auditor will probe, and the questions to prepare for

- **Audit log and security events**: append-only; can an administrator change them? (Security events are append-only; only the
  retention job deletes, and the retention period is configurable.)
- **SSO, SCIM and MFA**: enforced by the customer's identity provider. The vendor's own staff need the same on cloud and admin
  accounts, which is a Company control.
- **RBAC**: see [Permissions](../security/permissions.md). Custom roles may be changing that model; document the final one before the audit.
- **Encryption**: the app encrypts specific secrets; database and backup encryption at rest depends on the infrastructure.
  Document the key custody (the Data Protection certificate and the key ring).
- **Backups**: encrypted logical dumps and point-in-time recovery exist as scripts; the schedule, offsite copy and restore
  evidence must be Vendor's.

## What the path involves (no prices, only shape)

**Type I** reports on whether the controls are **designed** suitably **at a point in time**. **Type II** reports on whether
they **operated effectively over a period** (commonly `[3 to 12]` months; the first Type II is often 3 to 6).

| Stage | What happens | Typical elapsed time (estimate; depends on current state) |
|---|---|---|
| 1. Decide scope and criteria | Choose categories, system boundary, hosting; pick the auditor and a compliance automation tool or none | 2 to 4 weeks |
| 2. Gap assessment and remediation | Close the gaps above; write and approve policies; turn on the controls | 2 to 6 months, mostly people's time, not software |
| 3. Readiness review (optional) | The auditor or a consultant does a mock walkthrough | 2 to 4 weeks |
| 4. Type I audit | Auditor reviews design at a date; issues a report | 4 to 8 weeks |
| 5. Observation period | Operate and collect evidence daily | 3 to 12 months |
| 6. Type II audit | Auditor samples across the period; issues a report | 4 to 8 weeks after the period ends |
| 7. Annual repeat | A new period and report every year | ongoing |

- **Auditor.** A licensed CPA firm. Get quotes from at least three; ask whether they have audited software vendors of your
  size, how they scope Type I to Type II, and whether they will accept evidence from your tooling. This page deliberately does
  not guess their fees; they vary widely.
- **Tooling.** Compliance automation platforms (which gather evidence from your cloud, source and HR systems) shorten the
  work and have subscription fees; a spreadsheet and discipline also work for a small scope. Choose after the gap
  assessment, not before. **Do not buy a tool on the hope it makes a report happen**; people doing the controls does that.
- **Time.** Realistically `[9 to 15]` months from decision to a first Type II report for an organization starting from
  templates, less if Type I is skipped. A customer who needs a report sooner can often accept a Type I plus a bridge letter.
- **Internal effort.** Expect a named owner (part-time at least), engineering time for logging, access and alert work,
  and a lawyer for the customer documents. These costs matter more than the auditor's.
- **Cost drivers to ask each auditor about:** the number of categories, system complexity, the number of in-scope locations and
  sub-processors, the observation period length, whether the report is Type I or II, whether a readiness review is separate, and
  the tooling.

## Open decisions

1. **Will Vendor host at all?** If not, this whole page is unneeded; self-hosted customers audit their own environments.
2. **Hosting provider and regions**: the provider's own assurance reports are inherited controls.
3. **Which categories** (Security plus Availability plus Confidentiality is the recommendation).
4. **CUI.** Hosting Controlled Unclassified Information for defense customers requires a FedRAMP-Moderate-equivalent
   environment under DFARS 252.204-7012 and CMMC. SOC 2 does not meet that. Decide whether the hosted service will ever accept
   CUI; the default recommendation is **no**, with self-hosting for those customers.
5. **Type I first, or straight to Type II.**
6. **Who owns security**, and who owns the ISMS.
