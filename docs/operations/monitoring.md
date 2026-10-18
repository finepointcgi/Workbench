# Monitoring

ISO 27001 A.8.15 (logging) and A.8.16 (monitoring activities). Workbench emits OpenTelemetry traces, metrics and
logs over OTLP, writes structured JSON logs to stdout, and exposes liveness and readiness endpoints. Point it at an
OpenTelemetry Collector (or any OTLP backend: Grafana Cloud, Azure Monitor, AWS ADOT/CloudWatch, Datadog, Honeycomb)
and build the alerts in [alerts.md](alerts.md).

## Turning it on

```
OpenTelemetry__Endpoint=http://otel-collector:4317     # or OTEL_EXPORTER_OTLP_ENDPOINT
OpenTelemetry__Protocol=grpc                           # or http/protobuf (then usually port 4318)
OpenTelemetry__Headers=x-api-key=<secret>              # if the backend needs one
OpenTelemetry__ServiceName=workbench-web
```

Without an endpoint nothing is exported (console logs still are). Resource attributes: `service.name`,
`service.version`, `deployment.environment.name`.

## Health endpoints

Anonymous, not rate limited, and excluded from traces.

| Endpoint | Meaning | Use for |
| --- | --- | --- |
| `GET /health/live` | The process is up (`Healthy`, 200). No dependencies checked. | Liveness probe, container `HEALTHCHECK` (`--health-check`) |
| `GET /health/ready` | JSON: `{"status":"Healthy","checks":{"database":"Healthy","catalog":"Healthy","email":"Healthy"}}`. 503 when a required check fails; 200 when only *Degraded*. | Readiness probe, load balancer health check, uptime monitor |

- `database`: the tenant database answers (on the MultiTenant base domain: the catalog).
- `catalog`: MultiTenant only, the catalog answers (tenant resolution needs it on every request); always healthy in SingleTenant.
- `email`: never makes the instance unready (a mail outage mustn't take the app out of rotation) but reports
  **Degraded** when the SMTP relay refuses TCP connections (probed at most once a minute), when the last delivery
  failed within 15 minutes with no success since, or when Production writes mail to the pickup folder.

Only statuses are returned, never descriptions or exceptions (they could reveal host names). Descriptions are in
the logs (`Health check email with status Degraded ...`).

## Metrics

Besides the standard ASP.NET Core (`http.server.request.duration`, `http.server.active_requests`, Kestrel, SignalR
connections, Blazor circuits), HttpClient, .NET runtime (`process.runtime.dotnet.gc.*`, thread pool, exceptions) and
Npgsql (`db.client.connections.usage`, `db.client.operation.duration`) instruments, Workbench publishes on the meter
`Workbench`:

| Instrument | Type | Tags | Meaning |
| --- | --- | --- | --- |
| `workbench.jobs.runs` | counter | `job` (the background service, e.g. `RetentionBackgroundService`), `outcome` (`success`, `failure`, `cancelled`) | One per job run, per tenant in MultiTenant mode |
| `workbench.jobs.duration` | histogram (s) | `job` | Run time |
| `workbench.security.events` | counter | `event.type` (`signin.failure`, `access.denied`, `export.*`...), `outcome` (`success`, `failure`, `denied`) | Every security event recorded |
| `workbench.email.sent` | counter | `email.provider` (`smtp`, `sendgrid`, `ses`, `pickup`), `outcome` | Each email handed to the provider |

Tags never carry people, emails, tenants' data, amounts or secrets. (Per-tenant breakdowns come from logs, which carry
the tenant slug.)

## Traces

Spans for incoming requests (query strings removed: they can carry tokens; `/health` and `/_framework` skipped),
outgoing HTTP calls (query strings removed), PostgreSQL commands (Npgsql: the SQL text with parameter placeholders,
never parameter values) and one span per background job run (`job <name>`, error status on failure). Blazor Server
interactions after the first request travel over the circuit's WebSocket, so they appear as database spans without
a parent request.

## Logs

Outside Development the console log is **one JSON object per line** (`Logging:Console:FormatterName=workbench-json`):

```json
{"Timestamp":"2026-10-18T22:50:33.10+00:00","Level":"Warning","Category":"Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService","EventId":103,"Message":"Health check email with status Degraded ...","TraceId":"cb1a…","SpanId":"a3b3…","Tenant":"acme","State":{"HealthCheckName":"email","HealthStatus":"Degraded"}}
```

`TraceId`/`SpanId` join a log line to its trace; `Tenant` is the tenant slug in MultiTenant mode. The same records go
to the OTLP log exporter when telemetry is on. Ship stdout to your log store (the container runtime, Fluent Bit, the
Collector's filelog receiver) and keep it at least a year (A.8.15); restrict who can read it.

**No personal data or secrets.** Code never logs secrets, tokens, passwords or cost/bill rates (reviewed against
`docs/iso/secure-development-policy.md`), EF Core parameter values are never logged, and as a safety net both the
JSON console formatter and the OTLP exporter redact:

- email addresses (`[email]`) in messages, exceptions and values;
- `Bearer`/`Basic` credentials and `Password=`, `Pwd=`, `AccountKey=`, `ApiKey=`, `Secret=` in text (connection strings);
- the whole value of any property whose name says it is secret: `password`, `secret`, `token`, `apikey`,
  `authorization`, `cookie`, `connectionstring`, `tin`, `ssn`, `bankaccount`, `accountnumber`, `routingnumber`,
  `privatekey`.

Who did what is in the **security event log** (in the database, Users and security → Security events), which keeps
actors and is retained per `Retention:SecurityEventDays`; the operational logs deliberately don't duplicate it.

## A Collector to start from

```yaml
receivers:
  otlp:
    protocols: { grpc: { endpoint: 0.0.0.0:4317 }, http: { endpoint: 0.0.0.0:4318 } }
processors:
  batch: {}
  memory_limiter: { check_interval: 1s, limit_percentage: 75, spike_limit_percentage: 20 }
exporters:
  otlphttp/backend: { endpoint: https://otlp.example-backend.com, headers: { authorization: "${env:BACKEND_TOKEN}" } }
service:
  pipelines:
    traces:  { receivers: [otlp], processors: [memory_limiter, batch], exporters: [otlphttp/backend] }
    metrics: { receivers: [otlp], processors: [memory_limiter, batch], exporters: [otlphttp/backend] }
    logs:    { receivers: [otlp], processors: [memory_limiter, batch], exporters: [otlphttp/backend] }
```

Also monitor from outside: an uptime check on `https://<host>/health/ready` from another region, TLS certificate
expiry, and the database (CPU, connections, storage, replication lag, backup status) with your provider's tools.
