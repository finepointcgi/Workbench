# Alerts and runbook

What to alert on and what to do when each alert fires (ISO 27001 A.8.16). Expressions are PromQL against the OTLP
metrics as a Prometheus-compatible backend names them (dots become underscores, counters gain `_total`, histograms
`_bucket`, seconds-based histograms keep `_seconds`); translate to your backend's query language as needed. Thresholds
are starting points: tune them after a few weeks of real traffic.

Severity: **page** = someone acts now, 24/7 for the SaaS; **ticket** = next business day. Every page that turns out to
be a security event becomes an incident ([Incident response: technical steps](incident-response.md)).

| Alert | Condition | Severity |
| --- | --- | --- |
| Service down | External uptime check of `/health/ready` fails from 2 locations for 3 minutes, or no ready replica | page |
| Not ready | Any replica's `/health/ready` returns 503 for 5 minutes | page |
| Server errors | 5xx > 2% of requests for 10 minutes | page |
| Latency | p95 request duration > 2 s for 15 minutes (excluding `/_blazor` and SignalR) | ticket (page at > 5 s) |
| Background job failures | Any job's failures in 1 hour > 0 twice in a row, or a job with no successful run in 26 hours | ticket (page for payroll/billing windows) |
| Failed sign-ins | `signin.failure` > 20 in 5 minutes overall, or > 10 for one account in the security log | page (possible credential attack) |
| Denials / rate limiting | `access.denied` or `access.rate_limited` > 5× the 7-day baseline for 15 minutes | ticket |
| Backup failed | The backup job exits non-zero, its dead-man switch misses a check-in, or the newest complete run is older than schedule + 1 h | page |
| Off-site copy failed | The sync of `BACKUP_DIR` to immutable storage fails or is older than 6 hours | ticket |
| Restore test overdue / failed | No passing restore test in 92 days, or a failure | ticket |
| Email degraded | `email` check Degraded for 30 minutes, or email failures > 10% for 30 minutes | ticket |
| Database | Connections > 80% of `max_connections`; storage > 80%; replication lag > 60 s; PITR/automated backup failed | ticket (page for storage > 90%) |
| Certificates | TLS certificate expires in < 14 days; key-encryption certificate expires in < 60 days | ticket |
| Error logs | Error-level log rate > 5× the 7-day baseline for 15 minutes | ticket |

## Expressions

```promql
# Server errors (ratio)
sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[10m]))
  / sum(rate(http_server_request_duration_seconds_count[10m])) > 0.02

# Latency p95
histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{http_route!~"/_blazor.*|/hubs/.*"}[15m]))) > 2

# Background job failures, by job
sum by (job) (increase(workbench_jobs_runs_total{outcome="failure"}[1h])) > 0
# ...and jobs that stopped succeeding (daily jobs: retention, digests, reminders)
sum by (job) (increase(workbench_jobs_runs_total{outcome="success"}[26h])) == 0

# Failed sign-ins
sum(increase(workbench_security_events_total{event_type="signin.failure"}[5m])) > 20

# Denials against baseline
sum(rate(workbench_security_events_total{event_type=~"access.denied|access.rate_limited"}[15m]))
  > 5 * sum(rate(workbench_security_events_total{event_type=~"access.denied|access.rate_limited"}[7d]))

# Email failure ratio
sum(rate(workbench_email_sent_total{outcome="failure"}[30m])) / sum(rate(workbench_email_sent_total[30m])) > 0.1

# Database pool pressure (Npgsql)
sum(db_client_connections_usage{state="used"}) / sum(db_client_connections_max) > 0.8
```

(The OTel attribute `event.type` becomes the label `event_type`.)

## Runbook

### Service down / not ready

1. Check `/health/ready` on each replica (`kubectl exec`/`docker compose exec web dotnet /app/Workbench.Web.dll
   --health-check http://127.0.0.1:8080/health/ready` returns 1 when not ready) and read which check fails in the logs
   (`Health check database with status Unhealthy`).
2. `database`/`catalog` unhealthy: is PostgreSQL up, reachable, accepting connections (`max_connections`), with free
   storage? Managed service status page? Failover in progress? Fix the database; the app recovers by itself.
3. Replicas crash-looping right after a deploy: read the first log lines. A startup-check failure lists every
   configuration problem ("refuses to start in Production with a weak configuration") — fix the configuration or roll
   back ([Runbooks](runbooks.md#roll-back)). Migration not run: run the migrate job.
4. Proxy: certificate valid? Proxy healthy? Its upstream health check (`/health/live`)?

### Server errors / latency

1. Group the failing requests by route in traces; open a failing trace and follow it to the exception (log lines
   share the `TraceId`).
2. Slow database spans: look for a missing index or a large report; check database CPU and locks
   (`pg_stat_activity`). Many concurrent long reports: consider a read replica for reporting.
3. A bad deploy: roll back.

### Background job failures

1. Find the job (`job` tag) and its error logs (`Category` is the background service, or
   `Workbench.Infrastructure.Tenancy.MultiTenantJobRunner` with the tenant slug). MultiTenant: is it one tenant (its
   data or its database) or all?
2. Jobs are idempotent (notifications dedupe, retention and digests re-run safely): after fixing the cause, the next
   scheduled run catches up. Retention can be run on demand from Users and security → Retention.
3. A job with no successful runs at all: the host may not be running it (`Retention:NightlyJobEnabled=false`?) or it
   is stuck; check for errors at startup.

### Failed sign-ins

1. Open **Users and security → Security events**, filter `signin.failure`: one account (targeted guessing) or many
   (spraying)? Which IPs?
2. Sign-in is through your identity provider, so lockout and MFA are enforced there: check the IdP's sign-in logs and
   risk detections; block the IPs at the proxy/WAF if it's an attack.
3. Successful sign-ins following the failures from the same IP: treat as account compromise → incident runbook 6.1 in
   `docs/iso/incident-response-plan.md` (deactivate the user, which ends their sessions at once).

### Backup failed

1. Read the job's output: connection/authentication (pgpass, role), disk full in `BACKUP_DIR`, gpg key missing or
   expired, a tenant database missing (renamed/deleted tenant).
2. Fix and run the job again by hand; confirm `COMPLETE` and `SHA256SUMS` in the new run folder and the off-site copy.
3. If backups were missing for longer than the RPO, record it (it is a nonconformity against the backup policy) and
   check that PITR covered the gap.

### Email degraded

1. Logs: `SendGrid refused the message: 401` (key revoked or wrong), SMTP connect/TLS errors, SES
   `MessageRejected` (sender not verified / account in sandbox / suppressed recipient).
2. Notifications are still saved and shown in the app; emails that failed are not retried automatically, so tell users
   if an outage lasted long (approvals may be waiting).
3. See [Email](email.md) for bounces and suppression.

### Certificates

- TLS: Caddy/cert-manager renew automatically; an expiring certificate means renewal is failing (DNS challenge
  credentials, rate limits). Check the proxy's logs.
- Key-encryption certificate: rotate it ([Data Protection keys](data-protection-keys.md#rotation)). Expiry doesn't
  break decryption, but rotate on schedule.
