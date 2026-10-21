# Configuration reference

Every setting the web host reads, with its default. Set them as environment variables (`Section__Key`, arrays as
`Section__Key__0`), as files in `SecretsDirectory` (secrets), or in `appsettings.json` (non-secrets only).
**Secret** marks values that must come from a vault, never from `appsettings*.json` or the image.

Organization settings an administrator changes in the app (timesheet cadence, retention overrides, leave policies,
the chart of accounts...) live in the tenant database, not here. Settings are server-wide: every tenant of a
MultiTenant deployment shares them.

## Hosting

| Key | Default | Notes |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` (image) | `Development` turns off the startup check and enables the dev sign-in, seeding and auto-migration. Never in production. |
| `ASPNETCORE_HTTP_PORTS` | `8080` (image) | Port Kestrel listens on (plain HTTP behind the proxy). |
| `ASPNETCORE_URLS`, `Kestrel:Endpoints:*` | | Only if Kestrel terminates TLS itself (then configure a certificate). |
| `AllowedHosts` | `*` | Restrict to your host names (`workbench.example.com;*.workbench.example.com`) for defense in depth. |
| `SecretsDirectory` | `/run/secrets` (image) | Folder with one file per configuration key (`ConnectionStrings__Workbench`). Files with a `.` in the name are ignored. |
| `Security:TlsTerminatedByProxy` | `false` (`true` in the image) | Behind a TLS-terminating proxy: honour `X-Forwarded-For/Proto/Host` from trusted proxies. |
| `ForwardedHeaders:KnownProxies` | `[]` | IP addresses of the proxies whose forwarded headers are trusted. |
| `ForwardedHeaders:KnownNetworks` | `[]` | CIDR ranges of trusted proxies (a container network, an ingress pod range). |
| `Security:HstsMaxAgeDays` | `365` | HSTS max-age, sent outside Development. |
| `Security:ConnectSources` | `[]` | Extra origins for the CSP `connect-src`. |
| `Storage:Root` | `App_Data/blobs` (`/data/blobs` in the image) | Attachments and receipts. Shared storage when running several replicas. |

## Database and tenancy

| Key | Default | Notes |
| --- | --- | --- |
| `Database:Provider` | `PostgreSQL` | `PostgreSQL` or `Sqlite` (evaluation only). |
| `ConnectionStrings:Workbench` | — | **Secret.** SingleTenant database, e.g. `Host=db;Database=workbench;Username=workbench;Password=…;SSL Mode=VerifyFull;Maximum Pool Size=50`. |
| `Database:MigrateOnStartup` | `false` (`true` in Development) | Leave off; deploys run `--migrate`. |
| `Database:AllowSqliteInProduction` | `false` | SQLite in Production only with this (single-user evaluation). |
| `Tenancy:Mode` | `SingleTenant` | `MultiTenant` for the SaaS: a database per tenant plus the catalog. |
| `ConnectionStrings:Catalog` | — | **Secret.** MultiTenant: the catalog database. |
| `Tenancy:TenantConnectionString` | — | **Secret.** MultiTenant: template for new tenant databases, with `{tenant}` (e.g. `Database=workbench_{tenant}`). The login needs `CREATEDB`. |
| `Tenancy:BaseDomain` | `localhost` | MultiTenant: tenants are its subdomains; the base domain is the host admin site. Must not be `localhost` outside Development. |
| `Tenancy:HostAdmins` | `[]` | MultiTenant: emails of the people who create and suspend tenants. Required outside Development. |
| `Tenancy:FiscalYearStartMonth` | `10` | First fiscal month seeded for a new tenant. |
| `Tenancy:AllowTenantHeader` | `true` | `X-Tenant` header; honoured only in Development. |
| `Tenancy:DevelopmentTenants` | `[]` | Development only: tenants created with sample data. |

## Authentication and access

| Key | Default | Notes |
| --- | --- | --- |
| `Authentication:Oidc:Authority` | — | Issuer URL. Required in MultiTenant mode, and when built-in accounts are off; otherwise optional. |
| `Authentication:Oidc:ClientId` | — | |
| `Authentication:Oidc:ClientSecret` | — | **Secret.** |
| `Authentication:Oidc:Audience` | — | API audience for bearer tokens (desktop app). |
| `Authentication:Oidc:Scopes` | `[]` | Extra scopes beyond `openid profile email`. |
| `Authentication:Oidc:RequireHttpsMetadata` | `true` | Must stay `true` outside Development. |
| `Authentication:Admins` | `[]` | SingleTenant: bootstrap administrators (emails). Ignored in MultiTenant. Without SSO they set a first password with **Forgot your password?**. |
| `Authentication:Passwords:Enabled` | `true` | Built-in accounts: email and password sign-in with an authenticator app ([built-in-accounts.md](../security/built-in-accounts.md)). |
| `Authentication:Passwords:MinimumLength` | `12` | Also refused: common passwords, ones containing the email, runs like `123456789012`. |
| `Authentication:Passwords:MaxFailedAttempts` | `5` | Wrong passwords or codes in a row before the account locks. |
| `Authentication:Passwords:LockoutDuration` | `00:15:00` | |
| `Authentication:Passwords:ResetLinkLifetime` | `01:00:00` | Forgot-password links. Invite links last as long as the invite (7 days). |
| `Authentication:RequireInvite` | `true` | Only invited or provisioned people may sign in. Must stay `true` in MultiTenant. |
| `Authentication:Session:IdleTimeoutMinutes` | `30` | |
| `Authentication:Session:AbsoluteLifetimeHours` | `12` | |
| `Authentication:Session:CircuitRevalidationSeconds` | `60` | |
| `Scim:Enabled` | `true` | SCIM 2.0 provisioning at `/scim/v2` (SingleTenant only). |
| `Scim:BearerToken` | — | **Secret.** 32+ characters when SCIM is on. |
| `RateLimiting:Enabled` | `true` | |
| `RateLimiting:SignInPerMinute` | `20` | Per IP. |
| `RateLimiting:ScimPerMinute` | `300` | Per IP. |
| `RateLimiting:ApiPerMinute` | `600` | Per user or IP. |

Single sign-on and MFA settings added later are documented in `docs/security/configuration.md`.

## Data Protection (key ring encryption)

Details and procedures: [data-protection-keys.md](data-protection-keys.md).

| Key | Default | Notes |
| --- | --- | --- |
| `DataProtection:Store` | `Database` | `Database` (the tenant database, or the catalog in MultiTenant), `FileSystem` or `Default`. Production refuses `Default`. |
| `DataProtection:KeysFolder` | — | For `FileSystem`. |
| `DataProtection:KeyEncryption` | inferred | `Certificate`, `AzureKeyVault`, `AwsKms` or `None`. Empty: `Certificate` when a certificate is set, else `None`. **Production refuses `None`.** |
| `DataProtection:CertificatePath` | — | PFX file (with private key) that encrypts the keys. |
| `DataProtection:CertificatePassword` | — | **Secret.** |
| `DataProtection:CertificateThumbprint` | — | Alternatively, a certificate in the `My` store (current user, then local machine). |
| `DataProtection:PreviousCertificates:N:Path` / `:Password` / `:Thumbprint` | `[]` | Old certificates still needed to decrypt older keys (rotation). Never used to encrypt. |
| `DataProtection:AzureKeyVault:KeyId` | — | `https://<vault>.vault.azure.net/keys/<name>[/<version>]`. |
| `DataProtection:AzureKeyVault:ClientId` | — | User-assigned managed identity, or with `ClientSecret`+`TenantId` a service principal. Empty: `DefaultAzureCredential`. |
| `DataProtection:AzureKeyVault:TenantId` | — | |
| `DataProtection:AzureKeyVault:ClientSecret` | — | **Secret.** Prefer a managed or workload identity. |
| `DataProtection:AwsKms:KeyId` | — | Key ARN or alias ARN. Credentials from the default chain (IAM role). |
| `DataProtection:AwsKms:Region` | SDK default | E.g. `us-gov-west-1`. |
| `DataProtection:KeyLifetimeDays` | `90` | How long a key protects new data before the next is created (minimum 7). |
| `DataProtection:ApplicationName` | `Workbench` | Never change it on a running system: existing protected data would become unreadable. |

