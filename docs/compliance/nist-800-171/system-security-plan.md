# System Security Plan (SSP)

*Template for NIST SP 800-171 Rev. 2 requirement 3.12.4 and CMMC Level 2 (CA.L2-3.12.4). One SSP describes one system
boundary that processes, stores or transmits CUI. Replace `<placeholders>`, delete guidance in italics, keep the
version history. An assessor reads this first and then tests what it says, so describe what you actually do.*

| | |
| --- | --- |
| Organization | `<legal name>`, CAGE `<code>`, UEI `<id>` |
| System name | `<e.g. Acme Business Systems enclave>` |
| System owner | `<name, title, email>` |
| Information security lead | `<name, title, email>` |
| Version / date | `<1.0>` / `<date>` |
| Approved by | `<name, title>` / `<date>` |
| SPRS score / date | `<score>` / `<date>` (DoD Assessment Methodology) |
| CMMC level sought | `<Level 2 (self / C3PAO)>` |

## 1. System description

### 1.1 Purpose

`<What the system does>`. Workbench is the organization's project accounting, timekeeping, billing and collaboration
system; it holds `<which CUI: e.g. CUI//SP-CTI in project sheets and attachments, export-controlled technical data>` and
other sensitive data (labor costs, rates, employee PII).

### 1.2 Categories of information

| Information | CUI category (CUI Registry) | Where in the system |
| --- | --- | --- |
| `<Contract technical data>` | `<CTI>` | Workbench sheets/attachments, `<file share>` |
| Labor and cost records | Not CUI (company confidential) unless the contract says so | Workbench |
| Employee PII | `<Privacy, if applicable>` | Workbench, HR system |

### 1.3 Boundary and architecture

*Draw the boundary: every component that processes CUI and every component that protects it (security protection
assets). Attach the diagram as Figure 1.*

| Component | Role | Location / provider | In boundary | Notes |
| --- | --- | --- | --- | --- |
| Workbench web host (`Workbench.Web`) | Application (Blazor Server, API, SignalR) | `<Azure Government App Service / AWS GovCloud ECS / on-prem VM>` | Yes | `Tenancy:Mode=<SingleTenant>` |
| PostgreSQL | Database (all Workbench data, security event log) | `<Azure Database for PostgreSQL (Gov) / RDS GovCloud>` | Yes | Encrypted at rest; private network |
| Blob storage (attachments, receipts) | File storage | `<>` | Yes | |
| Workbench worker | Background jobs | `<>` | Yes | |
| Identity provider | Authentication, MFA | `<Entra ID (GCC High) / Okta (FedRAMP Moderate)>` | Security protection asset | See `../../security/sso-entra.md` |
| Load balancer / WAF / TLS termination | Boundary protection | `<>` | Security protection asset | FIPS-validated TLS |
| SIEM / log pipeline | Monitoring | `<>` | Security protection asset | |
| Email relay | Notifications (no CUI in emails) | `<>` | Out of scope (`<justify>`) | |
| Payroll provider | Hours export | `<Paylocity>` | Out of scope (no CUI) | |
| Endpoints | User laptops (browser, desktop client) | Company-managed | Yes | MDM, disk encryption |

### 1.4 Users and roles

| Role | Workbench role(s) | Number | Privileged | MFA |
| --- | --- | --- | --- | --- |
| Employees | Employee | `<n>` | No | Required (Workbench Sign-in: Everyone) |
| Timekeepers | Timekeeper | `<n>` | No | Required |
| Finance | Finance | `<n>` | Yes (rates, financials) | Required |
| Administrators | Admin | `<≤3>` | Yes | Required, phishing-resistant `<>` |
| Infrastructure admins | (cloud/DB) | `<n>` | Yes | Required |

### 1.5 Interconnections

