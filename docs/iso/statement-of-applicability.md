# Statement of Applicability

| | |
| --- | --- |
| Organization | `<Company>` |
| ISMS scope | `isms-scope.md` version `<x>` |
| Version / date | `<1.0>` / `<date>` |
| Approved by | `<top management>` |

*ISO 27001 clause 6.1.3 d. Lists every Annex A:2022 control, whether it applies, why, how it's implemented and its
status. Pre-filled for a Workbench deployment: confirm each row, replace `<placeholders>`, and change "Applicable" to
"No" only with a real justification (for example, "no on-premises facilities: fully cloud-hosted" for some physical
controls; the cloud provider's controls are then evidence under A.5.23).*

**Implementation** names where the control lives:
*Workbench* = a product feature, with where to find it (E1–E13 are evidence sources in
[evidence-catalog.md](evidence-catalog.md)); *Procedure* = the document in this kit that your organization completes and
operates; *Company* = something only your organization can do.
**Status:** `Implemented` · `Partial` · `Planned` · `Not started`. Product features start as "Implemented (product)";
they still need your evidence that they operate. Rows with a *Procedure* stay "Not started" until it's approved and in use.

## A.5 Organizational controls

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 5.1 Policies for information security | Yes | Required to direct the ISMS | Procedure: [information-security-policy.md](information-security-policy.md) plus the topic policies listed in [README.md](README.md); approved, communicated, acknowledged | Not started |
| 5.2 Information security roles and responsibilities | Yes | Accountability | Procedure: roles section of the security policy; owners named in every policy header. Workbench: Admin/Finance/Timekeeper/ResourceManager role holders listed in Users (E4) | Not started |
| 5.3 Segregation of duties | Yes | Prevent fraud in timekeeping and finance | Workbench: no self-approval of timesheets, approval workflows by document type (Setup › Approval workflows), Finance/Admin separation, audit log (E2). Procedure: [access-control-policy.md](access-control-policy.md) §2 conflicting roles | Partial |
| 5.4 Management responsibilities | Yes | Management must enforce policy | Procedure: [management-review.md](management-review.md), policy acknowledgements | Not started |
| 5.5 Contact with authorities | Yes | DFARS incident reporting, law enforcement | Procedure: contact list in [incident-response-plan.md](incident-response-plan.md) §3 (DoD DC3, FBI, state AG) | Not started |
| 5.6 Contact with special interest groups | Yes | Threat awareness | Company: `<ISAC (e.g., NDISAC), vendor advisories>`, recorded in the security policy | Not started |
| 5.7 Threat intelligence | Yes | Defense industrial base is targeted | Company: CISA/ISAC feeds. Workbench: CI fails on GitHub/NuGet advisories (E11) | Partial |
| 5.8 Information security in project management | Yes | Security in projects and features | Procedure: [secure-development-policy.md](secure-development-policy.md) §2; Workbench: `../security/threat-model.md` updated with interface changes | Partial |
| 5.9 Inventory of information and other associated assets | Yes | Know what to protect | Procedure: [asset-management-policy.md](asset-management-policy.md), [asset-inventory.md](asset-inventory.md); Workbench: SBOM per build (E11) | Partial |
| 5.10 Acceptable use of information and other associated assets | Yes | Staff obligations | Procedure: [asset-management-policy.md](asset-management-policy.md) §4 and an acceptable use statement staff acknowledge | Not started |
| 5.11 Return of assets | Yes | Leavers return equipment and data | Procedure: [asset-management-policy.md](asset-management-policy.md) §7; Workbench: deactivation (E1 `user.deactivated`) | Not started |
| 5.12 Classification of information | Yes | CUI, PII, financial data need handling rules | Procedure: scheme in [asset-inventory.md](asset-inventory.md); Workbench enforces Confidential handling for rates, pay and bank data (masked, role-restricted, reveal logged) | Partial |
| 5.13 Labelling of information | Yes | CUI marking requirements | Procedure: [asset-management-policy.md](asset-management-policy.md) §3 (CUI Registry markings on exports and documents) | Not started |
| 5.14 Information transfer | Yes | Data shared with customers/DCAA | Procedure: [asset-management-policy.md](asset-management-policy.md) §5; Workbench: HTTPS/HSTS, exports role-restricted and logged (`export.*`, E1) | Partial |
| 5.15 Access control | Yes | Core control | Workbench: org and workspace roles enforced server-side by permission decorators, restricted columns (Users and security, E4). Procedure: [access-control-policy.md](access-control-policy.md) | Implemented (product) |
| 5.16 Identity management | Yes | Joiner/mover/leaver | Workbench: invites (7-day expiry), just-in-time provisioning by allowed domain and IdP groups (Sign-in settings, E6), SCIM provisioning, deactivation (never deletion), employee linking (E1 `user.*`, `scim.request`). Company: HR triggers | Implemented (product) |
| 5.17 Authentication information | Yes | Credential protection | Workbench: no local passwords (SSO with Entra ID/Okta, `../security/sso-entra.md`, `sso-okta.md`), MFA required from the IdP (`amr`/`acr`), IdP client secrets encrypted and never shown, SCIM token from vault, startup check. Procedure: [cryptography-policy.md](cryptography-policy.md) §3; Company: IdP password/MFA policy | Implemented (product) / Company |
| 5.18 Access rights | Yes | Provision, review, remove | Workbench: quarterly access reviews with Keep/Revoke and immediate revocation (E3), 90-day dormant flag, roles from IdP groups. Procedure: [access-control-policy.md](access-control-policy.md) §4 | Implemented (product) |
| 5.19 Information security in supplier relationships | Yes | Cloud, IdP, email suppliers | Procedure: [supplier-security-policy.md](supplier-security-policy.md), [supplier-security-checklist.md](supplier-security-checklist.md) | Not started |
| 5.20 Addressing information security within supplier agreements | Yes | Contract terms | Procedure: [supplier-security-policy.md](supplier-security-policy.md) §2 (incl. DFARS 252.204-7012 flow-down) | Not started |
| 5.21 Managing information security in the ICT supply chain | Yes | Open-source dependencies | Workbench: SBOM, vulnerable package check, pinned versions (E11). Procedure: [supplier-security-policy.md](supplier-security-policy.md) | Partial |
| 5.22 Monitoring, review and change management of supplier services | Yes | Ongoing assurance | Procedure: yearly review in [supplier-security-policy.md](supplier-security-policy.md) §2.4 and register §3 | Not started |
| 5.23 Information security for use of cloud services | `<Yes if cloud-hosted>` | `<Azure Gov / AWS GovCloud hosting>` | Procedure: [supplier-security-policy.md](supplier-security-policy.md) §2.5; shared responsibility per requirement in `../compliance/nist-800-171/control-mapping.md` | Not started |
| 5.24 Information security incident management planning and preparation | Yes | Required | Procedure: [incident-response-plan.md](incident-response-plan.md) | Not started |
| 5.25 Assessment and decision on information security events | Yes | Triage | Procedure: severity matrix in the IR plan, review cadence in [logging-monitoring-policy.md](logging-monitoring-policy.md) §3; Workbench: security event log to triage from (E1) | Partial |
| 5.26 Response to information security incidents | Yes | Containment | Procedure: IR runbook; Workbench: deactivate user, sign out everywhere, require MFA/own IdP, legal hold (E1) | Partial |
| 5.27 Learning from information security incidents | Yes | Improvement | Procedure: post-incident review template in the IR plan | Not started |
| 5.28 Collection of evidence | Yes | Legal/contract (DFARS 90-day image retention) | Workbench: append-only security and audit logs, CSV export, legal holds (E1, E2, E7). Procedure: evidence handling in the IR plan | Partial |
| 5.29 Information security during disruption | Yes | Keep controls during incidents | Procedure: [business-continuity-plan.md](business-continuity-plan.md) | Not started |
| 5.30 ICT readiness for business continuity | Yes | Payroll/billing deadlines | Procedure: BC/DR plan with RTO/RPO, [backup-policy.md](backup-policy.md); Workbench: backups scripts, health checks | Partial |
| 5.31 Legal, statutory, regulatory and contractual requirements | Yes | FAR/DFARS/CMMC/privacy | Procedure: legal register `<location>`; references: `../compliance/nist-800-171/`, `../compliance/dcaa/sf1408.md` | Not started |
| 5.32 Intellectual property rights | Yes | Software licences | Company: licence inventory; Workbench: SBOM lists package licences (E11) | Partial |
| 5.33 Protection of records | Yes | FAR 4.703, DCAA | Workbench: protected record types never auto-deleted (≥ 3 years after contract close), legal holds, append-only audit (E2, E7). Company: records schedule | Implemented (product) |
| 5.34 Privacy and protection of PII | Yes | Employee PII | Company: privacy notice and PII inventory; Workbench: role-restricted rates and personal data, masked pay and bank data with logged, re-authenticated reveal, retention | Partial |
| 5.35 Independent review of information security | Yes | Required assurance | Procedure: [internal-audit-programme.md](internal-audit-programme.md) (independent auditor); external pen test | Not started |
| 5.36 Compliance with policies, rules and standards for information security | Yes | Check adherence | Procedure: internal audit programme; Workbench: security event review (E1), DCAA readiness checklist (E10) | Not started |
| 5.37 Documented operating procedures | Yes | Repeatable operations | Workbench: `../operations/backup-restore.md`, `../security/configuration.md`, SSO guides, README; Company: deployment runbooks | Partial |

## A.6 People controls

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 6.1 Screening | Yes | Access to CUI and financial data | Company: background checks per role and contract (HR procedure `<link>`) | Not started |
| 6.2 Terms and conditions of employment | Yes | Security obligations | Company: employment contract clauses referencing the security policy | Not started |
| 6.3 Information security awareness, education and training | Yes | Phishing is the top risk (R02) | Company: annual training and phishing exercises; developer training per [secure-development-policy.md](secure-development-policy.md) | Not started |
| 6.4 Disciplinary process | Yes | Enforce policy | Company: HR disciplinary procedure referenced from the security policy | Not started |
| 6.5 Responsibilities after termination or change of employment | Yes | Leavers | Procedure: offboarding in [access-control-policy.md](access-control-policy.md); Workbench: deactivation, SCIM deprovisioning, sign out everywhere (E1) | Partial |
| 6.6 Confidentiality or non-disclosure agreements | Yes | Protect customer data | Company: NDAs for staff and contractors | Not started |
| 6.7 Remote working | Yes | Remote staff | Company: remote work policy; Workbench: browser-based over HTTPS, MFA, idle/absolute session limits, re-authentication | Partial |
| 6.8 Information security event reporting | Yes | Early detection | Procedure: reporting channel in [incident-response-plan.md](incident-response-plan.md) §2; `../../SECURITY.md` for external reporters | Partial |

## A.7 Physical controls

*If Workbench runs only in a cloud region and you have no server room, these still apply to offices where staff
work with the data, and the cloud provider's physical controls are evidence for the hosting part (A.5.23).*

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 7.1 Physical security perimeters | Yes | Offices and any server room | Company: office perimeter; hosting provider's SOC 2/FedRAMP report (supplier register) | Not started |
| 7.2 Physical entry | Yes | Controlled entry | Company: badges, visitor log | Not started |
| 7.3 Securing offices, rooms and facilities | Yes | Shared offices | Company: locked rooms for equipment | Not started |
| 7.4 Physical security monitoring | Yes | Detect intrusion | Company: CCTV/alarms as appropriate; hosting provider report | Not started |
| 7.5 Protecting against physical and environmental threats | Yes | Fire, flood | Company / hosting provider (region choice in BC plan) | Not started |
| 7.6 Working in secure areas | `<Yes/No>` | `<only if you have secure areas>` | Company | Not started |
| 7.7 Clear desk and clear screen | Yes | Shared offices | Company: policy; Workbench: idle timeout (30 min default, server setting) and shorter organization session limit | Partial |
| 7.8 Equipment siting and protection | Yes | Servers, laptops | Company / hosting provider | Not started |
| 7.9 Security of assets off-premises | Yes | Laptops | Procedure: [asset-management-policy.md](asset-management-policy.md) §6 (disk encryption, MDM) | Not started |
| 7.10 Storage media | Yes | Backups, removable media | Procedure: [asset-management-policy.md](asset-management-policy.md) §6, [backup-policy.md](backup-policy.md) | Not started |
| 7.11 Supporting utilities | Yes | Power/cooling for servers | Company / hosting provider | Not started |
| 7.12 Cabling security | `<Yes/No>` | `<on-prem only>` | Company / hosting provider | Not started |
| 7.13 Equipment maintenance | Yes | Reliability | Company / hosting provider | Not started |
| 7.14 Secure disposal or re-use of equipment | Yes | Disks with data | Procedure: [asset-management-policy.md](asset-management-policy.md) §7 (NIST SP 800-88, certificates) | Not started |

## A.8 Technological controls

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 8.1 User end point devices | Yes | Laptops and the desktop client | Procedure: [asset-management-policy.md](asset-management-policy.md) §6; Workbench: thin web/desktop clients keep data on the server | Partial |
| 8.2 Privileged access rights | Yes | Admin/Finance roles | Workbench: roles, role changes logged (incl. from IdP groups), self-demotion and last-admin protection, MFA for admins option, re-authentication for sensitive actions, access reviews (E1, E3, E6). Procedure: [access-control-policy.md](access-control-policy.md) | Implemented (product) |
| 8.3 Information access restriction | Yes | Need to know | Workbench: workspace membership, Finance-only rates and budgets, restricted columns, permission decorators, 404 for unseen sheets; SaaS: a database per tenant (rows below) | Implemented (product) |
| 8.4 Access to source code | Yes | Protect code and secrets | Procedure: [secure-development-policy.md](secure-development-policy.md); Workbench: CODEOWNERS, gitleaks (E11); Company: GitHub org access, branch protection | Partial |
| 8.5 Secure authentication | Yes | Core control | Workbench: OIDC SSO with PKCE, per-organization Entra ID/Okta (Sign-in settings, E6), MFA enforced from `amr`/`acr` (off/admins/everyone), re-authentication with `prompt=login` for pay rates, bank details, vendor TINs, approval workflows and sign-in settings, idle 30 min / absolute 12 h (and shorter per organization), circuit re-validation, sign out everywhere, rate-limited sign-in (E1). Guides: `../security/sso-entra.md`, `sso-okta.md` | Implemented (product) |
| 8.6 Capacity management | Yes | Availability | Company: monitor capacity per [logging-monitoring-policy.md](logging-monitoring-policy.md); Workbench: OpenTelemetry metrics, health checks | Partial |
| 8.7 Protection against malware | Yes | Uploads, endpoints | Workbench: attachment type allow-list and size limit; Company: endpoint protection, storage scanning | Partial |
| 8.8 Management of technical vulnerabilities | Yes | Dependencies and hosts | Workbench: CI fails on High/Critical packages, SBOM, `SECURITY.md` disclosure (E11); Procedure: [secure-development-policy.md](secure-development-policy.md); Company: OS/DB patching, scanning, pen test | Partial |
| 8.9 Configuration management | Yes | Secure baselines | Workbench: startup check refuses weak production config; settings changes logged (`settings.*`, E1, E12); Company: infrastructure as code, baselines | Partial |
| 8.10 Information deletion | Yes | Retention limits | Workbench: retention policies and nightly purge of expired purgeable records, deletion reports (E7); Procedure: [asset-management-policy.md](asset-management-policy.md) §7 | Implemented (product) |
| 8.11 Data masking | `<Yes>` | Test data, exports | Workbench: pay rates, bank numbers and TINs masked unless revealed (logged, re-authenticated); cost rates hidden from non-Finance; Company: no production data in test | Partial |
| 8.12 Data leakage prevention | Yes | CSV exports of labor and financial data | Workbench: exports role-restricted and logged (`export.*`, E1); Company: DLP on email/endpoints | Partial |
| 8.13 Information backup | Yes | Ransomware, data loss | Workbench: `scripts/backup.sh`/`restore.sh`, tested restore; Procedure: [backup-policy.md](backup-policy.md) | Partial |
| 8.14 Redundancy of information processing facilities | Yes | Availability targets | Procedure: [business-continuity-plan.md](business-continuity-plan.md); Workbench: stateless web host, shared Data Protection keys allow several instances | Partial |
| 8.15 Logging | Yes | Detect and prove | Workbench: append-only security event log ([evidence-catalog.md](evidence-catalog.md) lists every event), audit log, CSV export, retention (E1, E2). Procedure: [logging-monitoring-policy.md](logging-monitoring-policy.md) | Implemented (product) |
| 8.16 Monitoring activities | Yes | Detect anomalies | Procedure: review cadence and alerts in [logging-monitoring-policy.md](logging-monitoring-policy.md) §3; Workbench: /health, OpenTelemetry, structured logs; Company: SIEM | Partial |
| 8.17 Clock synchronization | Yes | Log correlation | Company: NTP on hosts; Workbench records all times in UTC | Partial |
| 8.18 Use of privileged utility programs | Yes | DB consoles, scripts | Company: restrict and log psql/sqlite3 and admin tools ([logging-monitoring-policy.md](logging-monitoring-policy.md) §2) | Not started |
| 8.19 Installation of software on operational systems | Yes | Production change | Procedure: deploy only CI-built artifacts ([secure-development-policy.md](secure-development-policy.md)) | Not started |
| 8.20 Networks security | Yes | Internet-facing app | Workbench: HTTPS/HSTS, rate limiting, forwarded headers only from known proxies; Company: firewalls, TLS config | Partial |
| 8.21 Security of network services | Yes | Hosting and IdP services | Workbench: HTTPS-only IdP metadata; Procedure: supplier SLAs in [supplier-security-policy.md](supplier-security-policy.md) | Partial |
| 8.22 Segregation of networks | Yes | Isolate DB | Company: DB and blob store on private networks | Not started |
| 8.23 Web filtering | Yes | Staff browsing | Company: DNS/web filtering for staff devices. (Workbench's CSP limits what *its* pages load, but that is not this control.) | Not started |
| 8.24 Use of cryptography | Yes | Data in transit and at rest | Workbench: TLS/HSTS, Data Protection keys persisted and certificate-encrypted, IdP secrets, bank numbers and TINs encrypted; Procedure: [cryptography-policy.md](cryptography-policy.md) | Partial |
| 8.25 Secure development life cycle | Yes | We build Workbench | Procedure: [secure-development-policy.md](secure-development-policy.md); Workbench: CI pipeline, threat model | Implemented (product) |
| 8.26 Application security requirements | Yes | Requirements per feature | Workbench: CSP and security headers, antiforgery, permission model, input validation, re-authentication of sensitive actions; threat model | Implemented (product) |
| 8.27 Secure system architecture and engineering principles | Yes | Layered defense | Workbench: layered architecture (UI never touches the DB), server-side enforcement, threat model | Implemented (product) |
| 8.28 Secure coding | Yes | Avoid common flaws | Workbench: conventions, CODEOWNERS review, analyzers; Company: developer training | Partial |
| 8.29 Security testing in development and acceptance | Yes | Verify controls | Workbench: automated security tests (permissions, headers, rate limits, SCIM, sessions, SSO end to end against a mock IdP, MFA, re-authentication); Company: pen test before major releases | Partial |
| 8.30 Outsourced development | `<Yes/No>` | `<if contractors develop>` | Procedure: contractors follow [secure-development-policy.md](secure-development-policy.md); supplier review | Not started |
| 8.31 Separation of development, test and production environments | Yes | Dev conveniences out of prod | Workbench: dev sign-in exists only in Development; startup check; Company: separate environments and credentials | Partial |
| 8.32 Change management | Yes | Controlled change | Workbench: CODEOWNERS, CI, reviewed migrations, `settings.*` events; Procedure: release approval in [secure-development-policy.md](secure-development-policy.md) | Partial |
| 8.33 Test information | Yes | Production data in tests | Workbench: tests use seeded/synthetic data; Company: no production copies in test without masking | Partial |
| 8.34 Protection of information systems during audit testing | Yes | Audits and pen tests | Company: plan audit tests, read-only accounts ([internal-audit-programme.md](internal-audit-programme.md)) | Not started |

## SaaS (MultiTenant) deployment

*Extra rows for an organization that runs Workbench as a multi-tenant service (`Tenancy:Mode=MultiTenant`). They add to
the rows above for the same controls; a self-hosted, single-tenant deployment can mark them "No: single tenant".*

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 8.3 Information access restriction (tenants) | `<Yes if SaaS>` | Customers must never see each other's data | Workbench: one database per tenant; tenant resolved from the subdomain before authentication; cookies and Data Protection purposes per tenant; each tenant's own IdP, MFA rule and provisioning rules (a tenant's authorization response is useless at another tenant); isolation tests in CI | Implemented (product) |
| 8.3 Information access restriction (host administration) | `<Yes if SaaS>` | Operating the service is not a customer role | Workbench: host admins in `Tenancy:HostAdmins`, only on the base domain (`/host`), host audit log (E13). Company: review the list quarterly | Implemented (product) / Company |
| 5.23 Information security for use of cloud services (as provider) | `<Yes if SaaS>` | Customers rely on our isolation and operations | Workbench: tenant isolation, provisioning with an invited first Admin, suspension. Company: shared-responsibility statement (`../compliance/nist-800-171/control-mapping.md`), per-tenant backup/restore, offboarding and data return | Partial |
| 8.5 Secure authentication (tenants) | `<Yes if SaaS>` | Each customer controls its own sign-in | Workbench: per-tenant IdP with encrypted client secret, "only our IdP" option, MFA and session limits per tenant (E6) | Implemented (product) |
| 8.11 Data masking (tenants) | `<Yes if SaaS>` | Support staff shouldn't see tenant data | Workbench: the host site shows only tenant names, status, size and activity; tenant connection strings and secrets encrypted. Company: support access only through an invited, audited tenant account | Partial |
| 8.31 Separation of environments (tenants) | `<Yes if SaaS>` | Development conveniences must not reach production | Workbench: `X-Tenant` header and development tenants only in Development; startup check refuses weak MultiTenant production. Company: separate catalog, databases and keys per environment | Partial |

## Summary

| Theme | Controls | Applicable | Implemented (product) | Partial | Company / not started |
| --- | --- | --- | --- | --- | --- |
| A.5 Organizational | 37 | `<n>` | `<n>` | `<n>` | `<n>` |
| A.6 People | 8 | `<n>` | | | |
| A.7 Physical | 14 | `<n>` | | | |
| A.8 Technological | 34 | `<n>` | | | |
| **Total** | **93** | | | | |

## Approval

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<top management>` | | |