## Email

Details: [email.md](email.md).

| Key | Default | Notes |
| --- | --- | --- |
| `Email:Mode` | `Smtp` if `Email:Smtp:Host` is set, else `Pickup` | `Smtp`, `SendGrid`, `Ses` or `Pickup` (writes `.eml` files; development). Pickup in Production marks readiness *Degraded*. |
| `Email:From` | `workbench@localhost` | A verified sender on your domain. |
| `Email:FromName` | `Workbench` | |
| `Email:ReplyTo` | — | A monitored mailbox, if replies should go somewhere. |
| `Email:PickupFolder` | `App_Data/mail` | Pickup mode. |
| `Email:Smtp:Host` / `:Port` | — / `25` | Use 587 (STARTTLS) or 465 (implicit TLS). |
| `Email:Smtp:Security` | `Auto` | `Auto` (465 → implicit TLS, otherwise STARTTLS required), `StartTls`, `SslOnConnect`, `None`. **Production refuses `None`.** |
| `Email:Smtp:EnableSsl` | `true` | Legacy switch; `false` means `None` when `Security` isn't set. |
| `Email:Smtp:UserName` / `:Password` | — | Password is a **secret**. |
| `Email:SendGrid:ApiKey` | — | **Secret.** A key with only *Mail Send* permission. |
| `Email:Ses:Region` | SDK default | |
| `Email:Ses:ConfigurationSet` | — | Publishes bounce/complaint events (see [email.md](email.md)). |
| `Email:Ses:AccessKeyId` / `:SecretAccessKey` | — | **Secret.** Prefer an IAM role. |
| `Notifications:PublicBaseUrl` | — | Absolute base for links in emails; may contain `{tenant}` (`https://{tenant}.workbench.example.com`). |
| `Notifications:DigestHourLocal` | `7` | Hour the daily digest goes out. |

