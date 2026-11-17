# Service levels (draft)

!!! danger "DRAFT: have a lawyer review before use"
    Not legal advice, and **not a promise yet**. Every number is a placeholder `[?]` the owner must decide **only after
    checking what the team and the infrastructure can actually deliver**. A service level you cannot meet is a liability.
    This schedule applies to the **Hosted** service. Self-Hosted customers get the support terms in Section 3 only; uptime is
    theirs to run.

## 1. Availability

| | Target |
|---|---|
| Monthly uptime of the Hosted service (the web application and API for a customer's tenant) | `[99.5%]` per calendar month (placeholder: 99.5% allows about 3.6 hours down per month; 99.9% about 43 minutes) |
| Measured by | Vendor's external health check against the tenant's `/health/ready` endpoint every `[1]` minute `[confirm the monitoring setup]` |
| Counts as downtime | Minutes in which the check fails for `[?]` consecutive minutes |
| Does not count | Scheduled maintenance announced `[5]` business days ahead and held in `[window]` (limit `[?]` hours per month); emergency security fixes with notice as soon as possible; outages caused by Customer, its network, its identity provider or its integrations; force majeure; trials and free Core; beta features |
| Recovery targets | Recovery time objective `[?]` hours and recovery point objective `[?]` minutes. These are [backup targets](../operations/backup-restore.md); the deployment guide says what each topology reaches, and what is promised here must not exceed it |

## 2. Service credits

If monthly uptime falls below the target, Customer may claim a credit against the next invoice, as a percentage of that
month's Hosted fees:

| Monthly uptime | Credit |
|---|---|
| below `[99.5%]`, at or above `[99.0%]` | `[10%]` |
| below `[99.0%]`, at or above `[95.0%]` | `[25%]` |
| below `[95.0%]` | `[50%]` |

Credits must be claimed within `[30]` days of the end of the month, are capped at `[?]%` of the month's fees, are not paid in cash,
and are Customer's **only** remedy for downtime, except that persistent failure (below `[95%]` in `[3]` months out of `[12]`) allows
termination with a pro-rata refund.

## 3. Support

| | |
|---|---|
| Hours | `[Monday to Friday, 8:00 to 18:00 US Eastern, excluding US holidays]` (placeholder; the owner must choose a time zone and a window the team can really staff). Severity 1 `[24x7 / on-call]` or business hours only: **decide** |
| Channels | `[email address]`, `[web form / in-app Help > Contact support]` `[phone: Y/N]` |
| Who may raise tickets | Named contacts: `[2]` per `[25]` seats `[?]` |
| Language | English |
| Versions supported | The current release and the previous `[one]` |
| Included | Questions, defects, security fixes, upgrade help, guidance on configuration. Excluded: training, custom development, customer's own network, identity provider and integrations, data corrections `[statement of work]` |

### Severity levels and response targets

| Severity | Meaning | First response | Updates | Target for workaround or fix |
|---|---|---|---|---|
| **1: Critical** | Service down for all users, data loss or corruption, or a security incident in progress | `[1]` hour `[24x7?]` | every `[2]` hours | `[4]` hours to restore or work around |
| **2: High** | A core process is blocked for many people (timesheet submission, approvals, billing run, period close) with no workaround | `[4]` business hours | every business day | `[2]` business days |
| **3: Medium** | A feature misbehaves; a workaround exists | `[1]` business day | weekly | next release or `[10]` business days |
| **4: Low** | Question, cosmetic issue, enhancement request | `[2]` business days | as needed | considered for the roadmap |

Response times are targets for first human reply, not a promise of resolution. Severity is agreed with Customer; Vendor may
re-grade with reasons. Customer must give enough detail to reproduce (steps, time, user, screenshots, correlation ID from the
error page).

## 4. Maintenance and communication

- A status page at `[URL]` shows incidents and planned maintenance. `[Decide whether to run one; it is expected.]`
- Releases are applied with a database migration step first; Vendor aims for `[no more than N minutes]` of interruption.
- Security fixes follow the timelines in `SECURITY.md` (Critical `[14]` days, High `[30]` days).
- Customer is told of a confirmed breach under the [data processing addendum](data-processing-addendum-draft.md).

## 5. Customer responsibilities

Keep contact details current; keep its identity-provider settings valid; do not run load tests without notice; keep exports
and local records as its own compliance rules require; report problems promptly.

!!! info "Before publishing, check these are true"
    - A health check and alerting actually exist ([Monitoring](../operations/monitoring.md), [Alerts](../operations/alerts.md)).
    - Someone is on call for any severity-1 target that is promised outside business hours.
    - Backups have been restored, and the restore time measured against the RTO above.
    - The uptime number is lower than what the last quarter of monitoring shows.