| System | Direction | Data | Protection | Agreement |
| --- | --- | --- | --- | --- |
| IdP | Both | Identities, tokens, SCIM | TLS, signed tokens, bearer token | `<>` |
| Payroll provider | Out | Hours (no CUI) | TLS / SFTP | `<>` |

## 2. Workbench configuration in this system

*Record the settings an assessor will check. See `../../security/configuration.md`.*

| Setting | Value |
| --- | --- |
| Sign-in: identity provider | `<Entra ID tenant id / Okta org>` (organization provider, **only this provider**: `<on>`) |
| Sign-in: MFA | **Everyone** (required for 3.5.3); accepted values `<amr: mfa / acr: c1>` |
| Sign-in: provisioning | `<invite only / allowed domains>`; SCIM `<on>` |
| Re-authentication window / session limit | `<15 min>` / idle `<15–30 min>`, absolute `<8–12 h>` |
| Security event retention | `<≥ 1095 days>` |
| TLS | `<TLS 1.2+, FIPS mode on the terminator>` |
| Data Protection keys | Database store, encrypted with certificate `<thumbprint/vault>` |
| Backups | `<schedule, encryption, location>` |

## 3. Requirement implementation

*One entry per requirement (all 110 below). The Workbench part is copied from [NIST SP 800-171 Rev. 2 / CMMC Level 2: control mapping](control-mapping.md);
describe the customer and hosting parts as you implement them. Status: Implemented, Partially
implemented (with a POA&M item), Planned, Not applicable (with justification; rare at Level 2), Inherited (from a
FedRAMP-authorized provider, cite the package).*

