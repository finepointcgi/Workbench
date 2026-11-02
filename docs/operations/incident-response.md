# Incident response: technical steps

The **incident response plan** (`docs/iso/incident-response-plan.md`) is the policy: definitions, roles, severities,
notification duties (including DFARS 252.204-7012's 72-hour report to DoD for cyber incidents affecting covered defense
information) and the post-incident review template. This page is what the on-call engineer does in Workbench's
production environment, phase by phase (ISO 27001 A.5.24 planning, A.5.25 assessment, A.5.26 response, A.5.27
learning, A.5.28 evidence, A.6.8 reporting).

## 0. Be ready (before anything happens)

- On-call rota and escalation contacts are current (plan §3); the incident channel and bridge exist.
- Access that responders need works and is tested quarterly: read access to logs and traces, the security event log
  (Admin role in a break-glass tenant account), `kubectl`/SSH to the hosts, the database (read-only role), the vault,
  the cloud console. Break-glass accounts are in the vault and alert when used.
- Logs are shipped off the hosts and retained ≥ 1 year; clocks are NTP-synced (all timestamps are UTC).
- Backups and the restore test are current ([Backup and restore](backup-restore.md)); the key-encryption key is
  escrowed ([Data Protection keys](data-protection-keys.md)).

## 1. Detect and assess (A.5.25)

Triggers: an alert ([Alerts and runbook](alerts.md)), a user or customer report, the IdP's risk detections, a vulnerability
report (`SECURITY.md`), a provider notice.

1. **Open an incident record** (plan §7 log) with an ID, the UTC time detected, who reported it and what was seen.
   Open the incident channel. From now on, write down every action with its UTC time.
2. **Assess** in the first 15 minutes: is it a security event (confidentiality, integrity, availability of
   Workbench data) or an operational fault? What is affected: the whole service, one tenant (MultiTenant: note the
   tenant slugs), one user? Is CUI, PII (employee data, pay rates), vendor TINs/bank accounts or financial records
   involved?
3. **Set severity** (plan §4) and page accordingly. SEV1/SEV2 bring in the incident lead, and security/legal for
   anything touching CUI or personal data. **If covered defense information may be affected, start the 72-hour DoD
   clock now** and tell the incident lead.

Where to look:

| Question | Source |
| --- | --- |
| Who signed in, from where, what was denied or exported | Users and security → **Security events** (per tenant; export it as evidence) and `workbench.security.events` metrics |
| Changes to rates, setup, budgets, approvals | The audit log (append-only `AuditEvent`) in the app |
| Host administration (tenants created/suspended) | `/host` → audit log (catalog `HostAuditEvents`) |
| Errors, unusual requests, job failures | JSON logs (by `TraceId`, `Tenant`), traces, metrics |
| Sign-in risk, MFA, token issuance | The identity provider's logs |
| Infrastructure access | Cloud audit logs (CloudTrail / Azure Activity Log), SSH/`kubectl` audit, KMS/Key Vault access logs |

## 2. Contain (A.5.26)

Choose the narrowest action that stops the harm, and record it:

| Situation | Action in Workbench |
| --- | --- |
| A user account is compromised | Deactivate the user (Users and security): sign-in is refused and **every session ends at once**. Revoke their sessions at the IdP and reset credentials/MFA there. |
| An admin account is compromised | As above, then review Security events for role changes, invites and exports by that account; revert role changes. |
| One tenant is attacked or is the source | Suspend the tenant on `/host` (403 for everyone in it; jobs skip it; data kept). |
| SCIM token leaked | Set `Scim:Enabled=false` or rotate `Scim:BearerToken` and roll out ([Runbooks](runbooks.md#rotate-a-secret)). |
| Other secret leaked (DB password, OIDC secret, API keys) | Rotate it now ([Runbooks](runbooks.md#rotate-a-secret)); check its use in the provider's logs. |
| Key-encryption key or a database copy exposed | [Data Protection keys](data-protection-keys.md#if-the-key-encryption-key-is-compromised); treat vendor TINs/bank accounts as potentially exposed. |
| Abusive traffic | Block at the proxy/WAF; lower `RateLimiting:*` temporarily. |
| Compromised host or container | Isolate it (remove from the load balancer, deny egress) **without deleting it**; replace with fresh replicas from the scanned image digest. |
| Whole service must stop | Scale web hosts to zero or take the proxy down with a maintenance page; the database keeps the evidence. |

## 3. Preserve evidence (A.5.28)

Before eradicating anything:

- Export the relevant **Security events** (CSV, itself recorded as `export.security_events`) and audit log entries.
- Save logs and traces for the window (from the log store, not just the hosts), with their time range and query.
- Snapshot affected volumes and the database (a PITR restore point or a fresh `scripts/backup-all.sh` run kept
  outside retention), and container images by digest. DFARS: keep images of affected systems **90 days**.
- Record hashes (`sha256sum`) of exported files, who collected them and where they are stored (restricted access).
  Keep the chain of custody in the incident record (plan §6.6 checklist).

## 4. Eradicate and recover (A.5.26, A.5.29, A.5.30)

1. Remove the cause: patch and redeploy (the CI pipeline scans images; deploy by digest), remove malicious accounts,
   invites or API clients, fix the misconfiguration (the startup check lists known-weak settings).
2. Rotate every secret the attacker could have seen.
3. Restore data if it was destroyed or altered: point-in-time recovery to before the incident, or a single tenant's
   logical backup ([Backup and restore](backup-restore.md#restoring)). Restore into new databases; keep the damaged ones
   as evidence.
4. Verify: readiness healthy, smoke test, trial balance and recent timesheets look right, Security events show normal
   activity. Monitor closely for at least 48 hours.
5. Reactivate suspended tenants/users only when the lead agrees.

## 5. Notify (A.5.26, A.6.8)

The incident lead and legal decide; engineering supplies the facts (what data, which tenants and people, when, how
contained). Obligations to check (plan §5): customers (contract terms, usually without undue delay), DoD via
DIBNet within 72 hours for covered defense information, breach notification laws for personal data, and the cloud or
email provider when their service is involved. Keep copies of everything sent.

## 6. Learn (A.5.27)

Within 10 business days: a blameless post-incident review (plan §8 template) with the timeline, root cause, what
worked, what didn't, and actions with owners and dates (new alerts, runbook changes, code fixes, training). Update the
risk register and these runbooks. Close the incident record only when actions are tracked.