## Telemetry and logging

Details: [monitoring.md](monitoring.md).

| Key | Default | Notes |
| --- | --- | --- |
| `OpenTelemetry:Endpoint` | — (off) | OTLP endpoint, e.g. `http://otel-collector:4317`. `OTEL_EXPORTER_OTLP_ENDPOINT` also works. Turns on traces, metrics and logs. |
| `OpenTelemetry:Protocol` | `grpc` | `grpc` or `http/protobuf`. |
| `OpenTelemetry:Headers` | — | **Secret** when it carries an API key: `key=value,key2=value2`. |
| `OpenTelemetry:ServiceName` | `workbench-web` | |
| `Logging:Console:FormatterName` | `workbench-json` outside Development | One redacted JSON object per line. `simple` for humans. |
| `Logging:LogLevel:Default` | `Information` | `Microsoft.AspNetCore` and EF Core commands are `Warning`. |

## Retention

| Key | Default | Notes |
| --- | --- | --- |
| `Retention:SecurityEventDays` | `365` | |
| `Retention:NotificationDays` | `180` | |
| `Retention:ExpiredInviteDays` | `90` | |
| `Retention:ProtectedRecordDays` | `1095` | Reported only; time, ODC, journal and audit records are never auto-deleted. |
| `Retention:RunHourUtc` | `3` | |
| `Retention:NightlyJobEnabled` | `true` | |

## Business rules (server-wide defaults)