### 3.1 Access Control

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.1.1 | Limit system access to authorized users, processes and devices | `<>` | Workbench: SSO only (no local accounts); invite-only or allowed-domain JIT; SCIM; deactivation; access reviews (E1, E3, E6). Customer/hosting: `<C: approve accounts; H: restrict hosts/admin consoles>` | `<>` | `<>` | |
| 3.1.2 | Limit access to the transactions and functions users may execute | `<>` | Workbench: Org roles (Admin, Finance, Timekeeper, ResourceManager), workspace roles, permission decorators on every service, restricted columns. Customer/hosting: `<C: assign roles per job>` | `<>` | `<>` | |
| 3.1.3 | Control the flow of CUI per approved authorizations | `<>` | Workbench: Exports role-restricted and logged; masked pay/bank/TIN; per-tenant databases. Customer/hosting: `<C: transfer rules, DLP; H: network egress control>` | `<>` | `<>` | |
| 3.1.4 | Separate duties of individuals to reduce malevolent activity | `<>` | Workbench: No self-approval; approval workflows; Finance vs Admin roles; audit log. Customer/hosting: `<C: assign conflicting duties to different people>` | `<>` | `<>` | |
| 3.1.5 | Employ least privilege, including for security functions and privileged accounts | `<>` | Workbench: Least-privilege roles; Admin needed for users/security; MFA for admins option; re-authentication for sensitive actions; last-admin protection. Customer/hosting: `<C: limit admins; separate admin accounts for infrastructure>` | `<>` | `<>` | |
| 3.1.6 | Use non-privileged accounts for nonsecurity functions | `<>` | Workbench: Roles are per person; an admin can hold a separate non-admin account. Customer/hosting: `<C: policy and separate accounts>` | `<>` | `<>` | |
| 3.1.7 | Prevent non-privileged users from executing privileged functions; audit them | `<>` | Workbench: Server-side enforcement; denials recorded as `access.denied` (E1). `<describe>` | `<>` | `<>` | |
| 3.1.8 | Limit unsuccessful logon attempts | `<>` | Workbench: Rate-limited sign-in endpoints; lockout is the IdP's. Customer/hosting: `<C: IdP lockout/smart lockout policy>` | `<>` | `<>` | |
| 3.1.9 | Provide privacy and security notices consistent with CUI rules | `<>` | Customer/hosting: `<C: banner at the IdP sign-in page (Entra company branding / Okta sign-in widget text)>` | `<>` | `<>` | |
| 3.1.10 | Use session lock with pattern-hiding displays after inactivity | `<>` | Workbench: Idle timeout ends the session (30 min default, server setting). Customer/hosting: `<C: OS screen lock ≤ 15 min (MDM)>` | `<>` | `<>` | |
| 3.1.11 | Terminate (automatically) a user session after a defined condition | `<>` | Workbench: Idle and absolute session limits, organization's shorter limit, deactivation and "sign out everywhere" end sessions at once. `<describe>` | `<>` | `<>` | |
| 3.1.12 | Monitor and control remote access sessions | `<>` | Workbench: All access is remote over HTTPS; sign-ins and sessions logged (E1). Customer/hosting: `<H/C: VPN/ZTNA if required, IdP Conditional Access>` | `<>` | `<>` | |
| 3.1.13 | Employ cryptographic mechanisms to protect remote access confidentiality | `<>` | Workbench: HSTS, Secure cookies; refuses plain-HTTP production config. Customer/hosting: `<H: TLS 1.2+ with FIPS-validated modules>` | `<>` | `<>` | |
| 3.1.14 | Route remote access via managed access control points | `<>` | Customer/hosting: `<H: load balancer/WAF as the single entry point>` | `<>` | `<>` | |
| 3.1.15 | Authorize remote execution of privileged commands and access to security information | `<>` | Workbench: Admin role required for security settings, events and users; re-authentication for sign-in settings. Customer/hosting: `<C: authorize infrastructure admin access>` | `<>` | `<>` | |
| 3.1.16 | Authorize wireless access prior to connections | `<>` | Customer/hosting: `<C: corporate Wi-Fi policy>` | `<>` | `<>` | |
| 3.1.17 | Protect wireless access with authentication and encryption | `<>` | Customer/hosting: `<C: WPA2/3-Enterprise>` | `<>` | `<>` | |
| 3.1.18 | Control connection of mobile devices | `<>` | Customer/hosting: `<C: MDM / IdP device compliance (Conditional Access)>` | `<>` | `<>` | |
| 3.1.19 | Encrypt CUI on mobile devices and computing platforms | `<>` | Workbench: Thin clients: no local data store. Customer/hosting: `<C: full-disk encryption>` | `<>` | `<>` | |
| 3.1.20 | Verify and control/limit connections to external systems | `<>` | Workbench: Integrations (payroll, IdP, SMTP) configured by admins only. Customer/hosting: `<C: approve external systems; H: egress rules>` | `<>` | `<>` | |
| 3.1.21 | Limit use of portable storage devices on external systems | `<>` | Customer/hosting: `<C: removable-media policy>` | `<>` | `<>` | |
| 3.1.22 | Control CUI posted or processed on publicly accessible systems | `<>` | Workbench: Workbench has no public pages; every page needs sign-in. Customer/hosting: `<C: review before publishing>` | `<>` | `<>` | |

### 3.2 Awareness and Training

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.2.1 | Make users aware of security risks and applicable policies | `<>` | Customer/hosting: `<C: awareness program>` | `<>` | `<>` | |
| 3.2.2 | Train personnel for their security duties | `<>` | Workbench: Admin guides (SSO, configuration) support admin training. Customer/hosting: `<C: role-based training>` | `<>` | `<>` | |
| 3.2.3 | Insider threat awareness training | `<>` | Customer/hosting: `<C: training>` | `<>` | `<>` | |

