# Supplier security review checklist

| | |
| --- | --- |
| Owner | `<ISMS manager / procurement>` |
| Controls | A.5.19, A.5.20, A.5.21, A.5.22, A.5.23 |

*Use one copy per supplier that stores, processes or can access in-scope information, or provides a critical
service (hosting, identity provider, email, backup storage, payroll, a Workbench vendor-hosted service, support
contractors). Review before contract and at least yearly. Keep the completed checklist and evidence.*

## Supplier

| | |
| --- | --- |
| Supplier | `<name>` |
| Service | `<e.g. Azure Government hosting / Entra ID / SMTP relay>` |
| Data involved | `<CUI / PII / financial / none>` |
| Criticality | `<Critical / High / Medium / Low>` |
| Relationship owner | `<name>` |
| Review date / next review | `<date>` / `<date>` |

## 1. Assurance

| Question | Answer | Evidence |
| --- | --- | --- |
| Current ISO 27001 certificate (scope covers the service)? | | Certificate, scope, expiry |
| SOC 2 Type II report (last 12 months)? Exceptions reviewed? | | Report, bridge letter |
| FedRAMP authorization (Moderate/High) where CUI is involved (DFARS 7012 (b)(2)(ii)(D))? | | FedRAMP marketplace listing |
| CMMC / NIST SP 800-171 status if they handle CUI on our behalf? | | SPRS score / certificate |
| Penetration test in the last year? | | Summary letter |

## 2. Contract terms (A.5.20)

- [ ] Confidentiality and permitted use of our data
- [ ] Security requirements (or reference to their certified controls)
- [ ] Incident notification within `<24/72>` hours, cooperation with investigations
- [ ] DFARS 252.204-7012 / FAR 52.204-21 flow-down where applicable
- [ ] Data location (US only / specific regions) and US-person support requirements if needed
- [ ] Subprocessor list and notice of changes
- [ ] Right to audit or to receive audit reports
- [ ] Data return and deletion at contract end, with certificate
- [ ] Availability SLA and support terms
- [ ] Liability / insurance

## 3. Security controls

| Area | Question | Answer |
| --- | --- | --- |
| Access | MFA for their staff; least privilege to our data; access reviews | |
| Encryption | In transit (TLS 1.2+); at rest; key management (customer-managed keys?) | |
| Logging | Can we get audit logs of access to our data? | |
| Vulnerabilities | Patch SLAs; vulnerability disclosure program | |
| Continuity | RTO/RPO; backups; DR tests | |
| People | Background screening; security training | |
| Supply chain (A.5.21) | Their critical subcontractors; SBOM for software suppliers | |
| Cloud (A.5.23) | Shared responsibility matrix; tenant isolation; exit plan | |

## 4. Software suppliers and open source (A.5.21)

Workbench's own dependencies are covered by CI: every build checks NuGet advisories (fails on High/Critical) and
produces a CycloneDX SBOM. For other software suppliers, ask for an SBOM and their vulnerability response process.

## 5. Decision

| Residual risk | `<Low/Medium/High>` | Risk register ID | `<R12>` |
| --- | --- | --- | --- |
| Decision | `<Approve / Approve with conditions / Reject>` | Conditions | `<>` |
| Approved by | `<name>` | Date | `<>` |

## 6. Ongoing monitoring (A.5.22)

| Date | Change or event (new subprocessor, incident, certificate renewal) | Action |
| --- | --- | --- |
| | | |
