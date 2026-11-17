# License server

The license server is a small, separate service that sells and issues Workbench licenses by module. It is run by the
vendor (you, if you resell Workbench), not by customers. Customers only ever see the key it produces, or the portal page.
It does not reference the Workbench application: the single contract between the two is the `WB2` key format below.

What it does:

- keeps **customers**, **subscriptions** (modules, seats, light seats, term, status, Stripe ids), every **key it issued**, the **revocation list** and the **activations** of connected installs;
- issues keys by hand (the air-gapped defense flow) from an admin page and API;
- turns **Stripe** events (checkout, renewal, failed payment, seat change, cancellation) into entitlement changes and a fresh emailed key;
- gives connected installs `POST /v1/activate` and `POST /v1/refresh` for short-lived keys while the subscription is paid;
- gives customers a **portal** (`/portal`): sign in by emailed link, see the subscription, download the current key, open Stripe's billing portal to change seats.

!!! warning "What is stubbed"
    See [What is stubbed or unverified](#what-is-stubbed-or-unverified) before going live. The mailer only logs, and the Stripe
    integration has never been run against real Stripe.

## The key format (WB2)

```text
WB2.<base64url(payload JSON)>.<base64url(signature)>
```

The signature is ECDSA P-256 over the exact payload bytes (SHA-256, IEEE P1363 fixed-field signature), the same scheme as the older
`WB1` keys made by `tools/license.cs`. The payload has exactly these fields:

| Field | Meaning |
| --- | --- |
| `v` | `2` |
| `licenseId` | GUID. Stable for a subscription, so a revocation and the connected endpoints can name it |
| `licensee` | The customer's name |
| `issuedAt`, `expires` | ISO dates (`2027-01-31`) |
| `seats`, `lightSeats` | Full seats and light (view/self-service) seats |
| `modules` | Any of `billing`, `accounting`, `capture`, `workforce`, `projectcontrols`, `materials`, `govcon`, `enterprise`, `ai`. `core` is implied |
| `moduleSeats` | Optional. Object of module id to seats (a non-negative integer), for modules whose seats differ from `seats`. Absent: `seats` applies to every module. The server writes it only for modules in `modules` with a count different from `seats`, so a key with no per-module seats has no such member |
| `trial` | `true` for a trial |

Example payload: `{"v":2,"licenseId":"...","licensee":"...","issuedAt":"2027-01-01","expires":"2028-01-01","seats":25,"lightSeats":10,"modules":["accounting","billing"],"moduleSeats":{"accounting":10,"billing":5},"trial":false}`.

The retired module ids `finance` and `controls` are no longer issued (the admin API answers 400). Readers map old keys: `finance` is `billing` + `accounting`, `controls` is `enterprise`.

The public half of the signing key is what the product embeds to verify keys; the server publishes it at `GET /v1/public-key`.

## Signing key: generate, store, rotate, back up

The private key never goes in the repository, an image or `appsettings.json`. Generate it once, on a trusted machine:

```bash
dotnet run tools/license.cs -- keygen ./license-keys
```

This writes `private.pem` (mode 600) and `public.pem`. Then:

1. **Store** `private.pem` in your secret store (Key Vault, AWS Secrets Manager, Docker/Kubernetes secret) and give the server either
   `Signing__PrivateKeyPath=/run/secrets/license-private.pem` (a mounted file) or `Signing__PrivateKeyPem` (the PEM; `\n` sequences are accepted so it fits an environment variable).
   The server refuses to start without a valid P-256 key.
2. **Back up** `private.pem` to at least two separate, access-controlled places (an offline copy in a safe is normal). If it is lost you cannot
   issue keys the shipped product accepts, and every customer needs a new build or setting. If it leaks, treat every key as forgeable and rotate.
3. **Publish** `public.pem` to the product: it goes in the product's built-in public key (and the `License:PublicKeyPem` setting for builds that allow overriding it).
4. **Rotate** by generating a new pair, shipping a product build that trusts **both** public keys, then switching the server to the new private key
   and reissuing keys (customers get a fresh key by email, from the portal, or on their next refresh). Retire the old public key after the last key signed with it has expired.
   Nothing in a key says which key signed it, so overlap the trust period at least as long as your longest offline key (a year).

## Run it

```bash
docker build -f src/Workbench.LicenseServer/Dockerfile -t workbench-license-server .
docker run -p 8080:8080 \
  -v license-data:/data -v "$PWD/secrets:/run/secrets:ro" \
  -e Admin__ApiKey="$(openssl rand -hex 32)" \
  -e Signing__PrivateKeyPath=/run/secrets/license-private.pem \
  -e Portal__PublicBaseUrl=https://license.example.com \
  workbench-license-server
```

Put it behind TLS (a reverse proxy or your platform's load balancer) and set `ForwardedHeaders__TrustAll=true` only when the proxy is the sole way in, so rate limits see the real client address.
The image runs as a non-root user, keeps the SQLite database and the cookie-protection keys in `/data` (mount a volume), and answers `GET /healthz` for orchestrator probes.
It migrates its database on start (`Database__MigrateOnStartup`, default true). For anything beyond a single small instance use PostgreSQL:
`Database__Provider=PostgreSql` and `Database__ConnectionString="Host=...;Database=licenses;Username=...;Password=..."`.

### Settings

Every setting is an environment variable (`Section__Name`) or an `appsettings` key.

| Setting | Default | What it does |
| --- | --- | --- |
| `Admin:ApiKey` | none | The admin credential, at least 24 characters. Unset switches the admin API off (503) |
| `Signing:PrivateKeyPem` / `Signing:PrivateKeyPath` | none | The signing key (see above). One is required |
| `Database:Provider` | `Sqlite` | `Sqlite` or `PostgreSql` |
| `Database:ConnectionString` | `Data Source=App_Data/licenses.db` | Connection string |
| `Database:MigrateOnStartup` | `true` | Apply migrations when the server starts |
| `Keys:GraceDays` | `7` | Days a key keeps working past the paid-through date |
| `Keys:RefreshDays` | `14` | Lifetime of a connected install's refreshed key |
| `Keys:MaxInstalls` | `5` | Installs (production, staging, DR) one license may activate |
| `Keys:OfflineDays` | `365` | Default term of an offline key issued by hand |
| `RateLimits:ConnectedPerMinute` | `30` | Per client address, for `/v1/*` |
| `RateLimits:LoginPerMinute` | `5` | Per client address, for the portal sign-in form |
| `Portal:PublicBaseUrl` | the request | Base URL used in emailed links, e.g. `https://license.example.com` |
| `Portal:LoginLinkMinutes` / `Portal:SessionHours` | `15` / `8` | Sign-in link and session lifetime |
| `Portal:DataProtectionPath` | memory (image: `/data/keys`) | Where cookie-protection keys persist, so sessions survive restarts |
| `Stripe:WebhookSecret` | none | The webhook signing secret (`whsec_...`). Unset: the webhook answers 503 |
| `Stripe:SecretKey` | none | Stripe API key (`sk_...`), to read subscriptions and open billing-portal sessions |
| `Stripe:PortalReturnUrl` | `<base>/portal` | Where Stripe's portal sends people back to |
| `Stripe:Prices:<price_id>:...` | none | What each Stripe price sells (below) |
| `ForwardedHeaders:TrustAll` | `false` | Trust `X-Forwarded-*` from any proxy |

## Admin: customers, subscriptions and keys

Open `/admin/` and paste the admin key (it is kept only in that browser tab). The page drives the admin API, which you can also call directly with
`X-Admin-Key: <key>` or `Authorization: Bearer <key>`:

| Call | What it does |
| --- | --- |
| `POST /admin/api/customers` `{name, email, notes}` | Create a customer |
| `GET /admin/api/customers`, `GET /admin/api/customers/{id}` | List; one customer with subscriptions and licenses |
| `POST /admin/api/customers/{id}/subscriptions` `{modules, seats, lightSeats, moduleSeats, termMonths \| termEnd, trial}` | Create a subscription and its first key. `moduleSeats` is e.g. `{"accounting": 10}` |
| `PATCH /admin/api/subscriptions/{id}` `{modules, seats, lightSeats, moduleSeats}` | Change entitlements and issue a new key (`moduleSeats` replaces the whole map) |
| `POST /admin/api/subscriptions/{id}/extend` `{months \| termEnd}` | Extend the paid-through date (also reactivates a lapsed one) |
| `POST /admin/api/subscriptions/{id}/cancel` | Cancel now: connected installs get `402 subscription_canceled` |
| `POST /admin/api/subscriptions/{id}/reissue` `{email}` | Issue the current key again, optionally emailing it |
| `POST /admin/api/licenses/offline` `{customerId, modules, seats, lightSeats, moduleSeats, days \| expires, trial}` | An offline key |
| `POST /admin/api/licenses/{id}/revoke` `{reason}` | Revoke a license (keeps the history) |
| `GET /admin/api/licenses/{id}/history`, `/history`, `/revocations` | Issued keys and activations; recent keys; revoked ids |

### The offline (air-gapped) flow

Defense customers on isolated networks never connect. For them:

1. Create the customer, then **Offline key**: pick the modules, seats and a term (default 365 days, set `days` or `expires` for longer).
2. Hand over the key (email, ticket, removable media). The customer pastes it in Workbench; nothing calls out.
3. At renewal, issue a new offline key. To cut one off early, **revoke** it: an air-gapped install can't learn of it, so a revocation protects
   only installs that connect (and the portal stops serving the key). Keep offline terms to what you are willing to leave out there.

`tools/license.cs` still makes `WB1` keys and still works for the current product; the server issues `WB2`.

## Stripe

Create products and prices in Stripe, a Checkout (or Payment Link) for each, and a webhook endpoint at `https://<server>/stripe/webhook`
subscribed to `checkout.session.completed`, `invoice.paid`, `invoice.payment_failed`, `customer.subscription.updated` and
`customer.subscription.deleted`. Put its signing secret in `Stripe:WebhookSecret` and an API key in `Stripe:SecretKey`.
Enable the Stripe **customer portal** (Billing settings) and allow quantity changes, so the portal's *Manage billing* button can change seats.

Tell the server what each price sells. A price has a `Role`:

- `Seats`: quantity × `SeatsPerUnit` (default 1) **full seats**.
- `LightSeats`: quantity × `SeatsPerUnit` **light seats**.
- `Modules`: turns the listed `Modules` on, and adds `IncludedSeats` / `IncludedLightSeats` once.
- `ModuleSeats`: turns the listed `Modules` on with quantity × `SeatsPerUnit` seats for just those modules (a per-module seat count). `seats` stays the base from `Seats`/`Modules` prices; if no price gives base seats, `seats` is the largest per-module count. Only counts that differ from `seats` go into `moduleSeats`.

```bash
Stripe__Prices__price_1Abc__Role=Seats
Stripe__Prices__price_1Def__Role=Modules
Stripe__Prices__price_1Def__Modules__0=billing
Stripe__Prices__price_1Ghi__Role=Modules            # a bundle that includes 5 seats
Stripe__Prices__price_1Ghi__Modules__0=govcon
Stripe__Prices__price_1Ghi__Modules__1=enterprise
Stripe__Prices__price_1Ghi__IncludedSeats=5
Stripe__Prices__price_1Jkl__Role=ModuleSeats        # accounting sold per seat: quantity 10 = 10 accounting seats
Stripe__Prices__price_1Jkl__Modules__0=accounting
```

How events behave:

| Event | Effect |
| --- | --- |
| `checkout.session.completed` | Fetches the subscription, creates or links the customer (by Stripe id, then email), creates the subscription, issues and emails a key |
| `invoice.paid` | Refreshes the paid-through date from Stripe; a renewal emails a new key |
| `invoice.payment_failed` | Marks the subscription past due. The key keeps working until the paid-through date plus `Keys:GraceDays` |
| `customer.subscription.updated` | Applies seat/module/status changes; a real change emails a new key, an identical redelivery does nothing |
| `customer.subscription.deleted` | Cancels: connected installs get `402`, the portal stops serving a key |

Events are verified against the `Stripe-Signature` header (HMAC-SHA256, five-minute tolerance) and remembered by id, so Stripe's retries are harmless.
A price that is not in `Stripe:Prices` is logged and ignored; a subscription with none of its prices mapped changes nothing.

## Connected installs

`POST /v1/activate` and `POST /v1/refresh` take JSON `{licenseId, installId, seatsInUse, lightSeatsInUse, version}` and return a fresh key that lasts `Keys:RefreshDays`:

```json
{ "key": "WB2....", "expires": "2027-01-15", "refreshAfter": "2027-01-08", "modules": ["accounting", "billing"], "seats": 25, "moduleSeats": { "accounting": 10 }, "lightSeats": 5, "trial": false, "warnings": [] }
```

Seat changes in Stripe or the admin page show up on the next refresh. `warnings` says when reported seats exceed the license; the server does not block
over-use, the product decides. Failures:

| Status | `reason` | Meaning |
| --- | --- | --- |
| 402 | `subscription_canceled`, `subscription_lapsed`, `no_subscription` | Not entitled. Lapsed means past the paid-through date plus grace |
| 403 | `license_revoked` | Revoked |
| 404 | `unknown_license`, `not_activated` | Wrong id, or `refresh` before `activate` |
| 409 | `too_many_installs`, `offline_license` | Over `Keys:MaxInstalls`; or an offline key (those never connect) |
| 429 | | Rate limited |

`GET /v1/revocations` lists revoked license ids. The in-product client for these endpoints is a follow-up.

## Customer portal

`/portal`: the customer enters their email and receives a one-time link (valid 15 minutes). The portal shows each subscription (modules, seats and any per-module seats, status, paid-through date),
downloads the **current** key (a fresh one if the entitlement changed since the last), and, for Stripe customers, opens Stripe's billing portal.
The sign-in form answers the same whether or not the address is known.

## Tests

```bash
dotnet test tests/Workbench.LicenseServer.Tests
WORKBENCH_TEST_DB=PostgreSql WORKBENCH_TEST_POSTGRES="Host=localhost;Username=workbench;Password=workbench" dotnet test tests/Workbench.LicenseServer.Tests
```

The tests include an independent WB2 verifier written from the spec: issued keys round-trip, and changing any byte of the payload or signature breaks them.
Schema changes are EF migrations per provider in `src/Workbench.LicenseServer/Migrations/{Sqlite,Postgres}` (`dotnet tool run dotnet-ef migrations add Name --context SqliteLicenseDb -o Migrations/Sqlite`, then again for `PostgresLicenseDb` and `Migrations/Postgres`); CI fails if a model change has no migration.

## What is stubbed or unverified

- **Email is a stub.** `LogMailer` writes the message to the log (a sign-in link or a key in the log is fine for a trial, not for production). Implement `IMailer` (SMTP, SES, SendGrid) and register it before launch.
- **Stripe was never called for real.** The webhook handler is tested with locally signed sample payloads, and the Stripe API (`GetSubscription`, `GetCustomer`, billing-portal session) with a fake. `HttpStripeApi` is written from Stripe's documentation and has not run against Stripe, not even test mode. Exercise it with Stripe CLI (`stripe listen --forward-to`, `stripe trigger`) and a test-mode key first; check the price mapping and the portal configuration.
- **Tax, invoicing, proration and dunning policy** are Stripe's business; the server only reacts to the events above.
- **No client in Workbench.** The in-product WB2 parser and the activate/refresh client belong to the modules/entitlements work; until they land, the product reads only `WB1` keys.
- **Admin is one shared key**, not per-user accounts with an audit trail. Keep it in a secret store and rotate it; put the admin page behind a VPN or IP allow-list if you can.
- **Portal sessions** are cookie-protected, not revocable server-side before they expire (`Portal:SessionHours`).