### 3.3 Audit and Accountability

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.3.1 | Create and retain audit logs to monitor, analyze, investigate and report unlawful or unauthorized activity | `<>` | Workbench: Append-only security event log (sign-ins with `amr`, MFA refusals, sessions, users, roles, SCIM, denials, exports, reveals, settings) and audit log; configurable retention (E1, E2). Customer/hosting: `<H: infrastructure logs; C: retention ≥ contract needs>` | `<>` | `<>` | |
| 3.3.2 | Ensure actions of individual users can be traced to them | `<>` | Workbench: Every event and audit row names the person (no shared accounts). Customer/hosting: `<C: no shared IdP accounts>` | `<>` | `<>` | |
| 3.3.3 | Review and update logged events | `<>` | Workbench: Event catalog in `evidence-catalog.md`. Customer/hosting: `<C: yearly review of what is logged>` | `<>` | `<>` | |
| 3.3.4 | Alert in the event of an audit logging process failure | `<>` | Workbench: Failure to write a security event is logged as an error. Customer/hosting: `<C/H: alert on that log message and on pipeline gaps>` | `<>` | `<>` | |
| 3.3.5 | Correlate audit review, analysis and reporting | `<>` | Workbench: Event filters and CSV export; OpenTelemetry. Customer/hosting: `<C: SIEM correlation>` | `<>` | `<>` | |
| 3.3.6 | Provide audit record reduction and report generation | `<>` | Workbench: Security events: filter by date, type, actor, outcome, text; CSV export. `<describe>` | `<>` | `<>` | |
| 3.3.7 | Use an authoritative time source for time stamps | `<>` | Workbench: All times recorded in UTC from the host clock. Customer/hosting: `<H: NTP>` | `<>` | `<>` | |
| 3.3.8 | Protect audit information and tools from unauthorized access, modification and deletion | `<>` | Workbench: Append-only in the product, Admin-only viewing, exports logged. Customer/hosting: `<H: database access control; forward to a SIEM>` | `<>` | `<>` | |
| 3.3.9 | Limit management of audit logging to a subset of privileged users | `<>` | Workbench: Only Admins change retention (`settings.changed`). Customer/hosting: `<C: limit DB admins>` | `<>` | `<>` | |

### 3.4 Configuration Management

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.4.1 | Establish baseline configurations and inventories | `<>` | Workbench: Documented configuration (`configuration.md`), SBOM per build. Customer/hosting: `<C: inventory; H: hardened images>` | `<>` | `<>` | |
| 3.4.2 | Establish and enforce security configuration settings | `<>` | Workbench: Startup check refuses weak production config (dev sign-in, HTTP, HTTP IdP metadata, short SCIM token, SQLite). Customer/hosting: `<H: OS/DB baselines (CIS/STIG)>` | `<>` | `<>` | |
| 3.4.3 | Track, review, approve and log changes | `<>` | Workbench: Security settings changes logged (`settings.sso`, `settings.changed`); schema changes via reviewed migrations. Customer/hosting: `<C: change approval process>` | `<>` | `<>` | |
| 3.4.4 | Analyze the security impact of changes | `<>` | Workbench: Threat model maintained with the code. Customer/hosting: `<C: impact analysis in change process>` | `<>` | `<>` | |
| 3.4.5 | Define and enforce access restrictions for change | `<>` | Customer/hosting: `<C/H: deploy rights, branch protection>` | `<>` | `<>` | |
| 3.4.6 | Employ least functionality | `<>` | Workbench: Only HTTPS endpoints the app needs; dev features absent outside Development. Customer/hosting: `<H: disable unneeded services/ports>` | `<>` | `<>` | |
| 3.4.7 | Restrict nonessential programs, functions, ports, protocols and services | `<>` | Customer/hosting: `<H: firewall, hardening>` | `<>` | `<>` | |
| 3.4.8 | Apply deny-by-exception (blacklist) or permit-by-exception (whitelisting) policy | `<>` | Workbench: CSP `script-src 'self'` for Workbench pages. Customer/hosting: `<C: application allow-listing on endpoints>` | `<>` | `<>` | |
| 3.4.9 | Control and monitor user-installed software | `<>` | Customer/hosting: `<C: endpoint management>` | `<>` | `<>` | |

