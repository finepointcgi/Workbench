# Supplier security policy

| | |
| --- | --- |
| Owner | `<ISMS manager / procurement>` |
| Approved by | `<top management>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.5.19, A.5.20, A.5.21, A.5.22, A.5.23, A.8.30 · NIST SP 800-171 3.12.4 (SSP), DFARS 252.204-7012(m) flow-down |
| Procedure | [supplier-security-checklist.md](supplier-security-checklist.md) |

## 1. Scope

Every supplier that stores, processes or can access in-scope information, or provides a service the ISMS depends on.
For a Workbench deployment that typically means:

| Supplier type | Example | Data | Typical criticality |
| --- | --- | --- | --- |
| Hosting / cloud | `<Azure Government / AWS GovCloud / data centre>` | All, incl. CUI | Critical |
| Identity provider | `<Entra ID / Okta>` | Identities, MFA | Critical |
| Workbench vendor (if vendor-hosted SaaS) | `<vendor>` | All | Critical |
| Email relay | `<SMTP provider>` | Notifications (names, links) | Medium |
| Payroll provider | `<Paylocity>` | Hours export, pay register | High |
| Backup storage | `<provider>` | Encrypted backups | High |
| Support / development contractors | `<company>` | Possibly all | High |
| Open-source components | NuGet packages (SBOM) | n/a | Medium (A.5.21) |

## 2. Rules

1. **Before contract**: complete the [checklist](supplier-security-checklist.md); obtain assurance evidence (ISO 27001
   certificate with SoA, SOC 2 Type II, FedRAMP authorization for CUI hosting, pen test summary). Criticality decides
   depth.
2. **Contract terms** (A.5.20): confidentiality, security requirements, breach notification within `<24/72> hours`,
   right to audit or receive reports, sub-processor disclosure, data location (`<US only>`), return and deletion at
   exit, and for CUI the DFARS 252.204-7012 flow-down (FedRAMP Moderate baseline or equivalent for cloud services).
3. **ICT supply chain** (A.5.21): Workbench's dependencies are pinned, scanned in CI (build fails on High/Critical
   advisories) and listed in a CycloneDX SBOM per build ([secure-development-policy.md](secure-development-policy.md)).
4. **Monitoring** (A.5.22): review critical suppliers yearly (refresh the checklist and evidence) and on major change;
   track their incidents and SLA performance.
5. **Cloud services** (A.5.23): keep a shared-responsibility matrix per cloud service
   (`../compliance/nist-800-171/control-mapping.md` marks Workbench / customer / hosting responsibility per requirement).
6. **Exit**: data returned in a usable format and deleted, with a certificate; credentials and integrations revoked.
7. **Access by suppliers**: only named accounts invited in Workbench, least privilege, MFA required (Sign-in settings),
   included in access reviews; never shared accounts.

## 3. Register

| Supplier | Service | Data | Criticality | Assurance | Contract clauses OK | Last review | Next review | Owner |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `<name>` | | | | | `<Yes/No>` | | | |

## 4. Evidence

Completed checklists, certificates/reports with dates, contract clause references, this register, and in Workbench the
access reviews and security events showing supplier accounts' access.

## Approval

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<role>` | | |
