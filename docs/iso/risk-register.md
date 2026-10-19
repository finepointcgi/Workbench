# Risk register and treatment plan

| | |
| --- | --- |
| Owner | `<ISMS manager>` |
| Last reviewed | `<date>` |
| Method | `risk-assessment-method.md` (L × I, 1–25) |

*Starter risks for a Workbench deployment. The inherent scores are suggestions: re-score them for your organization,
assign owners, delete what doesn't apply and add your own (HR, facilities, finance, legal). "Controls in place"
lists what Workbench provides; anything marked `<company>` is yours to implement.*

| ID | Risk (threat → asset → consequence) | Owner | L | I | Inherent | Controls in place / planned | Annex A | Res. L | Res. I | Residual | Treatment and actions | Due | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| R01 | Leaver keeps access → labor and financial data viewed or changed after departure | `<IT>` | 3 | 4 | 12 | SCIM deactivation from IdP; admin deactivation ends sessions and tokens at once; quarterly access reviews flag 90-day dormant users; `<company: HR→IT offboarding checklist>` | 5.18, 6.5, 8.5 | 1 | 4 | 4 | Modify: connect SCIM; offboarding SLA 1 business day | `<date>` | `<Open>` |
| R02 | Phishing / credential theft → account takeover | `<IT>` | 4 | 4 | 16 | SSO via OIDC (organization IdP per tenant); MFA required by Workbench from the IdP's `amr`/`acr` (Sign-in settings: admins or everyone), refusals logged; re-authentication for pay rates, bank details, approval workflows; session idle 30 min / absolute 12 h; `<company: MFA + conditional access at IdP>` | 5.17, 8.5, 6.3 | 2 | 4 | 8 | Modify: enforce phishing-resistant MFA; awareness training | | |
| R03 | Excessive privilege → unauthorized rate or budget changes | `<Finance lead>` | 3 | 4 | 12 | Org roles (Admin/Finance/Timekeeper); permission decorators; audit log of rate/setup/budget changes; access reviews | 5.15, 5.18, 8.2, 5.3 | 2 | 3 | 6 | Modify: limit Admin to `<n>` people; review quarterly | | |
| R04 | Timesheet falsification or after-the-fact edits → DCAA findings, disallowed costs | `<Controller>` | 3 | 5 | 15 | Daily entry and late-entry flags; revisions with reasons; supervisor approval; no self-approval; append-only audit | 5.33, 8.15 | 2 | 4 | 8 | Modify: monthly review of late entries and revisions | | |
| R05 | Web application vulnerability (XSS, injection, broken access control) → data breach | `<Engineering>` | 3 | 4 | 12 | CSP, encoded output, EF parameterized queries, server-side permission checks, CI tests; threat model | 8.25–8.29, 8.26 | 2 | 4 | 8 | Modify: annual penetration test; fix SLAs | | |
| R06 | Vulnerable third-party package → exploitation | `<Engineering>` | 3 | 4 | 12 | CI fails on High/Critical NuGet advisories; SBOM per build | 8.8, 5.21 | 2 | 3 | 6 | Modify: monthly dependency updates | | |
| R07 | Secret committed to source control → infrastructure compromise | `<Engineering>` | 3 | 4 | 12 | gitleaks in CI; secrets from vault/env vars; startup refuses weak config | 8.4, 8.24, 5.17 | 1 | 4 | 4 | Modify: rotate on any detection | | |
| R08 | Ransomware or database loss → data and operations lost | `<Operations>` | 3 | 5 | 15 | Backup/restore scripts with checksums; `<company: off-site immutable copies, quarterly restore tests>` | 8.13, 5.30, 8.7 | 2 | 3 | 6 | Modify: immutable backups in second region | | |
| R09 | Hosting outage → users can't record time (payroll and billing deadlines) | `<Operations>` | 3 | 3 | 9 | /health/live and /health/ready; `<company: redundancy, BC/DR plan>` | 5.30, 8.14, 8.16 | 2 | 3 | 6 | Modify: define RTO/RPO; test failover | | |
| R10 | Insufficient logging → incidents undetected or unprovable | `<ISMS manager>` | 3 | 3 | 9 | Security event log (sign-ins, role changes, SCIM, denials, exports, period close); `<company: SIEM forwarding and review>` | 8.15, 8.16, 5.28 | 2 | 2 | 4 | Modify: weekly review of failures/denials | | |
| R11 | Bulk export of labor/financial CSVs → data leaves the organization | `<Finance lead>` | 3 | 4 | 12 | Exports limited by role and logged with actor and IP; `<company: DLP on endpoints/email>` | 8.12, 8.15 | 2 | 3 | 6 | Modify: monthly review of export events | | |
| R12 | Supplier (cloud, IdP, email) breach → our data exposed | `<ISMS manager>` | 2 | 4 | 8 | `<company: supplier reviews, FedRAMP/SOC reports, contracts>` | 5.19–5.23 | 2 | 3 | 6 | Modify: annual supplier review | | |
| R13 | Records deleted before retention ends → FAR 4.703 / contract breach | `<Controller>` | 2 | 4 | 8 | Protected record types never auto-deleted; legal holds; retention report; FK restrict deletes | 5.33, 8.10 | 1 | 4 | 4 | Retain | | |
| R14 | Personal data kept too long → privacy non-compliance | `<HR>` | 2 | 3 | 6 | Retention policies for security events, notifications, invites; `<company: employee data retention schedule>` | 5.34, 8.10 | 2 | 2 | 4 | Retain / modify | | |
| R15 | Misconfigured production (dev sign-in, HTTP, SQLite) → trivial compromise | `<Operations>` | 2 | 5 | 10 | Startup check refuses these; HSTS; documented configuration | 8.9, 8.20 | 1 | 5 | 5 | Retain | | |
| R16 | SCIM token theft → attacker creates or disables users | `<IT>` | 2 | 4 | 8 | 32+ char token in vault; constant-time check; rate limit; every call logged; SCIM can't grant roles | 5.17, 8.5 | 1 | 3 | 3 | Retain; rotate yearly | | |
| R17 | CUI handled outside a compliant environment → DFARS breach | `<Contracts lead>` | 3 | 5 | 15 | `<company: hosting in GovCloud/Azure Gov or on-prem enclave; SSP for NIST 800-171>` | 5.31, 5.23 | | | | Modify | | |
| R18 | Lost or stolen laptop → data exposure | `<IT>` | 3 | 3 | 9 | Thin desktop client (no local data); `<company: disk encryption, MDM>` | 8.1, 7.9 | 2 | 2 | 4 | Modify | | |
| R19 | Insider with Admin abuses access | `<CEO>` | 2 | 4 | 8 | Admin actions in security log; last-admin and self-protection; `<company: two-person review of admin list, screening>` | 5.3, 6.1, 8.2 | 1 | 4 | 4 | Modify | | |
| R20 | Denial of service / credential stuffing → outage | `<Operations>` | 3 | 3 | 9 | Rate limiting on sign-in, API, SCIM; `<company: WAF / DDoS protection>` | 8.20, 8.6 | 2 | 2 | 4 | Modify | | |

## Change history

| Date | Change | By |
| --- | --- | --- |
| `<date>` | Initial register from the Workbench starter risks | `<name>` |