### 3.5 Identification and Authentication

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.5.1 | Identify system users, processes and devices | `<>` | Workbench: One account per email from the IdP; SCIM external ids; service tokens named. Customer/hosting: `<C: unique IdP identities>` | `<>` | `<>` | |
| 3.5.2 | Authenticate users, processes or devices before access | `<>` | Workbench: OIDC (PKCE) for every page and API; bearer tokens validated; SCIM token. `<describe>` | `<>` | `<>` | |
| 3.5.3 | Use MFA for local and network access to privileged accounts and for network access to non-privileged accounts | `<>` | Workbench: Refuses sign-ins without IdP-reported MFA (`amr`/`acr`); set **Everyone** for CUI (Sign-in settings, E6, `signin.mfa_required`). Customer/hosting: `<C: MFA at the IdP; H: MFA on admin consoles>` | `<>` | `<>` | |
| 3.5.4 | Employ replay-resistant authentication for network access to privileged and non-privileged accounts | `<>` | Workbench: OIDC code flow with PKCE, nonce and state; TLS. Customer/hosting: `<C: phishing-resistant factors recommended>` | `<>` | `<>` | |
| 3.5.5 | Prevent reuse of identifiers for a defined period | `<>` | Workbench: Users are deactivated, never deleted, so an email can't be reissued to someone else in Workbench. Customer/hosting: `<C: IdP identifier policy>` | `<>` | `<>` | |
| 3.5.6 | Disable identifiers after a defined period of inactivity | `<>` | Workbench: Access reviews flag 90-day dormant users; Revoke deactivates. Customer/hosting: `<C: IdP inactivity disablement>` | `<>` | `<>` | |
| 3.5.7 | Enforce minimum password complexity | `<>` | Workbench: No Workbench passwords. Customer/hosting: `<C: IdP policy>` | `<>` | `<>` | |
| 3.5.8 | Prohibit password reuse for a number of generations | `<>` | Customer/hosting: `<C: IdP policy>` | `<>` | `<>` | |
| 3.5.9 | Allow temporary passwords only with immediate change | `<>` | Customer/hosting: `<C: IdP policy>` | `<>` | `<>` | |
| 3.5.10 | Store and transmit only cryptographically protected passwords | `<>` | Workbench: No passwords stored; IdP client secrets encrypted at rest. Customer/hosting: `<C: IdP>` | `<>` | `<>` | |
| 3.5.11 | Obscure feedback of authentication information | `<>` | Workbench: Authentication happens at the IdP. Customer/hosting: `<C: IdP>` | `<>` | `<>` | |

### 3.6 Incident Response

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.6.1 | Establish an operational incident-handling capability | `<>` | Workbench: Containment tools: deactivate, sign out everywhere, require MFA/own IdP, legal hold; evidence in E1/E2. Customer/hosting: `<C: IR plan (`../../iso/incident-response-plan.md`)>` | `<>` | `<>` | |
| 3.6.2 | Track, document and report incidents (DFARS: 72 hours to DIBNet) | `<>` | Workbench: Security events and CSV export support reporting. Customer/hosting: `<C: reporting, 90-day image retention>` | `<>` | `<>` | |
| 3.6.3 | Test the incident response capability | `<>` | Customer/hosting: `<C: tabletop exercises>` | `<>` | `<>` | |

### 3.7 Maintenance

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.7.1 | Perform maintenance on systems | `<>` | Workbench: Releases with migrations applied by `--migrate`. Customer/hosting: `<H/C: patching>` | `<>` | `<>` | |
| 3.7.2 | Provide controls on maintenance tools, techniques, mechanisms and personnel | `<>` | Customer/hosting: `<H/C>` | `<>` | `<>` | |
| 3.7.3 | Sanitize equipment removed for off-site maintenance | `<>` | Customer/hosting: `<H/C: NIST SP 800-88>` | `<>` | `<>` | |
| 3.7.4 | Check media with diagnostic programs for malicious code | `<>` | Customer/hosting: `<H/C>` | `<>` | `<>` | |
| 3.7.5 | Require MFA for nonlocal maintenance sessions and terminate them when complete | `<>` | Workbench: Workbench admin functions use the same MFA rule. Customer/hosting: `<H: MFA on consoles, bastions>` | `<>` | `<>` | |
| 3.7.6 | Supervise maintenance personnel without access authorization | `<>` | Customer/hosting: `<C/H>` | `<>` | `<>` | |

