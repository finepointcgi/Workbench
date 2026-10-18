# Incident response plan

| | |
| --- | --- |
| Owner | `<ISMS manager>` |
| Approved by | `<top management>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.5.24–A.5.28, A.6.8, A.5.5 |
| Tested | `<tabletop exercise date>` |

## 1. Purpose and definitions

- **Event**: something observed that may affect security (a failed sign-in burst, a 403 spike, a phishing email).
- **Incident**: an event that has, or likely has, compromised confidentiality, integrity or availability.
- **Cyber incident affecting covered defense information** (DFARS 252.204-7012): must be reported to DoD within
  **72 hours** of discovery at https://dibnet.dod.mil (needs a DoD-approved medium assurance certificate), with
  images of affected systems preserved for **90 days**. Check each contract for other reporting duties.

## 2. Reporting (A.6.8)

Everyone reports suspected events immediately to `<security@company / phone / ticket queue>`. Don't investigate
alone, don't delete evidence, don't contact the attacker. External researchers report through `SECURITY.md`.

## 3. Team and contacts

| Role | Name | Phone | Backup |
| --- | --- | --- | --- |
| Incident lead | `<ISMS manager>` | `<>` | `<>` |
| Technical lead (Workbench) | `<Engineering lead>` | `<>` | `<>` |
| Infrastructure / hosting | `<Operations>` | `<>` | `<>` |
| Workbench administrator | `<Admin>` | `<>` | `<>` |
| Legal / contracts | `<Counsel>` | `<>` | `<>` |
| Communications | `<>` | `<>` | `<>` |
| Executive sponsor | `<CEO>` | `<>` | `<>` |

External contacts (A.5.5): DoD DC3 / DIBNet `<>`; contracting officers `<>`; FBI field office `<>`; CISA
(report@cisa.gov); cyber insurer `<policy no., hotline>`; hosting provider support `<>`; IdP support `<>`;
forensic retainer `<>`; state attorney general for breach notification `<>`.

## 4. Severity

| Severity | Examples | Response start | Notify |
| --- | --- | --- | --- |
| SEV1 Critical | CUI/PII exfiltration, ransomware, admin account takeover, production down > RTO | Immediately, 24/7 | Executive, legal; DoD within 72 h if covered |
| SEV2 High | Single account compromised, malware on an endpoint with access, suspected data exposure | 4 hours | ISMS manager, legal |
| SEV3 Medium | Policy violation, blocked attack with no impact, lost encrypted laptop | 1 business day | ISMS manager |
| SEV4 Low | Phishing reported and blocked, scan noise | Weekly review | — |

## 5. Phases

1. **Detect and assess (A.5.25).** Record the event in the incident log; assign severity; decide whether it's an
   incident.
2. **Contain (A.5.26).** Stop the damage while preserving evidence (see runbook).
3. **Preserve evidence (A.5.28).** Export logs before they roll over; snapshot affected systems; keep chain of custody;
   place legal holds.
4. **Eradicate and recover.** Remove the cause, patch, rotate secrets, restore from known-good backups, monitor.
5. **Notify.** Customers, DoD (72 h), regulators, individuals as required. Legal approves wording.
6. **Learn (A.5.27).** Post-incident review within 10 business days; actions into the corrective action log and the
   risk register.

## 6. Runbook: Workbench

### 6.1 Suspected compromised user account

1. In Workbench: **Users and security → Users → ⋮ → Sign out everywhere** (ends every session and token at once). If
   the account must be blocked, **Deactivate** with the incident number as the reason.
2. In the IdP: disable the account or reset credentials and MFA; revoke refresh tokens.
3. **Security events**: filter by actor = the user's email, last 30 days. Export CSV to the evidence folder. Look for
   `signin.*` from unusual IPs, `export.labor_csv` / `export.financial_csv`, `user.roles_changed`, `access.denied`.
4. **Setup → Audit log**: check changes by that user (rates, budgets, approvals).
5. If the user was an Admin, review every `user.*`, `invite.*`, `settings.changed` and `legal_hold.*` event since the
   suspected compromise and undo unauthorized changes.

### 6.2 Suspected data exfiltration through exports

1. Security events: `EventType = export.labor_csv` / `export.financial_csv` / `export.security_events`; note actor,
   time, IP, target.
2. Contain the account (6.1). Consider legal holds on the affected contracts/projects (**Retention → Legal holds**) so
   nothing related is purged during the investigation.
3. Determine whether CUI or PII was included; legal decides on notifications.

### 6.3 SCIM token leaked or abused

1. Generate a new token (`openssl rand -base64 48`), store it in the vault, update `Scim__BearerToken`, restart the web
   host, update the IdP's provisioning configuration.
2. Security events: `scim.request` with outcome `failure`/`denied`, and `user.created` / `user.deactivated` with
   actor `scim`. Reactivate wrongly deactivated users; deactivate users created by the attacker.

### 6.4 Leaked secret in source control

1. Rotate the secret first (it is compromised whether or not the commit is removed).
2. Remove it from history if required; gitleaks in CI confirms.
3. Review access logs of the system the secret protects.

### 6.5 Ransomware or destroyed database

1. Isolate affected hosts; don't pay or negotiate without legal and executive approval.
2. Preserve images (DFARS: 90 days) before rebuilding.
3. Rebuild infrastructure; restore from the latest clean, immutable backup (`../operations/backup-restore.md`);
   rotate every secret, including the Data Protection certificate.
4. Verify `/health/ready`, then data integrity (timesheets, financials) with Finance before reopening to users.

### 6.6 Evidence checklist

- [ ] Security event export (CSV) for the period, with the export time recorded
- [ ] Application logs and OpenTelemetry traces (if enabled)
- [ ] IdP sign-in logs
- [ ] Hosting/cloud audit logs (Azure Activity Log / AWS CloudTrail)
- [ ] Database snapshot of the affected period
- [ ] Hashes (SHA-256) of every evidence file; chain-of-custody form

## 7. Incident log template

| ID | Detected (UTC) | Reported by | Summary | Severity | Lead | Contained | Closed | Notifications | PIR done |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| INC-`<yyyy>`-001 | | | | | | | | | |

## 8. Post-incident review template

- What happened (timeline, UTC)?
- How was it detected; how could it have been detected sooner?
- What worked, what didn't?
- Root cause(s).
- Actions (owner, due date) → corrective action log; risks → risk register.
