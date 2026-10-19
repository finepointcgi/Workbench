# Logging and monitoring policy

| | |
| --- | --- |
| Owner | `<ISMS manager / operations lead>` |
| Approved by | `<top management>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.8.15, A.8.16, A.8.17, A.5.25, A.5.28 · NIST SP 800-171 3.3.1–3.3.9, 3.14.6, 3.14.7 |

## 1. What is logged

| Log | Contents | Where | Retention | Who can read |
| --- | --- | --- | --- | --- |
| **Security event log** (Workbench) | Sign-ins (method, `amr`), failures, MFA refusals, re-authentications, sign-outs, rejected sessions, user creation (incl. just-in-time), role changes (incl. from IdP groups), invites, deactivation, SCIM calls, access reviews, permission denials and 403s, rate limiting, sensitive reveals and exports, sign-in settings changes, period close, retention runs, legal holds, compliance checklist answers | Tenant database, append-only (`SecurityEvents`) | `Retention:SecurityEventDays` (default 365; set `<≥ 3 years for CUI/DFARS>`) | Admins (**Users and security › Security events**), CSV export (itself logged) |
| **Audit log** (Workbench) | Changes to rates, setup, budgets, approvals, timesheet revisions with reasons | Tenant database, append-only (`AuditEvents`) | Protected: never auto-deleted (≥ 3 years after contract close) | Finance/Admin (**Setup › Audit log**) |
| **Host audit log** (MultiTenant) | Tenant creation, suspension, host admin actions | Catalog database | `<>` | Host admins |
| **Application logs** | Errors, warnings, request traces (no secrets, tokens or rates) | stdout → `<log pipeline / SIEM>` (`Logging:Console:FormatterName=json`) | `<90 days hot, 1 year archive>` | Operations |
| **Traces and metrics** | OpenTelemetry (OTLP), query strings dropped | `<collector>` | `<30 days>` | Operations |
| **Infrastructure** | Load balancer, WAF, database, OS, cloud control plane | `<provider>` | `<1 year>` | Operations |
| **Identity provider** | Sign-in logs, MFA, Conditional Access results | `<Entra / Okta>` | `<per IdP, export to SIEM>` | IdP admins |

Logs never contain passwords, tokens, client secrets, bank or pay details, or cost/bill rates (Workbench enforces this
for its own logs; see [../security/configuration.md](../security/configuration.md#logging)).

## 2. Protection (A.8.15, NIST 3.3.8)

- Security and audit events are append-only in the product; only the retention job deletes security events after the
  retention period; audit records are never purged.
- Exports of the security log are logged. Database access to the log tables is limited to `<DBA role>` (A.8.18).
- Forward Workbench security events to the SIEM `<daily CSV export / database read replica / API>` so a database
  administrator can't silently alter the only copy.
- Clocks: hosts use NTP (`<source>`); Workbench records UTC (A.8.17, NIST 3.3.7).

## 3. Monitoring and review (A.8.16, NIST 3.3.3, 3.3.5, 3.14.7)

| What | How | Frequency | Who |
| --- | --- | --- | --- |
| Failed sign-ins, `signin.mfa_required`, `session.rejected` bursts | SIEM alert `<threshold>` or **Security events** filter | Daily / alert | `<on-call>` |
| `access.denied`, `access.rate_limited` spikes | SIEM alert | Alert | `<on-call>` |
| Role changes, `settings.sso`, `settings.changed`, new admins | Review list | Weekly | `<ISMS manager>` |
| Sensitive reveals (`payroll.pay_rate_revealed`, `bank.account_number_revealed`, `vendor.tax_id_revealed`) and `export.*` | Review list | Monthly | `<Controller>` |
| Health and availability (`/health`, OTLP metrics) | Monitoring with paging | Continuous | Operations |
| Access reviews | **Users and security › Access reviews** | Quarterly | System owners |

Record each review (date, reviewer, findings) in `<ticket / review log>`; findings that are events go to the incident
process ([incident-response-plan.md](incident-response-plan.md)).

## 4. Logging failures (NIST 3.3.4)

Workbench logs an error when it can't write a security event (the action itself proceeds). Alert on that log message
(`Couldn't write security event`) and on log pipeline gaps.

## Approval

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<role>` | | |