### 3.8 Media Protection

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.8.1 | Protect system media containing CUI | `<>` | Customer/hosting: `<H: encrypted storage; C: paper and removable media>` | `<>` | `<>` | |
| 3.8.2 | Limit access to CUI on system media to authorized users | `<>` | Workbench: Attachments only through permission-checked services. Customer/hosting: `<H: storage account access>` | `<>` | `<>` | |
| 3.8.3 | Sanitize or destroy media before disposal or reuse | `<>` | Workbench: Retention purge for purgeable records. Customer/hosting: `<H/C: NIST SP 800-88>` | `<>` | `<>` | |
| 3.8.4 | Mark media with CUI markings and distribution limitations | `<>` | Customer/hosting: `<C: marking procedure (exports, printed reports)>` | `<>` | `<>` | |
| 3.8.5 | Control access to and accountability for media during transport | `<>` | Workbench: Exports logged with actor. Customer/hosting: `<C>` | `<>` | `<>` | |
| 3.8.6 | Use cryptography to protect CUI on digital media during transport | `<>` | Workbench: Backups via `scripts/backup.sh` are to be stored encrypted. Customer/hosting: `<C/H: encrypted backup media>` | `<>` | `<>` | |
| 3.8.7 | Control the use of removable media | `<>` | Customer/hosting: `<C>` | `<>` | `<>` | |
| 3.8.8 | Prohibit portable storage devices without an identifiable owner | `<>` | Customer/hosting: `<C>` | `<>` | `<>` | |
| 3.8.9 | Protect the confidentiality of backup CUI at storage locations | `<>` | Workbench: Backup/restore procedure. Customer/hosting: `<H/C: encrypted, access-controlled backups>` | `<>` | `<>` | |

### 3.9 Personnel Security

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.9.1 | Screen individuals prior to authorizing access | `<>` | Customer/hosting: `<C: HR screening>` | `<>` | `<>` | |
| 3.9.2 | Protect CUI during personnel actions (terminations, transfers) | `<>` | Workbench: SCIM deprovisioning, deactivation ends sessions and tokens at once, roles from IdP groups, access reviews. Customer/hosting: `<C: HR→IT offboarding same day>` | `<>` | `<>` | |

### 3.10 Physical Protection

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.10.1 | Limit physical access to systems and environments to authorized individuals | `<>` | Customer/hosting: `<H: data centre; C: offices>` | `<>` | `<>` | |
| 3.10.2 | Protect and monitor the physical facility and support infrastructure | `<>` | Customer/hosting: `<H/C>` | `<>` | `<>` | |
| 3.10.3 | Escort and monitor visitors | `<>` | Customer/hosting: `<C/H>` | `<>` | `<>` | |
| 3.10.4 | Maintain audit logs of physical access | `<>` | Customer/hosting: `<C/H>` | `<>` | `<>` | |
| 3.10.5 | Control and manage physical access devices | `<>` | Customer/hosting: `<C/H>` | `<>` | `<>` | |
| 3.10.6 | Enforce safeguarding measures for CUI at alternate work sites | `<>` | Workbench: Session limits and MFA apply everywhere. Customer/hosting: `<C: remote work policy>` | `<>` | `<>` | |

