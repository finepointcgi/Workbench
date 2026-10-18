# Business continuity and disaster recovery plan

| | |
| --- | --- |
| Owner | `<Operations lead>` |
| Approved by | `<top management>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.5.29, A.5.30, A.8.14, A.8.13 |
| Last test | `<date, type>` |

## 1. Critical activities and targets

*Business impact analysis: what must keep working, and how long can it stop?*

| Activity (in Workbench) | Peak need | Max tolerable outage | RTO | RPO | Manual workaround |
| --- | --- | --- | --- | --- | --- |
| Daily time entry (DCAA daily recording) | Every working day | `<1 day>` | `<8 h>` | `<4 h>` | Paper/spreadsheet timesheets signed daily, entered later with reason |
| Timesheet approval and payroll export | Period end | `<2 days>` | `<8 h>` | `<4 h>` | Approve from exported records |
| Billing / financial reports | Month end | `<3 days>` | `<24 h>` | `<4 h>` | Delay invoices |
| ODC/expense approval | Weekly | `<5 days>` | `<24 h>` | `<24 h>` | Defer |
| Project collaboration (sheets, boards) | Daily | `<3 days>` | `<24 h>` | `<4 h>` | Email/files |

## 2. Scenarios

| Scenario | Response |
| --- | --- |
| Web host down (app crash, bad deployment) | Roll back to the previous release; `/health/live` and `/health/ready` show recovery |
| Database lost or corrupted | Restore per `../operations/backup-restore.md`; PITR where available |
| Region / data center outage | Fail over to `<second region / site>`: deploy from CI artifacts, restore latest replicated backup, switch DNS |
| Identity provider outage | Users can't sign in; existing sessions continue up to their timeouts. Break-glass: `<procedure, or accept outage>` |
| Ransomware | Incident response plan 6.5; restore from immutable backup |
| Loss of key staff | Documented procedures (`../operations`, `../security`), at least two trained admins |
| Supplier failure | Exit plan per supplier checklist |

## 3. Keeping security during disruption (A.5.29)

- Access control, logging and encryption stay on in the recovery environment; recovery uses the same configuration
  (the startup check refuses weak configuration).
- Emergency access is logged and reviewed afterwards; temporary exceptions expire with the incident.
- Manual workarounds that create records (paper timesheets) are kept and entered with reasons.

## 4. Recovery procedure (summary)

1. Incident lead declares a continuity event and the scenario.
2. Operations provisions the target environment from infrastructure code `<repo>`.
3. Restore the database and blob store; restore secrets from the vault; deploy the last good release.
4. Verify `/health/ready`, admin sign-in, a sample timesheet and project financials.
5. Switch traffic (DNS / load balancer). Tell users.
6. After recovery: review data lost against RPO, re-enter manual records, post-incident review.

## 5. Contacts

See `incident-response-plan.md` section 3, plus hosting provider `<>`, DNS provider `<>`.

## 6. Testing

| Test | Frequency | Last done | Result |
| --- | --- | --- | --- |
| Backup restore test | Quarterly | `<>` | |
| Tabletop exercise (scenario walk-through) | Yearly | `<>` | |
| Failover to second region/site | Yearly | `<>` | |
