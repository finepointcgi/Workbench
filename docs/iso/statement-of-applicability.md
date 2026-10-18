# Statement of Applicability

| | |
| --- | --- |
| Organization | `<Company>` |
| ISMS scope | `isms-scope.md` version `<x>` |
| Version / date | `<1.0>` / `<date>` |
| Approved by | `<top management>` |

*ISO 27001 clause 6.1.3 d. Lists every Annex A:2022 control, whether it applies, why, how it's implemented and its
status. Pre-filled for a self-hosted Workbench deployment: confirm each row, replace `<placeholders>`, and change
"Applicable" to "No" only with a real justification (for example, "no on-premises facilities: fully cloud-hosted"
for some physical controls; the cloud provider's controls are then evidence under A.5.23).*

**Implementation:** *Workbench* = a product feature or document in this repository; *Company* = a process or
control your organization must operate.
**Status:** `Implemented` · `Partial` · `Planned` · `Not started` (set yours; product rows start as Implemented only
where the feature exists, and still need your evidence of operation).

## A.5 Organizational controls

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 5.1 Policies for information security | Yes | Required to direct the ISMS | Company: `information-security-policy.md` and topic policies, approved and communicated | Not started |
| 5.2 Information security roles and responsibilities | Yes | Accountability | Company: roles in the security policy; Workbench admin role holders named | Not started |
| 5.3 Segregation of duties | Yes | Prevent fraud in timekeeping and finance | Workbench: no self-approval of timesheets; Finance/Admin/Timekeeper roles; supervisor approvals. Company: assign conflicting duties to different people | Partial |
| 5.4 Management responsibilities | Yes | Management must enforce policy | Company: management review, policy acknowledgement | Not started |
| 5.5 Contact with authorities | Yes | DFARS incident reporting, law enforcement | Company: contact list in `incident-response-plan.md` (DoD DC3, FBI, state AG) | Not started |
| 5.6 Contact with special interest groups | Yes | Threat awareness | Company: `<ISAC (e.g., NDISAC), vendor advisories>` | Not started |
| 5.7 Threat intelligence | Yes | Defense industrial base is targeted | Company: subscribe to CISA/ISAC feeds; GitHub/NuGet advisories (CI vulnerability check) | Partial |
| 5.8 Information security in project management | Yes | Security in projects and features | Company: security review step in project templates; Workbench: threat model, secure development policy | Partial |
| 5.9 Inventory of information and other associated assets | Yes | Know what to protect | Company: `asset-inventory.md`; Workbench: SBOM per CI build | Partial |
| 5.10 Acceptable use of information and other associated assets | Yes | Staff obligations | Company: acceptable use policy | Not started |
| 5.11 Return of assets | Yes | Leavers return equipment and data | Company: offboarding checklist | Not started |
| 5.12 Classification of information | Yes | CUI, PII, financial data need handling rules | Company: classification scheme in `asset-inventory.md` | Not started |
| 5.13 Labelling of information | Yes | CUI marking requirements | Company: labelling procedure (CUI markings on exports and documents) | Not started |
| 5.14 Information transfer | Yes | Data shared with customers/DCAA | Company: transfer rules; Workbench: HTTPS/HSTS, CSV exports logged | Partial |
| 5.15 Access control | Yes | Core control | Workbench: org and workspace roles enforced server-side; Company: `access-control-policy.md` | Implemented (product) |
| 5.16 Identity management | Yes | Joiner/mover/leaver | Workbench: invites (7-day expiry), SCIM provisioning, deactivation (never deletion), employee linking; Company: HR triggers | Implemented (product) |
| 5.17 Authentication information | Yes | Credential protection | Workbench: no local passwords (SSO), SCIM token and secrets from vault, startup check; Company: IdP password/MFA policy | Partial |
| 5.18 Access rights | Yes | Provision, review, remove | Workbench: quarterly access reviews with Keep/Revoke, immediate revocation, stored evidence; 90-day dormant flag | Implemented (product) |
| 5.19 Information security in supplier relationships | Yes | Cloud, IdP, email suppliers | Company: `supplier-security-checklist.md` | Not started |
| 5.20 Addressing information security within supplier agreements | Yes | Contract terms | Company: security clauses, flow-downs (DFARS 7012) | Not started |
| 5.21 Managing information security in the ICT supply chain | Yes | Open-source dependencies | Workbench: SBOM, vulnerable package check, pinned versions; Company: supplier reviews | Partial |
| 5.22 Monitoring, review and change management of supplier services | Yes | Ongoing assurance | Company: yearly supplier review | Not started |
| 5.23 Information security for use of cloud services | `<Yes if cloud-hosted>` | `<Azure Gov / AWS GovCloud hosting>` | Company: cloud security requirements, FedRAMP evidence, shared-responsibility matrix | Not started |
| 5.24 Information security incident management planning and preparation | Yes | Required | Company: `incident-response-plan.md` | Not started |
| 5.25 Assessment and decision on information security events | Yes | Triage | Company: severity matrix in the IR plan; Workbench: security event log to triage from | Partial |
| 5.26 Response to information security incidents | Yes | Containment | Company: IR runbook; Workbench: deactivate user, sign out everywhere, legal hold | Partial |
| 5.27 Learning from information security incidents | Yes | Improvement | Company: post-incident review template in the IR plan | Not started |
| 5.28 Collection of evidence | Yes | Legal/contract (DFARS 90-day image retention) | Workbench: append-only security event log, CSV export, legal holds; Company: evidence handling procedure | Partial |
| 5.29 Information security during disruption | Yes | Keep controls during incidents | Company: `business-continuity-plan.md` | Not started |
| 5.30 ICT readiness for business continuity | Yes | Payroll/billing deadlines | Company: BC/DR plan with RTO/RPO; Workbench: backups, health checks | Partial |
| 5.31 Legal, statutory, regulatory and contractual requirements | Yes | FAR/DFARS/CMMC/privacy | Company: legal register | Not started |
| 5.32 Intellectual property rights | Yes | Software licences | Company: licence inventory; Workbench: SBOM lists package licences | Partial |
| 5.33 Protection of records | Yes | FAR 4.703, DCAA | Workbench: protected record types never auto-deleted (≥ 3 years after contract close), legal holds, append-only audit; Company: records schedule | Implemented (product) |
| 5.34 Privacy and protection of PII | Yes | Employee PII | Company: privacy notice and PII inventory; Workbench: role-restricted rates and personal data, retention | Partial |
| 5.35 Independent review of information security | Yes | Required assurance | Company: internal audit by independent auditor; external pen test | Not started |
| 5.36 Compliance with policies, rules and standards for information security | Yes | Check adherence | Company: internal audit programme; Workbench: security event review | Not started |
| 5.37 Documented operating procedures | Yes | Repeatable operations | Workbench: `../operations/backup-restore.md`, `../security/configuration.md`, README; Company: deployment runbooks | Partial |

## A.6 People controls

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 6.1 Screening | Yes | Access to CUI and financial data | Company: background checks per role (and contract requirements) | Not started |
| 6.2 Terms and conditions of employment | Yes | Security obligations | Company: employment contract clauses | Not started |
| 6.3 Information security awareness, education and training | Yes | Phishing is the top risk (R02) | Company: annual training plus phishing exercises; developer secure coding training | Not started |
| 6.4 Disciplinary process | Yes | Enforce policy | Company: HR disciplinary procedure | Not started |
| 6.5 Responsibilities after termination or change of employment | Yes | Leavers | Company: offboarding; Workbench: deactivation, SCIM, sign out everywhere | Partial |
| 6.6 Confidentiality or non-disclosure agreements | Yes | Protect customer data | Company: NDAs for staff and contractors | Not started |
| 6.7 Remote working | Yes | Remote staff | Company: remote work policy; Workbench: browser-based, HTTPS, session timeouts | Partial |
| 6.8 Information security event reporting | Yes | Early detection | Company: reporting channel in the IR plan; `../../SECURITY.md` for external reporters | Partial |

## A.7 Physical controls

*If Workbench runs only in a cloud region and you have no server room, these still apply to offices where staff
work with the data, and the cloud provider's physical controls are evidence for the hosting part (A.5.23).*

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 7.1 Physical security perimeters | Yes | Offices and any server room | Company / hosting provider | Not started |
| 7.2 Physical entry | Yes | Controlled entry | Company: badges, visitor log | Not started |
| 7.3 Securing offices, rooms and facilities | Yes | | Company | Not started |
| 7.4 Physical security monitoring | Yes | | Company: CCTV/alarms as appropriate; hosting provider | Not started |
| 7.5 Protecting against physical and environmental threats | Yes | | Company / hosting provider | Not started |
| 7.6 Working in secure areas | `<Yes/No>` | `<only if you have secure areas>` | Company | Not started |
| 7.7 Clear desk and clear screen | Yes | Shared offices | Company: policy; Workbench: session idle timeout (30 min) | Partial |
| 7.8 Equipment siting and protection | Yes | | Company / hosting provider | Not started |
| 7.9 Security of assets off-premises | Yes | Laptops | Company: disk encryption, MDM | Not started |
| 7.10 Storage media | Yes | Backups, removable media | Company: encrypted backup media, media policy | Not started |
| 7.11 Supporting utilities | Yes | Power/cooling for servers | Company / hosting provider | Not started |
| 7.12 Cabling security | `<Yes/No>` | `<on-prem only>` | Company / hosting provider | Not started |
| 7.13 Equipment maintenance | Yes | | Company / hosting provider | Not started |
| 7.14 Secure disposal or re-use of equipment | Yes | Disks with data | Company: wiping/destruction with certificates | Not started |

## A.8 Technological controls

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 8.1 User end point devices | Yes | Laptops and the desktop client | Company: MDM, encryption, patching; Workbench: thin desktop client keeps data on the server | Partial |
| 8.2 Privileged access rights | Yes | Admin/Finance roles | Workbench: roles, role changes logged, self-demotion and last-admin protection, access reviews; Company: limit admins, separate admin accounts for infrastructure | Implemented (product) |
| 8.3 Information access restriction | Yes | Need to know | Workbench: workspace membership, Finance-only rates and budgets, permission decorators, 404 for unseen sheets; SaaS: a database per tenant (see the SaaS rows below) | Implemented (product) |
| 8.4 Access to source code | Yes | Protect code and secrets | Company: GitHub org access, branch protection; Workbench: CODEOWNERS, gitleaks | Partial |
| 8.5 Secure authentication | Yes | Core control | Workbench: OIDC SSO (PKCE), idle 30 min / absolute 12 h sessions, circuit re-validation, sign out everywhere, rate-limited sign-in; Company: MFA at IdP | Implemented (product) |
| 8.6 Capacity management | Yes | Availability | Company: monitor capacity; Workbench: OpenTelemetry metrics, health checks | Partial |
| 8.7 Protection against malware | Yes | Uploads, endpoints | Workbench: attachment type allow-list and size limit; Company: endpoint protection, storage scanning | Partial |
| 8.8 Management of technical vulnerabilities | Yes | Dependencies and hosts | Workbench: CI fails on High/Critical packages, SBOM, `SECURITY.md` disclosure process; Company: OS/DB patching, scanning, pen test | Partial |
| 8.9 Configuration management | Yes | Secure baselines | Workbench: startup check refuses weak production config; documented settings; Company: infrastructure as code, baselines | Partial |
| 8.10 Information deletion | Yes | Retention limits | Workbench: retention policies and nightly purge of expired purgeable records, deletion reports; Company: disposal of backups and media | Implemented (product) |
| 8.11 Data masking | `<Yes>` | Test data, exports | Company: no production data in test, or masked; Workbench: cost rates hidden from non-Finance roles | Partial |
| 8.12 Data leakage prevention | Yes | CSV exports of labor and financial data | Workbench: exports role-restricted and logged; Company: DLP on email/endpoints | Partial |
| 8.13 Information backup | Yes | Ransomware, data loss | Workbench: `scripts/backup.sh`/`restore.sh`, procedure, tested restore; Company: `backup-policy.md`, schedule, off-site copies | Partial |
| 8.14 Redundancy of information processing facilities | Yes | Availability targets | Company: redundant hosting per BC plan; Workbench: stateless web host, DB-stored Data Protection keys allow several instances | Partial |
| 8.15 Logging | Yes | Detect and prove | Workbench: append-only security event log (sign-ins, sign-outs, failures, role changes, invites, deactivation, SCIM, access reviews, 403s, exports, period close, settings), viewer, CSV export, retention; audit log for financial changes | Implemented (product) |
| 8.16 Monitoring activities | Yes | Detect anomalies | Workbench: /health endpoints, OpenTelemetry (OTLP), structured logs; Company: SIEM, alerting, log review | Partial |
| 8.17 Clock synchronization | Yes | Log correlation | Company: NTP on hosts; Workbench records all times in UTC | Partial |
| 8.18 Use of privileged utility programs | Yes | DB consoles, scripts | Company: restrict and log use of psql/sqlite3 and admin tools | Not started |
| 8.19 Installation of software on operational systems | Yes | Production change | Company: deploy only CI-built artifacts | Not started |
| 8.20 Networks security | Yes | Internet-facing app | Workbench: HTTPS/HSTS, rate limiting, forwarded headers only from known proxies; Company: firewalls, TLS config | Partial |
| 8.21 Security of network services | Yes | Hosting and IdP services | Company: supplier SLAs and security; Workbench: TLS to IdP (HTTPS metadata required) | Partial |
| 8.22 Segregation of networks | Yes | Isolate DB | Company: DB and blob store on private networks | Not started |
| 8.23 Web filtering | Yes | Staff browsing | Company: DNS/web filtering for staff devices. (Workbench's CSP limits what *its* pages can load, but that is not this control.) | Not started |
| 8.24 Use of cryptography | Yes | Data in transit and at rest | Workbench: TLS/HSTS, Data Protection keys persisted and optionally certificate-encrypted, secrets from vault; Company: key management, DB and disk encryption | Partial |
| 8.25 Secure development life cycle | Yes | We build Workbench | Workbench: `secure-development-policy.md`, CI pipeline, threat model | Implemented (product) |
| 8.26 Application security requirements | Yes | | Workbench: CSP and security headers, antiforgery, permission model, input validation; threat model requirements | Implemented (product) |
| 8.27 Secure system architecture and engineering principles | Yes | | Workbench: layered architecture (UI never touches the DB), server-side enforcement, threat model | Implemented (product) |
| 8.28 Secure coding | Yes | | Workbench: conventions, code review via CODEOWNERS, analyzers; Company: developer training | Partial |
| 8.29 Security testing in development and acceptance | Yes | | Workbench: automated security tests (permissions, headers, rate limits, SCIM auth, session revocation); Company: pen test before major releases | Partial |
| 8.30 Outsourced development | `<Yes/No>` | `<if contractors develop>` | Company: same policy and reviews for contractors | Not started |
| 8.31 Separation of development, test and production environments | Yes | | Workbench: dev sign-in exists only in Development; startup check; Company: separate environments and credentials | Partial |
| 8.32 Change management | Yes | | Company: PR review, CI gates, release approval; Workbench: CODEOWNERS, CI, schema changes only through reviewed EF Core migrations for both providers (CI fails on model drift), applied by `Workbench.Web --migrate` as a deploy step | Partial |
| 8.33 Test information | Yes | Production data in tests | Workbench: tests use seeded/synthetic data; Company: no production copies in test without masking | Partial |
| 8.34 Protection of information systems during audit testing | Yes | Audits and pen tests | Company: plan audit tests, read-only accounts, schedule | Not started |

## SaaS (MultiTenant) deployment

*Extra rows for an organization that runs Workbench as a multi-tenant service (`Tenancy:Mode=MultiTenant`). They add to
the rows above for the same controls; a self-hosted, single-tenant deployment can mark them "No: single tenant".*

| Control | Applicable | Justification | Implementation | Status |
| --- | --- | --- | --- | --- |
| 8.3 Information access restriction (tenants) | `<Yes if SaaS>` | Customers must never see each other's data | Workbench: one database per tenant; tenant resolved from the subdomain before authentication; the database factory opens only the resolved tenant's database and refuses without one; cookies and Data Protection purposes per tenant; tenant-keyed caches, live updates and SignalR groups; background jobs run per tenant; isolation tests in CI on SQLite and PostgreSQL | Implemented (product) |
| 8.3 Information access restriction (host administration) | `<Yes if SaaS>` | Operating the service is not a customer role | Workbench: host admins listed in `Tenancy:HostAdmins`, only on the base domain (`/host`), separate from every tenant's Admin role; host audit log in the catalog. Company: keep the list short and review it with the quarterly access review | Implemented (product) / Company |
| 5.23 Information security for use of cloud services (as provider) | `<Yes if SaaS>` | Customers rely on our isolation and operations | Workbench: tenant isolation, provisioning with an invited first Admin, suspension; per-tenant database size and last activity on `/host`. Company: shared-responsibility statement for customers, per-tenant backup/restore runbook, tenant offboarding and data return/deletion procedure | Partial |
| 8.11 Data masking (tenants) | `<Yes if SaaS>` | Support staff and host admins shouldn't see tenant data | Workbench: the host site shows only tenant names, status, size and activity, never tenant records; tenant connection strings encrypted in the catalog; vendor TINs encrypted per tenant. Company: support access to a tenant only through an invited, audited tenant account | Partial |
| 8.31 Separation of environments (tenants) | `<Yes if SaaS>` | Development conveniences must not reach production | Workbench: the `X-Tenant` header and development tenants work only in Development; the startup check refuses MultiTenant production without invites, host admins and a real base domain; production migrates only through `--migrate`. Company: separate catalog, tenant databases and Data Protection keys per environment | Partial |

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