### 3.11 Risk Assessment

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.11.1 | Periodically assess risk to operations, assets and individuals | `<>` | Workbench: Starter risk register and threat model. Customer/hosting: `<C: `../../iso/risk-register.md`>` | `<>` | `<>` | |
| 3.11.2 | Scan for vulnerabilities periodically and when new ones are identified | `<>` | Workbench: CI fails on vulnerable packages; SBOM per build. Customer/hosting: `<H/C: host and web scans, pen test>` | `<>` | `<>` | |
| 3.11.3 | Remediate vulnerabilities per risk assessments | `<>` | Workbench: Dependency updates per release. Customer/hosting: `<C: patch SLAs>` | `<>` | `<>` | |

### 3.12 Security Assessment

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.12.1 | Periodically assess security controls for effectiveness | `<>` | Workbench: Evidence catalog; access reviews. Customer/hosting: `<C: yearly self-assessment (SPRS score)>` | `<>` | `<>` | |
| 3.12.2 | Develop and implement plans of action (POA&M) | `<>` | Customer/hosting: `<C: POA&M in the SSP>` | `<>` | `<>` | |
| 3.12.3 | Monitor security controls on an ongoing basis | `<>` | Workbench: Security event log, health and telemetry. Customer/hosting: `<C: continuous monitoring>` | `<>` | `<>` | |
| 3.12.4 | Develop, document and update a system security plan | `<>` | Workbench: SSP template. Customer/hosting: `<C: [System Security Plan (SSP)](system-security-plan.md)>` | `<>` | `<>` | |

### 3.13 System and Communications Protection

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.13.1 | Monitor, control and protect communications at external and key internal boundaries | `<>` | Workbench: Rate limiting; trusted proxies only for forwarded headers. Customer/hosting: `<H: firewall, WAF>` | `<>` | `<>` | |
| 3.13.2 | Employ architectural designs and engineering principles that promote security | `<>` | Workbench: Layered architecture, server-side enforcement, threat model, database per tenant. `<describe>` | `<>` | `<>` | |
| 3.13.3 | Separate user functionality from system management functionality | `<>` | Workbench: Admin functions behind the Admin role; host administration only on the base domain (`/host`). Customer/hosting: `<H: management plane separation>` | `<>` | `<>` | |
| 3.13.4 | Prevent unauthorized and unintended information transfer via shared resources | `<>` | Workbench: Tenant-qualified caches, per-tenant databases and Data Protection purposes. Customer/hosting: `<H: platform isolation>` | `<>` | `<>` | |
| 3.13.5 | Implement subnetworks for publicly accessible components | `<>` | Customer/hosting: `<H: DMZ / private DB subnet>` | `<>` | `<>` | |
| 3.13.6 | Deny network traffic by default, allow by exception | `<>` | Customer/hosting: `<H>` | `<>` | `<>` | |
| 3.13.7 | Prevent split tunneling for remote devices | `<>` | Customer/hosting: `<C: VPN configuration>` | `<>` | `<>` | |
| 3.13.8 | Implement cryptographic mechanisms to prevent unauthorized disclosure of CUI during transmission | `<>` | Workbench: HSTS, Secure cookies, HTTPS-only IdP metadata. Customer/hosting: `<H: TLS 1.2+ (FIPS-validated), TLS to DB>` | `<>` | `<>` | |
| 3.13.9 | Terminate network connections at the end of sessions or after inactivity | `<>` | Workbench: Sessions end on idle/absolute limit; Blazor circuits re-validate. Customer/hosting: `<H: LB idle timeouts>` | `<>` | `<>` | |
| 3.13.10 | Establish and manage cryptographic keys | `<>` | Workbench: Data Protection key ring (90-day rotation, certificate-encrypted). Customer/hosting: `<C/H: `../../iso/cryptography-policy.md`>` | `<>` | `<>` | |
| 3.13.11 | Employ FIPS-validated cryptography to protect CUI | `<>` | Workbench: .NET uses the OS crypto provider. Customer/hosting: `<H: FIPS mode OS / validated modules ([Gaps: Workbench and the hosted SaaS for CUI](https://github.com/finepointcgi/Workbench/blob/master/docs/compliance/nist-800-171/gaps.md))>` | `<>` | `<>` | |
| 3.13.12 | Prohibit remote activation of collaborative computing devices | `<>` | Customer/hosting: `<C>` | `<>` | `<>` | |
| 3.13.13 | Control and monitor the use of mobile code | `<>` | Workbench: CSP `script-src 'self'`, no inline scripts or `eval`. Customer/hosting: `<C: browser policies>` | `<>` | `<>` | |
| 3.13.14 | Control and monitor the use of VoIP | `<>` | Customer/hosting: `<C>` | `<>` | `<>` | |
| 3.13.15 | Protect the authenticity of communications sessions | `<>` | Workbench: Encrypted, signed session cookies bound to the tenant; antiforgery; SameSite. `<describe>` | `<>` | `<>` | |
| 3.13.16 | Protect the confidentiality of CUI at rest | `<>` | Workbench: Encrypted fields (bank numbers, TINs, IdP secrets). Customer/hosting: `<H: database/storage encryption>` | `<>` | `<>` | |