| Key | Default | Notes |
| --- | --- | --- |
| `Timekeeping:Cadence` / `:WeekStartDay` / `:BiweeklyAnchor` | `Weekly` / `Monday` / — | Used until an Admin saves the organization's period on `/setup/organization`. |
| `Timekeeping:TimeZone` | server's zone | IANA id deciding "today" for timesheets. Containers run in UTC: set it (e.g. `America/New_York`). |
| `Timekeeping:LateEntryBusinessDays` | `1` | |
| `Timekeeping:MissingTimeReminderHour` | `9` | Local hour of the missing-time reminder. |
| `Expenses:ReceiptThreshold` | `75` | |
| `PerDiem:LodgingOveragePolicy` | `RequiresJustification` | Or `Unallowable`. |
| `PerDiem:JustifiedLodgingCapPercent` | `300` | |
| `PerDiem:Gsa:ApiKey` | — | **Secret.** api.data.gov key for GSA rates. |
| `PerDiem:Gsa:BaseUrl` | `https://api.gsa.gov/travel/perdiem/v2/` | |
| `Travel:TolerancePercent` | `10` | How far (percent) an expense report category or trip total may go over its approved travel authorization before it is flagged. |
| `Resourcing:NonBillableAllowancePercent` / `:FullThresholdPercent` / `:StaffingTolerancePercent` | `10` / `90` / `10` | |
| `FundingNotices:WarningThreshold` / `:CriticalThreshold` | `0.75` / `0.90` | Limitation of Funds notices. |
| `Billing:CompanyName` / `:RemitToAddress` / `:CageCode` / `:Uei` | — | Printed on invoices. |
| `Purchasing:RetainageAccountNumber` / `:RetainageAccountName` | `2020` / `AP retainage` | |
| `Purchasing:CashAccountNumber` / `:BankAccountNumber` | `1010` / `OPERATING` | |
| `Purchasing:Form1099Threshold` | `600` | |
| `Purchasing:RequisitionFinanceThreshold` | `10000` | |
| `Purchasing:MatchPriceTolerancePercent` | `5` | |
| `Purchasing:Sourcing:PriceWeight` / `:LeadTimeWeight` / `:DeliveryWeight` | `60` / `20` / `20` | How the RFQ comparison scores quotes (percent). |
| `Purchasing:Sourcing:NoHistoryDeliveryScore` / `:PerformanceMonths` | `80` / `24` | Delivery score of a vendor with no measured receipts; how far back on-time delivery is measured. |
| `Edi:InterchangeId` / `:InterchangeQualifier` / `:UsageIndicator` | `WORKBENCH` / `ZZ` / `P` | Our X12 identity (ISA06 out, ISA08 in); `T` marks files we send as test. |
| `Edi:FileDropRoot` | `App_Data/edi` | Root of the per-tenant, per-partner `inbound`/`outbound` folders. Put it on persistent storage. |
| `SupplierPortal:DemoContactPassword` | — | Development seed only: the password of the demo portal contact (jordan.reyes@primavera.example). Never set in production. |
| `Assets:CapitalizationThreshold` / `:GainLossAccountNumber` / `:CashAccountNumber` | `5000` / `4900` / `1010` | |
| `Materials:InventoryAccountNumber` / `:FinishedGoodsAccountNumber` / `:WipAccountNumber` / `:ReceivedNotInvoicedAccountNumber` / `:DirectMaterialAccountNumber` / `:PurchasePriceVarianceAccountNumber` / `:ManufacturingVarianceAccountNumber` / `:InventoryAdjustmentAccountNumber` / `:LaborAppliedAccountNumber` | `1410` / `1420` / `1450` / `1405` / `5120` / `6150` / `6151` / `6152` / `6195` | GL accounts materials posts to; created on first use when the chart lacks them |
| `Materials:Sales:CostOfGoodsSoldAccountNumber` / `Materials:Sales:ShipmentNumberPrefix` | `5160` / `SHP` | Cost of goods sold for sales order shipments (created on first use); the three letters DD250 shipment numbers start with |
| `Quality:NcrFinanceThreshold` / `Quality:EcoFinanceThreshold` | `5000` / `25000` | NCR dispositions of material worth more (at cost), and engineering changes with a larger cost impact, also go to Finance in the default approval workflows |
| `Materials:LandedCostClearingAccountNumber` / `:CostOfGoodsSoldAccountNumber` | `1406` / `5160` | Landed cost: the clearing account freight/duty bills are coded to (allocation clears it), and where landed cost of units no longer in stock goes; created on first use |
| `Payroll:Paylocity:BaseUrl` / `:TokenUrl` / `:Scope` | Paylocity production | Sandbox: `https://apisandbox.paylocity.com`. |
| `Payroll:Paylocity:ClientId` / `:ClientSecret` | — | Secret is a **secret**. |
| `Hr:CompensationFinanceThreshold` | `10000` | A pay change whose annualized increase is over this also needs Finance's approval (after HR). |
| `Costpoint:Source` / `:Csv:Folder` / `:SyncInterval` | `None` / — / `24:00:00` | Legacy import path; hidden in the UI. |
| `SamGov:ApiKey` | — | **Secret.** api.data.gov key for SAM.gov's Opportunities API (business development saved searches, run daily). Without it a built-in sample stands in. |
| `SamGov:BaseUrl` / `:UseSample` | `https://api.sam.gov/opportunities/v2/search` / `false` | `UseSample=true` ignores the key. |

