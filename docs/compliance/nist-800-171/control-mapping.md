# NIST SP 800-171 Rev. 2 / CMMC Level 2: control mapping

All 110 security requirements of NIST SP 800-171 Rev. 2 (the basis of CMMC Level 2 and DFARS 252.204-7012), with who
meets each one when Workbench is part of the system that processes CUI.

**Responsibility** (a requirement often needs more than one):

- **W** = Workbench: a product feature does it (named, with where to find it).
- **C** = Customer: the contractor's own policy, people or process (also for a self-hosted Workbench).
- **H** = Hosting: the infrastructure Workbench runs on. *Self-hosted*: the customer's data centre or cloud tenant.
  *Workbench SaaS*: the Workbench operator and its cloud provider (see [Gaps: Workbench and the hosted SaaS for CUI](https://github.com/finepointcgi/Workbench/blob/master/docs/compliance/nist-800-171/gaps.md) before relying on it for CUI).

**Status for Workbench** is about the product only: `Met` (the feature fully covers the W part), `Partial` (it helps;
something else is needed), `—` (no product part). Your SSP ([System Security Plan (SSP)](system-security-plan.md)) records
your own implementation status for every requirement; Workbench being "Met" doesn't make the requirement met for you.

Evidence references E1–E13 are listed in [Evidence catalog](https://github.com/finepointcgi/Workbench/blob/master/docs/iso/evidence-catalog.md).

## 3.1 Access Control (22)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.1.1 | Limit system access to authorized users, processes and devices | W C H | SSO only (no local accounts); invite-only or allowed-domain JIT; SCIM; deactivation; access reviews (E1, E3, E6) | Met | C: approve accounts; H: restrict hosts/admin consoles |
| 3.1.2 | Limit access to the transactions and functions users may execute | W C | Org roles (Admin, Finance, Timekeeper, ResourceManager), workspace roles, permission decorators on every service, restricted columns | Met | C: assign roles per job |
| 3.1.3 | Control the flow of CUI per approved authorizations | W C H | Exports role-restricted and logged; masked pay/bank/TIN; per-tenant databases | Partial | C: transfer rules, DLP; H: network egress control |
| 3.1.4 | Separate duties of individuals to reduce malevolent activity | W C | No self-approval; approval workflows; Finance vs Admin roles; audit log | Partial | C: assign conflicting duties to different people |
| 3.1.5 | Employ least privilege, including for security functions and privileged accounts | W C | Least-privilege roles; Admin needed for users/security; MFA for admins option; re-authentication for sensitive actions; last-admin protection | Partial | C: limit admins; separate admin accounts for infrastructure |
| 3.1.6 | Use non-privileged accounts for nonsecurity functions | C | Roles are per person; an admin can hold a separate non-admin account | — | C: policy and separate accounts |
| 3.1.7 | Prevent non-privileged users from executing privileged functions; audit them | W | Server-side enforcement; denials recorded as `access.denied` (E1) | Met | |
| 3.1.8 | Limit unsuccessful logon attempts | H C | Rate-limited sign-in endpoints; lockout is the IdP's | Partial | C: IdP lockout/smart lockout policy |
| 3.1.9 | Provide privacy and security notices consistent with CUI rules | C H | — | — | C: banner at the IdP sign-in page (Entra company branding / Okta sign-in widget text) |
| 3.1.10 | Use session lock with pattern-hiding displays after inactivity | W C | Idle timeout ends the session (30 min default, server setting) | Partial | C: OS screen lock ≤ 15 min (MDM) |
| 3.1.11 | Terminate (automatically) a user session after a defined condition | W | Idle and absolute session limits, organization's shorter limit, deactivation and "sign out everywhere" end sessions at once | Met | |
| 3.1.12 | Monitor and control remote access sessions | W H C | All access is remote over HTTPS; sign-ins and sessions logged (E1) | Partial | H/C: VPN/ZTNA if required, IdP Conditional Access |
| 3.1.13 | Employ cryptographic mechanisms to protect remote access confidentiality | H W | HSTS, Secure cookies; refuses plain-HTTP production config | Partial | H: TLS 1.2+ with FIPS-validated modules |
| 3.1.14 | Route remote access via managed access control points | H | — | — | H: load balancer/WAF as the single entry point |
| 3.1.15 | Authorize remote execution of privileged commands and access to security information | W C | Admin role required for security settings, events and users; re-authentication for sign-in settings | Partial | C: authorize infrastructure admin access |
| 3.1.16 | Authorize wireless access prior to connections | C H | — | — | C: corporate Wi-Fi policy |
| 3.1.17 | Protect wireless access with authentication and encryption | C H | — | — | C: WPA2/3-Enterprise |
| 3.1.18 | Control connection of mobile devices | C | — | — | C: MDM / IdP device compliance (Conditional Access) |
| 3.1.19 | Encrypt CUI on mobile devices and computing platforms | C | Thin clients: no local data store | — | C: full-disk encryption |
| 3.1.20 | Verify and control/limit connections to external systems | C H | Integrations (payroll, IdP, SMTP) configured by admins only | Partial | C: approve external systems; H: egress rules |
| 3.1.21 | Limit use of portable storage devices on external systems | C | — | — | C: removable-media policy |
| 3.1.22 | Control CUI posted or processed on publicly accessible systems | C W | Workbench has no public pages; every page needs sign-in | Partial | C: review before publishing |

## 3.2 Awareness and Training (3)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.2.1 | Make users aware of security risks and applicable policies | C | — | — | C: awareness program |
| 3.2.2 | Train personnel for their security duties | C | Admin guides (SSO, configuration) support admin training | — | C: role-based training |
| 3.2.3 | Insider threat awareness training | C | — | — | C: training |

## 3.3 Audit and Accountability (9)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.3.1 | Create and retain audit logs to monitor, analyze, investigate and report unlawful or unauthorized activity | W H | Append-only security event log (sign-ins with `amr`, MFA refusals, sessions, users, roles, SCIM, denials, exports, reveals, settings) and audit log; configurable retention (E1, E2) | Met | H: infrastructure logs; C: retention ≥ contract needs |
| 3.3.2 | Ensure actions of individual users can be traced to them | W | Every event and audit row names the person (no shared accounts) | Met | C: no shared IdP accounts |
| 3.3.3 | Review and update logged events | C | Event catalog in `evidence-catalog.md` | — | C: yearly review of what is logged |
| 3.3.4 | Alert in the event of an audit logging process failure | W H C | Failure to write a security event is logged as an error | Partial | C/H: alert on that log message and on pipeline gaps |
| 3.3.5 | Correlate audit review, analysis and reporting | C H | Event filters and CSV export; OpenTelemetry | Partial | C: SIEM correlation |
| 3.3.6 | Provide audit record reduction and report generation | W | Security events: filter by date, type, actor, outcome, text; CSV export | Met | |
| 3.3.7 | Use an authoritative time source for time stamps | H W | All times recorded in UTC from the host clock | Partial | H: NTP |
| 3.3.8 | Protect audit information and tools from unauthorized access, modification and deletion | W H | Append-only in the product, Admin-only viewing, exports logged | Partial | H: database access control; forward to a SIEM |
| 3.3.9 | Limit management of audit logging to a subset of privileged users | W C | Only Admins change retention (`settings.changed`) | Met | C: limit DB admins |

## 3.4 Configuration Management (9)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.4.1 | Establish baseline configurations and inventories | C H W | Documented configuration (`configuration.md`), SBOM per build | Partial | C: inventory; H: hardened images |
| 3.4.2 | Establish and enforce security configuration settings | W H | Startup check refuses weak production config (dev sign-in, HTTP, HTTP IdP metadata, short SCIM token, SQLite) | Partial | H: OS/DB baselines (CIS/STIG) |
| 3.4.3 | Track, review, approve and log changes | W C | Security settings changes logged (`settings.sso`, `settings.changed`); schema changes via reviewed migrations | Partial | C: change approval process |
| 3.4.4 | Analyze the security impact of changes | C | Threat model maintained with the code | — | C: impact analysis in change process |
| 3.4.5 | Define and enforce access restrictions for change | C H | — | — | C/H: deploy rights, branch protection |
| 3.4.6 | Employ least functionality | H W | Only HTTPS endpoints the app needs; dev features absent outside Development | Partial | H: disable unneeded services/ports |
| 3.4.7 | Restrict nonessential programs, functions, ports, protocols and services | H | — | — | H: firewall, hardening |
| 3.4.8 | Apply deny-by-exception (blacklist) or permit-by-exception (whitelisting) policy | C H | CSP `script-src 'self'` for Workbench pages | — | C: application allow-listing on endpoints |
| 3.4.9 | Control and monitor user-installed software | C | — | — | C: endpoint management |

## 3.5 Identification and Authentication (11)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.5.1 | Identify system users, processes and devices | W C | One account per email from the IdP; SCIM external ids; service tokens named | Met | C: unique IdP identities |
| 3.5.2 | Authenticate users, processes or devices before access | W | OIDC (PKCE) for every page and API; bearer tokens validated; SCIM token | Met | |
| 3.5.3 | Use MFA for local and network access to privileged accounts and for network access to non-privileged accounts | W C | Refuses sign-ins without IdP-reported MFA (`amr`/`acr`); set **Everyone** for CUI (Sign-in settings, E6, `signin.mfa_required`) | Met (with "Everyone") | C: MFA at the IdP; H: MFA on admin consoles |
| 3.5.4 | Employ replay-resistant authentication for network access to privileged and non-privileged accounts | W C | OIDC code flow with PKCE, nonce and state; TLS | Met | C: phishing-resistant factors recommended |
| 3.5.5 | Prevent reuse of identifiers for a defined period | W C | Users are deactivated, never deleted, so an email can't be reissued to someone else in Workbench | Partial | C: IdP identifier policy |
| 3.5.6 | Disable identifiers after a defined period of inactivity | W C | Access reviews flag 90-day dormant users; Revoke deactivates | Partial | C: IdP inactivity disablement |
| 3.5.7 | Enforce minimum password complexity | C | No Workbench passwords | — | C: IdP policy |
| 3.5.8 | Prohibit password reuse for a number of generations | C | — | — | C: IdP policy |
| 3.5.9 | Allow temporary passwords only with immediate change | C | — | — | C: IdP policy |
| 3.5.10 | Store and transmit only cryptographically protected passwords | C W | No passwords stored; IdP client secrets encrypted at rest | Partial | C: IdP |
| 3.5.11 | Obscure feedback of authentication information | C | Authentication happens at the IdP | — | C: IdP |

## 3.6 Incident Response (3)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.6.1 | Establish an operational incident-handling capability | C W | Containment tools: deactivate, sign out everywhere, require MFA/own IdP, legal hold; evidence in E1/E2 | Partial | C: IR plan (`../../iso/incident-response-plan.md`) |
| 3.6.2 | Track, document and report incidents (DFARS: 72 hours to DIBNet) | C | Security events and CSV export support reporting | — | C: reporting, 90-day image retention |
| 3.6.3 | Test the incident response capability | C | — | — | C: tabletop exercises |

## 3.7 Maintenance (6)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.7.1 | Perform maintenance on systems | H C | Releases with migrations applied by `--migrate` | — | H/C: patching |
| 3.7.2 | Provide controls on maintenance tools, techniques, mechanisms and personnel | H C | — | — | H/C |
| 3.7.3 | Sanitize equipment removed for off-site maintenance | H C | — | — | H/C: NIST SP 800-88 |
| 3.7.4 | Check media with diagnostic programs for malicious code | H C | — | — | H/C |
| 3.7.5 | Require MFA for nonlocal maintenance sessions and terminate them when complete | H C | Workbench admin functions use the same MFA rule | Partial | H: MFA on consoles, bastions |
| 3.7.6 | Supervise maintenance personnel without access authorization | C H | — | — | C/H |

## 3.8 Media Protection (9)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.8.1 | Protect system media containing CUI | H C | — | — | H: encrypted storage; C: paper and removable media |
| 3.8.2 | Limit access to CUI on system media to authorized users | W H | Attachments only through permission-checked services | Partial | H: storage account access |
| 3.8.3 | Sanitize or destroy media before disposal or reuse | H C | Retention purge for purgeable records | — | H/C: NIST SP 800-88 |
| 3.8.4 | Mark media with CUI markings and distribution limitations | C | — | — | C: marking procedure (exports, printed reports) |
| 3.8.5 | Control access to and accountability for media during transport | C | Exports logged with actor | — | C |
| 3.8.6 | Use cryptography to protect CUI on digital media during transport | C H | Backups via `scripts/backup.sh` are to be stored encrypted | — | C/H: encrypted backup media |
| 3.8.7 | Control the use of removable media | C | — | — | C |
| 3.8.8 | Prohibit portable storage devices without an identifiable owner | C | — | — | C |
| 3.8.9 | Protect the confidentiality of backup CUI at storage locations | H C | Backup/restore procedure | — | H/C: encrypted, access-controlled backups |

## 3.9 Personnel Security (2)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.9.1 | Screen individuals prior to authorizing access | C | — | — | C: HR screening |
| 3.9.2 | Protect CUI during personnel actions (terminations, transfers) | W C | SCIM deprovisioning, deactivation ends sessions and tokens at once, roles from IdP groups, access reviews | Partial | C: HR→IT offboarding same day |

## 3.10 Physical Protection (6)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.10.1 | Limit physical access to systems and environments to authorized individuals | H C | — | — | H: data centre; C: offices |
| 3.10.2 | Protect and monitor the physical facility and support infrastructure | H C | — | — | H/C |
| 3.10.3 | Escort and monitor visitors | C H | — | — | C/H |
| 3.10.4 | Maintain audit logs of physical access | C H | — | — | C/H |
| 3.10.5 | Control and manage physical access devices | C H | — | — | C/H |
| 3.10.6 | Enforce safeguarding measures for CUI at alternate work sites | C | Session limits and MFA apply everywhere | — | C: remote work policy |

## 3.11 Risk Assessment (3)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.11.1 | Periodically assess risk to operations, assets and individuals | C | Starter risk register and threat model | — | C: `../../iso/risk-register.md` |
| 3.11.2 | Scan for vulnerabilities periodically and when new ones are identified | W H C | CI fails on vulnerable packages; SBOM per build | Partial | H/C: host and web scans, pen test |
| 3.11.3 | Remediate vulnerabilities per risk assessments | C W | Dependency updates per release | Partial | C: patch SLAs |

## 3.12 Security Assessment (4)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.12.1 | Periodically assess security controls for effectiveness | C | Evidence catalog; access reviews | — | C: yearly self-assessment (SPRS score) |
| 3.12.2 | Develop and implement plans of action (POA&M) | C | — | — | C: POA&M in the SSP |
| 3.12.3 | Monitor security controls on an ongoing basis | C W | Security event log, health and telemetry | Partial | C: continuous monitoring |
| 3.12.4 | Develop, document and update a system security plan | C | SSP template | — | C: [System Security Plan (SSP)](system-security-plan.md) |

## 3.13 System and Communications Protection (16)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.13.1 | Monitor, control and protect communications at external and key internal boundaries | H W | Rate limiting; trusted proxies only for forwarded headers | Partial | H: firewall, WAF |
| 3.13.2 | Employ architectural designs and engineering principles that promote security | W | Layered architecture, server-side enforcement, threat model, database per tenant | Met | |
| 3.13.3 | Separate user functionality from system management functionality | W H | Admin functions behind the Admin role; host administration only on the base domain (`/host`) | Partial | H: management plane separation |
| 3.13.4 | Prevent unauthorized and unintended information transfer via shared resources | W H | Tenant-qualified caches, per-tenant databases and Data Protection purposes | Met | H: platform isolation |
| 3.13.5 | Implement subnetworks for publicly accessible components | H | — | — | H: DMZ / private DB subnet |
| 3.13.6 | Deny network traffic by default, allow by exception | H | — | — | H |
| 3.13.7 | Prevent split tunneling for remote devices | C | — | — | C: VPN configuration |
| 3.13.8 | Implement cryptographic mechanisms to prevent unauthorized disclosure of CUI during transmission | H W | HSTS, Secure cookies, HTTPS-only IdP metadata | Partial | H: TLS 1.2+ (FIPS-validated), TLS to DB |
| 3.13.9 | Terminate network connections at the end of sessions or after inactivity | W H | Sessions end on idle/absolute limit; Blazor circuits re-validate | Partial | H: LB idle timeouts |
| 3.13.10 | Establish and manage cryptographic keys | W C H | Data Protection key ring (90-day rotation, certificate-encrypted) | Partial | C/H: `../../iso/cryptography-policy.md` |
| 3.13.11 | Employ FIPS-validated cryptography to protect CUI | H C | .NET uses the OS crypto provider | Partial | H: FIPS mode OS / validated modules ([Gaps: Workbench and the hosted SaaS for CUI](https://github.com/finepointcgi/Workbench/blob/master/docs/compliance/nist-800-171/gaps.md)) |
| 3.13.12 | Prohibit remote activation of collaborative computing devices | C | — | — | C |
| 3.13.13 | Control and monitor the use of mobile code | W C | CSP `script-src 'self'`, no inline scripts or `eval` | Met | C: browser policies |
| 3.13.14 | Control and monitor the use of VoIP | C | — | — | C |
| 3.13.15 | Protect the authenticity of communications sessions | W | Encrypted, signed session cookies bound to the tenant; antiforgery; SameSite | Met | |
| 3.13.16 | Protect the confidentiality of CUI at rest | H W | Encrypted fields (bank numbers, TINs, IdP secrets) | Partial | H: database/storage encryption |

## 3.14 System and Information Integrity (7)

| ID | Requirement (summary) | Resp. | Workbench | Status | Customer / hosting part |
| --- | --- | --- | --- | --- | --- |
| 3.14.1 | Identify, report and correct system flaws in a timely manner | W C H | `SECURITY.md` disclosure; CI vulnerability gate | Partial | C/H: patching |
| 3.14.2 | Provide protection from malicious code | W H C | Attachment type allow-list and size limit | Partial | H/C: anti-malware, storage scanning |
| 3.14.3 | Monitor security alerts and advisories and take action | C W | Dependency advisories in CI | Partial | C: subscribe to CISA/vendor advisories |
| 3.14.4 | Update malicious code protection mechanisms | H C | — | — | H/C |
| 3.14.5 | Perform periodic scans and real-time scans of files from external sources | H C | — | — | H/C: scan the blob store |
| 3.14.6 | Monitor systems, including inbound and outbound traffic, to detect attacks | W H C | Security events (failures, denials, rate limits), OpenTelemetry | Partial | H/C: IDS, SIEM |
| 3.14.7 | Identify unauthorized use of systems | W C | Sign-in failures, MFA refusals, denials, sensitive reveals and exports logged; access reviews | Partial | C: review cadence (`../../iso/logging-monitoring-policy.md`) |

## Totals

| Family | Requirements | Workbench Met | Workbench Partial | No product part |
| --- | --- | --- | --- | --- |
| 3.1 AC | 22 | 4 | 10 | 8 |
| 3.2 AT | 3 | 0 | 0 | 3 |
| 3.3 AU | 9 | 4 | 4 | 1 |
| 3.4 CM | 9 | 0 | 4 | 5 |
| 3.5 IA | 11 | 4 | 3 | 4 |
| 3.6 IR | 3 | 0 | 1 | 2 |
| 3.7 MA | 6 | 0 | 1 | 5 |
| 3.8 MP | 9 | 0 | 1 | 8 |
| 3.9 PS | 2 | 0 | 1 | 1 |
| 3.10 PE | 6 | 0 | 0 | 6 |
| 3.11 RA | 3 | 0 | 2 | 1 |
| 3.12 CA | 4 | 0 | 1 | 3 |
| 3.13 SC | 16 | 4 | 7 | 5 |
| 3.14 SI | 7 | 0 | 5 | 2 |
| **Total** | **110** | **16** | **40** | **54** |

Workbench alone never makes an organization compliant: about half the requirements are entirely the customer's or the
hosting provider's, and the "Partial" rows need them too. Use this table to fill the SSP, then score yourself with the
DoD Assessment Methodology for SPRS.