### 3.14 System and Information Integrity

| ID | Requirement | Status | Implementation (Workbench part pre-filled; add yours) | Responsible | Evidence | POA&M |
| --- | --- | --- | --- | --- | --- | --- |
| 3.14.1 | Identify, report and correct system flaws in a timely manner | `<>` | Workbench: `SECURITY.md` disclosure; CI vulnerability gate. Customer/hosting: `<C/H: patching>` | `<>` | `<>` | |
| 3.14.2 | Provide protection from malicious code | `<>` | Workbench: Attachment type allow-list and size limit. Customer/hosting: `<H/C: anti-malware, storage scanning>` | `<>` | `<>` | |
| 3.14.3 | Monitor security alerts and advisories and take action | `<>` | Workbench: Dependency advisories in CI. Customer/hosting: `<C: subscribe to CISA/vendor advisories>` | `<>` | `<>` | |
| 3.14.4 | Update malicious code protection mechanisms | `<>` | Customer/hosting: `<H/C>` | `<>` | `<>` | |
| 3.14.5 | Perform periodic scans and real-time scans of files from external sources | `<>` | Customer/hosting: `<H/C: scan the blob store>` | `<>` | `<>` | |
| 3.14.6 | Monitor systems, including inbound and outbound traffic, to detect attacks | `<>` | Workbench: Security events (failures, denials, rate limits), OpenTelemetry. Customer/hosting: `<H/C: IDS, SIEM>` | `<>` | `<>` | |
| 3.14.7 | Identify unauthorized use of systems | `<>` | Workbench: Sign-in failures, MFA refusals, denials, sensitive reveals and exports logged; access reviews. Customer/hosting: `<C: review cadence (`../../iso/logging-monitoring-policy.md`)>` | `<>` | `<>` | |

## 4. Plan of Action and Milestones (3.12.2)

| # | Requirement | Weakness | Planned action | Owner | Milestone | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `<3.x.y>` | `<>` | `<>` | `<>` | `<date>` | `<Open>` |

*Under the CMMC rule only some requirements may be on a POA&M at assessment time, and they must be closed within 180
days. Check the current rule (32 CFR part 170) for which ones.*

## 5. Related documents

Policies: `../../iso/` (access control, cryptography, logging and monitoring, incident response, supplier security,
asset management, business continuity, backup, secure development). Gaps of the hosted service: [Gaps: Workbench and the hosted SaaS for CUI](https://github.com/finepointcgi/Workbench/blob/master/docs/compliance/nist-800-171/gaps.md).

## 6. Approval and review

Reviewed at least yearly and on significant change.

| Version | Date | Change | Approved by |
| --- | --- | --- | --- |
| 1.0 | `<date>` | Initial | `<>` |