## AI assistant

Server-wide connections; each organization turns the assistant on and picks a connection and model in **Admin › Assistant**
(off by default). Details: `docs/security/ai-assistant.md`.

| Key | Default | Notes |
| --- | --- | --- |
| `Assistant:EnableFakeProvider` | `false` (`true` in Development) | Offers the built-in "fake" connection (canned answers, no network). Refused outside Development and the public demo (`Demo:Enabled`). |
| `Assistant:Connections:<name>:Provider` | `Anthropic` | `Anthropic` or `Bedrock`. `<name>` is what administrators pick (letters, digits, `-`, `_`; not `fake`). |
| `Assistant:Connections:<name>:ApiKey` | — | **Secret.** Anthropic connections only. |
| `Assistant:Connections:<name>:BaseUrl` | — | Optional https base URL (an egress proxy) for Anthropic connections. |
| `Assistant:Connections:<name>:Region` | — | Bedrock: AWS region, e.g. `us-gov-west-1` for GovCloud (CUI). |
| `Assistant:Connections:<name>:AwsProfile` | — | Bedrock (Messages API endpoint): optional shared-config profile; otherwise the default AWS credential chain (instance/task/pod role). |
| `Assistant:Connections:<name>:BedrockApi` | `runtime` in GovCloud, else `mantle` | `runtime` (InvokeModel) needs `AWS_REGION` set to the connection's region. |
| `Assistant:Connections:<name>:DefaultModel` / `:Models` | `claude-opus-5-5` (Bedrock: `anthropic.claude-opus-5-5`) / `[]` | Models administrators may choose; the default is always offered. |
| `Assistant:Connections:<name>:Label` | provider name | Shown to administrators. |
| `Assistant:MaxToolCallsPerQuestion` / `:MaxRoundTrips` | `12` / `6` | Per question. |
| `Assistant:MaxOutputTokens` / `:Effort` | `16000` / `medium` | Per model call (`low`, `medium`, `high`, `max`). |
| `Assistant:MaxQuestionLength` / `:ConversationTurns` / `:MaxToolResultChars` | `2000` / `6` / `24000` | |
| `Assistant:RequestTimeout` | `00:03:00` | |

## Integrations (webhooks, API keys, connectors)

