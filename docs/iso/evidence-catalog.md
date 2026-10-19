# Evidence catalog

What Workbench records that an ISO/IEC 27001 auditor (or a CMMC assessor, or DCAA) can sample, where to find it, and
how to export it. Pair it with the [Statement of Applicability](statement-of-applicability.md): each "Workbench" row there
points at one of the sources below. Evidence of *operation* (that people actually do the reviews) is what Stage 2
audits sample, so export and file it on the schedule in the last column.

## Sources

| # | Evidence source | Where in Workbench | Export | Integrity |
| --- | --- | --- | --- | --- |
| E1 | **Security event log** | Admin › Users and security › Security events | CSV (logged as `export.security_events`) | Append-only; retention-limited (`Retention:SecurityEventDays`) |
| E2 | **Audit log** (financial and setup changes) | Admin › Audit log (`/setup/audit`) | Viewer; report writer | Append-only, never purged |
| E3 | **Access reviews** | Users and security › Access reviews | CSV per review | Completed reviews are immutable |
| E4 | **Users and roles** | Users and security › Users | Screenshot / API `GET /api/v1/admin/users` | Changes in E1 |
| E5 | **Invites** | Users and security › Invites | Screen / API | Changes in E1 |
| E6 | **Sign-in settings** (IdP, MFA, provisioning, sessions) | Users and security › Sign-in | Screen; every change in E1 as `settings.sso` | Secret never shown |
| E7 | **Retention and legal holds** | Users and security › Retention | Run reports | Runs in E1 (`retention.run`) |
| E8 | **Timesheet history and revisions** | Time › history; Timesheet compliance report | Report export (.xlsx/.csv) | Revisions append-only with reasons |
| E9 | **Period close** | Finance › Period close | Screen; `period.status_changed` in E1 | Closed periods locked |
| E10 | **DCAA readiness checklist** | Admin › Compliance › DCAA readiness | Screen; answers in E1 as `compliance.checklist` | Answers kept as history |
| E11 | **SBOM and CI results** | CI artifacts (CycloneDX SBOM, vulnerable-package check, gitleaks, tests) | CI | Build provenance |
| E12 | **Configuration** | `docs/security/configuration.md`; startup check log | Deployment records | Production refuses weak config |
| E13 | **Host audit log** (SaaS) | `/host` (host admins) | Screen | Catalog database |

## Security event types (E1)

| Event | Meaning | Controls |
| --- | --- | --- |
| `signin.success` (detail: `sso`/`oidc`, `amr=`, `acr=`) | Interactive sign-in and how the IdP authenticated the person | A.8.5, A.5.17 · 3.5.1–3.5.3 |
| `signin.failure` | Refused sign-in (not invited, deactivated, org IdP required, re-auth not performed) | A.8.5, A.5.16 · 3.1.8 |
| `signin.mfa_required` | Refused because MFA wasn't reported (with what was) | A.8.5 · 3.5.3 |
| `signin.reauthenticated` | Fresh sign-in for a sensitive action | A.8.5 · 3.5.x |
| `signin.dev` | Development-only sign-in (never in production) | A.8.31 |
| `signout`, `session.rejected` | Sign-out; expired, revoked or over-limit sessions | A.8.5 · 3.1.10, 3.1.11 |
| `access.reauth_required` | Sensitive action refused for an old sign-in | A.8.5, A.8.2 |
| `access.denied`, `access.rate_limited` | Permission denials (incl. HTTP 403) and throttling | A.8.3, A.8.20 · 3.1.1, 3.1.2 |
| `user.created` (incl. "Just-in-time"), `user.roles_changed` (actor `sso` when from IdP groups), `user.deactivated`, `user.reactivated`, `user.sessions_revoked`, `user.employee_link` | Identity lifecycle | A.5.16, A.5.18, A.6.5, A.8.2 · 3.1.1, 3.1.5, 3.5.5, 3.9.2 |
| `invite.created`, `invite.accepted`, `invite.revoked` | Access requests and approvals | A.5.18 |
| `scim.request` | Provisioning/deprovisioning by the IdP | A.5.16 · 3.9.2 |
| `access_review.started`, `access_review.decision`, `access_review.completed` | Periodic access review | A.5.18 · 3.1.1 |
| `export.*`, `payroll.pay_rate_revealed`, `bank.account_number_revealed`, `vendor.tax_id_revealed` | Sensitive data leaving the masked view | A.8.12, A.5.34 · 3.1.3 |
| `settings.sso`, `settings.changed` | Security configuration changes | A.8.9, A.8.32 · 3.4.3 |
| `period.status_changed` | Fiscal period opened/closed | DCAA, A.5.33 |
| `retention.run`, `legal_hold.placed`, `legal_hold.released` | Records retention | A.5.33, A.8.10 |
| `compliance.checklist` | DCAA checklist answers | A.5.36 |

## What to file, and when

| Control(s) | Evidence to keep | Frequency |
| --- | --- | --- |
| A.5.18, A.8.2 · 3.1.1, 3.1.5 | Completed access review CSV, signed off | Quarterly |
| A.8.15, A.8.16 · 3.3.3 | Security event CSV for the period + your review notes | Monthly |
| A.8.5 · 3.5.3 | Screenshot of **Sign-in** (MFA: Everyone/Admins), sample of `signin.success` with `amr=...mfa` | On change + yearly |
| A.5.16, A.6.5 · 3.9.2 | Leavers list from HR vs `user.deactivated` / `scim.request` events | Monthly |
| A.8.32 · 3.4.3 | `settings.sso`/`settings.changed` events vs change tickets | Monthly |
| A.5.33, A.8.10 | Retention run report, legal holds list | Quarterly |
| A.8.8, A.8.25–A.8.29 | CI run with vulnerable-package check and SBOM for each release | Each release |
| DCAA | DCAA readiness checklist with notes and CPA sign-off; timesheet compliance report | Before a pre-award survey, then yearly |
