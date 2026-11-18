# Push to QuickBooks Online and Xero

!!! info "Optional module: Billing"
    This page describes the **Billing** module, an optional add-on. It appears only when your license includes it. See [Optional modules](../reference/optional-modules.md).

<p class="roles">Finance and administrators (permission: Export to accounting software); registering the apps is the host administrator's job</p>

!!! warning "Not run against the real products"
    This feature was built and tested **only against fake HTTP handlers** that return the shapes in the vendors' documentation. Nothing was run
    against Intuit's or Xero's sandboxes, and no real OAuth sign-in has been completed. Before you rely on it, follow
    [Testing with each sandbox](#testing-with-each-sandbox) and read [What has not been verified](#what-has-not-been-verified).

**Admin › Integrations › Accounting export** can send journal entries, customer invoices and vendor bills **straight into QuickBooks Online or
Xero** instead of through an import file. Everything else about the [file export](accounting-export.md) still applies: the same account, customer,
vendor, class and item [mapping](accounting-export.md#account-mapping), the same preview, the same history, and the same rule that **a record is
never exported twice**. The file export keeps working with or without a connection.

## What you see

| Where | What |
|---|---|
| **Live connections** tab | One card per package: connect, disconnect, the company it is connected to, the last push and how it went. Xero asks which organisation when you connected several. |
| **Export** tab, under the file export | For QuickBooks and Xero formats and the kinds journals, invoices and bills: **Push to QuickBooks Online / Xero**, with a preview, the run result and a retry button. |
| **History** tab | Every push is a row (format *qbo-live* or *xero-live*, no file) with who, when, period, counts and a control total. |
| **Live connections › Recent pushes** | Open a push to see every record: created, already there, failed (with the provider's message), not sent. |

## Connecting

1. Choose **Connect QuickBooks Online** (or Xero). You are sent to the provider, sign in and choose the company or organisation, and approve.
2. You come back to Workbench, which checks the one-time `state` it sent (so only this sign-in can finish the connection), trades the code for tokens
   on the server and stores them **encrypted, per tenant**. Tokens are never shown or sent to a browser.
3. The connection renews itself. Both providers **rotate the refresh token on every renewal**; Workbench saves the new one immediately and
   keeps using only the newest. If the provider ever refuses the refresh (revoked, unused for too long, changed password policies), the card says
   **needs to be connected again** and pushes stop until you connect.
4. **Disconnect** revokes the token at the provider and forgets it. Records already pushed keep their provider ids in Workbench.

Connecting a **different** company after records were pushed is refused: the provider ids Workbench remembers belong to the first company.

## Pushing

1. Make sure the [mapping](accounting-export.md#account-mapping) for the package is complete. A push needs every account, customer, vendor and (QuickBooks)
   item mapped; it does not offer the file export's "export with Workbench names".
2. Choose the format (QuickBooks Online or Xero), the kind and the period. Choose **Preview the push**. It lists:
   how many records will be created, lines and control total; anything **not mapped**; anything the mapping names that is **not in the provider yet**
   (accounts and products must exist there; customers and vendors can be created for you if you tick *Create customers and vendors that aren't in ... yet*);
   projects without a matching class or tracking option (those lines go without one); warnings.
3. **Push**. Workbench sends the records in batches, saves a result for each, and shows them.
4. If some failed, fix what the message says and choose **Retry the failed**. Only the failed records are sent.

### What is created

| | QuickBooks Online | Xero |
|---|---|---|
| Journal entries | JournalEntry, debit and credit lines, class per line | Manual journal, signed amounts, tracking option per line |
| Invoices | Invoice with sales-item lines, class per line | Invoice (ACCREC) with the revenue account on each line |
| Bills | Bill with account-based lines, class per line | Bill (ACCPAY) with the expense account on each line |
| Status | Posted | **Draft** by default (`Accounting:Xero:PostAsDraft`): approve in Xero. Set it to `false` to authorise and post straight away |
| Per call | Up to 30 records per batch | Up to 50 records per call |

Not pushed: credit memos and vendor credits (shown as **not sent**, never marked exported, still available for the file export), customer
payments and the chart of accounts, tax codes (QuickBooks applies the product's default; Xero lines carry no tax), and foreign currency (everything is in the
functional currency, as in the file export). Journal lines on **Accounts Receivable or Payable** need a customer or vendor in QuickBooks, and Xero refuses bank,
receivable and payable accounts in manual journals, so such entries fail with the provider's own message and stay unexported: export those documents as invoices and bills.

## Never twice, and what happens when something goes wrong

- A pushed record gets the **provider's own id**, saved against it. A record that has one is never created again, whatever else is asked
  (*Push again* with a note reports it as **Already there**). The file export's marks apply too: a record exported as a file is not pushed, and the other way round.
- Every create carries an **idempotency key** built from the record ids and how many times each has failed before (QuickBooks `requestid`, Xero `Idempotency-Key`).
  If an answer is lost (timeout, 5xx) and Workbench retries, the provider recognises the key and returns the first result instead of creating a second copy;
  a retry after a failure is a new request.
- A failed record is listed with the provider's message, is **not** marked exported, and stays available to retry. The rest of the batch still goes through:
  QuickBooks answers per item, and Xero is called with `summarizeErrors=false`.
- Only one push per package runs at a time (a run that has not finished blocks another for 30 minutes).
- **401**: the token is renewed and the call tried once more. **429**: Workbench waits for the provider's `Retry-After` (a minute when it gives none) and tries
  again, up to four times. **5xx or no answer**: waits 2, 4, 8, 16 seconds. After that the records show as failed with the reason and can be retried.
- The record of what happened is saved after the provider answers. If saving fails the error says so and tells you not to push again until you have checked the provider.

## For the host administrator: registering the apps

Client ids and secrets are **server configuration**, never stored in the database or the source tree. Use environment variables or a secret store:

| Setting | Meaning |
|---|---|
| `Accounting:QuickBooks:ClientId`, `ClientSecret` | From the Intuit Developer portal: **Development** keys for a sandbox company, **Production** keys for a real one |
| `Accounting:QuickBooks:RedirectUri` | `https://your-workbench-host/admin/integrations/accounting-live/callback/quickbooks`, exactly as registered with Intuit (case, scheme and trailing slash count) |
| `Accounting:QuickBooks:Sandbox` | `true` to call `sandbox-quickbooks.api.intuit.com` (with Development keys), `false` for `quickbooks.api.intuit.com` |
| `Accounting:QuickBooks:Scopes` | Optional; default `com.intuit.quickbooks.accounting` |
| `Accounting:Xero:ClientId`, `ClientSecret` | From the Xero developer portal (a Web app, authorization code grant) |
| `Accounting:Xero:RedirectUri` | `https://your-workbench-host/admin/integrations/accounting-live/callback/xero`; Xero requires https, and allows `http://localhost` for testing but not `http://127.0.0.1` |
| `Accounting:Xero:Scopes` | Optional; default `offline_access accounting.invoices accounting.manualjournals accounting.contacts accounting.settings` |
| `Accounting:Xero:PostAsDraft` | `true` (default): invoices, bills and journals arrive as drafts |
| `Accounting:Xero:Sandbox` | Only labels the connection; Xero has no separate API host (use its Demo Company) |

Environment variable form: `Accounting__QuickBooks__ClientId`, `Accounting__Xero__ClientSecret`, and so on.

**One redirect address per app.** A deployment with a single host works as described. In multi-tenant mode every tenant is its own host, and the
redirect address is one value per provider, so each provider app would have to list, and Workbench would have to be configured with, the address of the tenant
that connects. Until that is solved, connect from one host.

### Registering with Intuit (QuickBooks Online)

1. Sign in at developer.intuit.com and create an app; select the **QuickBooks Online Accounting** scope.
2. Under **Development › Keys & OAuth** copy the Client ID and Client secret (these are for sandbox companies) and add the redirect URI above.
3. Create a **sandbox company** in the developer portal (Sandboxes).
4. Production keys (**Production › Keys & OAuth**) are for real companies. Intuit may ask for details about the app before it issues them; that process
   was not read when this was built, so check the portal.

### Registering with Xero

1. Sign in at developer.xero.com › My apps › New app. Choose a **Web app** (authorization code flow), enter your company and app URLs and the redirect URI above.
2. Generate a **client secret** and copy it with the client id.
3. A new app is in Xero's starter tier, which allows **5 connected organisations** and 1,000 calls a day per organisation; higher tiers have more (Xero's
   OAuth 2.0 API limits page). Since March 2026 Web apps are given Xero's granular scopes, which is what the defaults ask for; the broad
   `accounting.transactions` scope is deprecated until September 2027.
4. Xero's **Demo Company** is the safe place to test.

## Testing with each sandbox

Do this before the first real push. Nothing below has been done by the people who built the feature.

**QuickBooks Online sandbox**

1. Set the QuickBooks configuration with Development keys and `Sandbox: true`, restart, open **Live connections** and connect the sandbox company.
2. In **Account mapping**, map the sandbox company's accounts, customers and products. The sandbox's sample company has its own chart of accounts; names are matched
   without regard to case, and sub-accounts are written `Parent:Sub`.
3. Push one invoice, then one bill, then one journal entry. Open each in QuickBooks and compare the amounts and dates.
4. Check these specifically: the **minor version** (`minorversion=75` is sent), how a **duplicate document number** behaves, whether the line `Qty` and `UnitPrice`
   are accepted next to `Amount`, A/R lines on journals, **class** tracking being switched on in the company, and what a sandbox does with the `requestid` key
   (push, then force a retry by cutting the network to see the replay).
5. Disconnect, connect again, and push a second time: the first record must say **Already there**, not be created again.

**Xero Demo Company**

1. Set the Xero configuration, connect (choose the Demo Company on Xero's consent page; if you pick several organisations Workbench asks which one).
2. Map accounts using the **account code** (the code column) and the contact names; map projects to a tracking option with the tracking category in the code column.
3. Push one invoice, one bill, one journal; they arrive as drafts. Compare them with the source.
4. Check specifically: **line amount** rounding with quantity × unit amount, the `Idempotency-Key` replay, manual journals on a bank account (should fail with Xero's message),
   duplicate invoice numbers, and the **organisation's currency** (all amounts go as the functional currency).

## What has not been verified

- No call has reached Intuit or Xero. Token exchange, renewal and rotation, 401, 429 and 5xx handling and partial failure are tested with recorded-shape fakes only
  (`tests/Workbench.Tests/AccountingLive`). Real error bodies and edge cases will differ.
- QuickBooks: OAuth, base URLs, minor versions, `/batch` limits (30 items, 40 calls a minute), the Fault shape and the `requestid` rules were read in Intuit's documentation. The **Invoice,
  Bill, Customer, Vendor, Item and Class field shapes were written from the API reference's known structure, not re-read**, and Intuit's pages for them were not fetched.
  Intuit's API call limits page was seen only in search results (500 requests a minute and 10 a second per realm, 429 on excess, a minute's wait advised).
- Xero: OAuth, scopes, connections, rate limits and the `Idempotency-Key` header were read in Xero's developer documentation and OpenAPI description. Request bodies follow the OpenAPI
  description, not a live organisation. Xero's **Contacts POST** creates or updates by name; Workbench only calls it for names it did not find.
- **PKCE** is not used: neither provider documents it for the confidential web-app flow Workbench uses (Xero documents it for apps without a client secret). The one-time `state` is the CSRF defence.
- Refresh-token rotation across **several servers** relies on the saved version of the connection and, for Xero, its 30-minute grace period for the previous token; it was tested for concurrent requests in one process.
- The Integrations **edition gate** (which a licensed edition can switch off) lives in the modules and entitlements work that this branch does not include. When both are merged, add
  `services.Gate<IAccountingConnectionService>(Feature.Integrations, nameof(IAccountingConnectionService.StartAsync), nameof(IAccountingConnectionService.CompleteAsync))` and the same for
  `IAccountingPushService.PushAsync` next to the Jira and GitLab lines in `EditionGates`. Until then the permission is the only gate.
- Credit memos, vendor credits, customer payments, tax codes and the chart of accounts are not pushed.

## For developers

- A provider is an `IAccountingOAuth` (endpoints, token calls) and an `IAccountingPublisher` (`Application/AccountingLive`), registered in `AccountingLiveRegistration`.
  A publisher takes the same `ExportInput` as an `IAccountingExporter` and returns a result per record; `AccountingPushService` owns the run, the provider links
  (`AccountingProviderLink`), the exported-record marks and the history. Don't write those rows from a publisher.
- Calls go through `AccountingApi` (renews on 401, backs off on 429 and 5xx) over `IAccountingHttp`; tests register `FakeAccountingHttp` and a recording `IAccountingDelay`.
- Tokens are protected with Data Protection (`AccountingSecrets`, per-tenant through the tenant-aware provider). `AccountingConnection.TokenVersion` is a concurrency token;
  `AccountingTokenService` is the only code that writes refreshed tokens.
- API: `/api/v1/accounting-live` (connections, `preview`, `push`, `runs`, `runs/{id}/retry`). It is closed to API keys: connecting is a person's sign-in.
- Sources are cited in the code comments of `QuickBooksOAuth`, `XeroOAuth`, `QuickBooksPublisher` and `XeroPublisher`.