| Key | Default | Notes |
| --- | --- | --- |
| `Integrations:DispatcherEnabled` | `true` | Runs the dispatcher (webhook deliveries, Teams/Slack posts, scheduled exports) on this host. Safe on several hosts: every attempt is claimed first. |
| `Integrations:DispatchIntervalSeconds` | `15` | How often the dispatcher runs (2 to 3600). |
| `Integrations:MaxAttempts` | `12` | Tries per delivery (30 s, 1 min, 2 min … up to 6 h apart, about a day in all). |
| `Integrations:UnhealthyAfterFailures` | `10` | Consecutive failed attempts before an endpoint is marked unhealthy and only probed hourly. |
| `Integrations:RequestTimeoutSeconds` | `10` | Per request (connect and response). |
| `Integrations:RetentionDays` | `30` | Sent events, deliveries, connector runs and their files are deleted after this. |
| `Integrations:AllowHttp` | `false` (`true` in Development) | Lets endpoints use `http://`. Refused by the startup check outside Development. |
| `Integrations:AllowPrivateNetworkTargets` | `false` (`true` in Development) | Lets endpoints, Teams/Slack URLs and SFTP servers resolve to loopback or private addresses. Refused by the startup check outside Development. |

Outbound integration traffic goes straight from the web host (no proxy) to public addresses only. Allow egress on 443 (and the
SFTP ports you use) from the web hosts. Signing secrets and connector secrets are encrypted with the Data Protection key ring.

## Automation (Admin › Automation)

| Key | Default | Notes |
| --- | --- | --- |
| `Automations:BackgroundEnabled` | `true` | Runs queued automations (every few seconds), schedules and thresholds (every five minutes) on this host, for every tenant. Safe on several hosts: queue items are claimed first. Validation rules (refuse a change) run in the request regardless. |

Limits (chain depth, runs per record per hour) and the "pause every automation" switch are tenant settings on the Automation page, not configuration.

## Public demo

Only for the hosted demo with sample data (`docs/demo-hosting.md`); the `demo` image target sets these.

| Key | Default | Notes |
| --- | --- | --- |
| `Demo:Enabled` | `false` | With `ASPNETCORE_ENVIRONMENT=Demo` only: seeds the sample data, offers the demo sign-in page without an identity provider and turns the assistant on with the fake provider. Refused in any other environment; refuses a database that has users but no `dev@workbench.local`. |
| `Demo:NightlyReset` | `true` | Deletes the demo data once a day and stops the host, which the platform restarts with fresh data. |
| `Demo:ResetHourUtc` | `8` | Hour (UTC) of the nightly reset. |

## Backup job (environment of `scripts/backup-all.sh`)

| Variable | Default | Notes |
| --- | --- | --- |
| `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSFILE`/`PGPASSWORD`, `PGSSLMODE` | libpq defaults | Connection to the server. |
| `BACKUP_DIR` | `/backups` | Run folders go here; copy them off-host. |
| `BACKUP_DATABASES` | — | SingleTenant database(s). |
| `BACKUP_CATALOG_DB` | — | MultiTenant: the catalog; its Active and Suspended tenants are added. |
| `BACKUP_TENANT_DB_TEMPLATE` | `workbench_{tenant}` | Must match `Tenancy:TenantConnectionString`'s database name. |
| `BACKUP_GPG_RECIPIENT` / `BACKUP_GPG_RECIPIENT_FILE` | — | Encrypt to this key (required unless `BACKUP_ALLOW_UNENCRYPTED=1`). |
| `BACKUP_RETENTION_DAYS` / `BACKUP_MIN_KEEP` | `35` / `7` | |
| `BACKUP_NOTIFY_URL` | — | Dead-man switch pinged on success (`<url>`) and failure (`<url>/fail`). |
| `BACKUP_GPG_PRIVATE_KEY_FILE` | — | Restore test only: the private key, from the vault. |
| `RESTORE_TEST_KEEP` | — | `1` keeps the scratch databases. |
