# Webhooks

Workbench sends an HTTPS `POST` to each registered endpoint when an event it subscribes to happens. Administrators register
endpoints under **Admin › Integrations › Webhooks** (or `POST /api/v1/integrations/webhooks`). This page is for the people who
build the receiver.

## Delivery

- **When.** Events are written to an outbox in the same database transaction as the change they describe, so an event is sent
  if and only if the change was saved. A dispatcher picks them up within about 15 seconds.
- **At least once.** A delivery can repeat (a retry after a timeout, an admin pressing **Redeliver**). Use the payload's `id` to
  ignore an event you already handled. Events of one subject are sent in the order they happened, but retries can reorder them;
  compare `occurredAt` if order matters.
- **Answer quickly.** Reply with any `2xx` within 10 seconds, then do the work. Workbench doesn't follow redirects.
- **Retries.** No answer, a timeout, `408`, `429` or any `5xx` is retried after 30 s, 1 min, 2 min, 4 min … doubling up to 6 hours,
  12 tries in all (about a day). Other `4xx` answers (for example `401` from a bad signature check) aren't retried.
- **Unhealthy endpoints.** After 10 failed attempts in a row the endpoint is marked unhealthy, the admin who registered it is
  notified, and Workbench tries only once an hour until one succeeds. **Resume** in the admin page sends the waiting deliveries at once.
- **Log.** Every attempt is kept for 30 days with the request headers and body, the response status, headers and the first 4 KB
  of the body. **Redeliver** sends the same event again (same body, new `Workbench-Delivery` id and a fresh signature).
- **Targets.** Endpoints must use `https://` and resolve to public internet addresses. Workbench refuses loopback, private,
  link-local (including cloud metadata `169.254.169.254`), carrier-grade NAT, multicast and reserved ranges, in IPv4 and IPv6,
  when the endpoint is saved and again each time it connects.

## Request

```http
POST /your/path HTTP/1.1
Content-Type: application/json; charset=utf-8
User-Agent: Workbench-Integrations/1.0
Workbench-Event: invoice.approved
Workbench-Event-Id: 0192f5a1-7c3e-7d4b-9a51-1c0f2b3d4e5f
Workbench-Delivery: 0192f5a1-8a10-7f00-8c2d-6b5a4c3d2e1f
Workbench-Attempt: 1
Workbench-Signature: t=1760886000,v1=5f2c0d…

{"id":"0192f5a1-7c3e-7d4b-9a51-1c0f2b3d4e5f","type":"invoice.approved","version":1,"occurredAt":"2026-10-19T14:20:00Z",
 "subject":"Invoice","subjectId":"0192f5a0-…","actor":"pat@example.com","data":{…}}
```

## Verifying the signature

`Workbench-Signature` is `t={unix seconds},v1={signature}`. The signature is the lower-case hex HMAC-SHA256 of
`{t}.{raw body}`, keyed with the endpoint's signing secret (the whole `whsec_…` string, UTF-8). To check a request:

1. Read the **raw** body bytes before any JSON parsing (re-serializing changes the bytes).
2. Compute `HMAC-SHA256(secret, t + "." + body)` as lower-case hex.
3. Compare it with `v1` in constant time. Refuse the request if it doesn't match.
4. Refuse `t` more than 5 minutes from your clock (replays).

The secret is shown once, when the endpoint is created or its secret replaced. **Replace signing secret** takes effect at once,
so update the receiver right away. .NET code can use `Workbench.Contracts.Integrations.WebhookSignature.Verify(secret, header, body, now)`.

C#:

```csharp
static bool Verify(string secret, string header, string body, DateTimeOffset now)
{
    var parts = header.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    var t = long.Parse(parts["t"]);
    if (Math.Abs(now.ToUnixTimeSeconds() - t) > 300) return false;
    var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
    return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(mac)), Encoding.ASCII.GetBytes(parts["v1"]));
}
```

Node.js:

```js
import crypto from "node:crypto";

export function verify(secret, header, rawBody) {
  const parts = Object.fromEntries(header.split(",").map(p => p.split("=", 2)));
  if (Math.abs(Date.now() / 1000 - Number(parts.t)) > 300) return false;
  const expected = crypto.createHmac("sha256", secret).update(`${parts.t}.${rawBody}`).digest("hex");
  return crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(parts.v1 ?? ""));
}
```

Python:

```python
import hashlib, hmac, time

def verify(secret: str, header: str, raw_body: bytes) -> bool:
    parts = dict(p.split("=", 1) for p in header.split(","))
    if abs(time.time() - int(parts["t"])) > 300:
        return False
    expected = hmac.new(secret.encode(), parts["t"].encode() + b"." + raw_body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, parts.get("v1", ""))
```

## Payload

Every body has the same envelope:

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | uuid | The event. The same in every delivery and redelivery of it. |
| `type` | string | The event type (below). |
| `version` | int | Payload version, currently `1`. New fields may be added within a version; fields are never removed or renamed. |
| `occurredAt` | ISO 8601 UTC | When the change was made. |
| `subject` / `subjectId` | string / uuid | What the event is about (`Invoice` and its id). |
| `actor` | string or null | Email of who made the change (`null` for automatic changes). |
| `data` | object | The event's fields (below). |

Dates are `yyyy-MM-dd`, money is a JSON number in the functional currency unless `currencyCode` says otherwise, ids are UUIDs.

**What's never sent.** Payloads are fixed allow-lists of ids and summary fields. They never contain pay, cost or bill rates,
salaries, bank or tax details, EEO data, or sheet cell values (so restricted columns can't leak). A receiver that needs more
calls the REST API with an [API key](api-keys.md), where the usual permission checks apply. `Workbench.Tests/Integrations/PayloadRedactionTests`
fails the build if a sensitive-looking field is added.

### Events

| Type | When | `data` fields |
| --- | --- | --- |
| `timesheet.submitted` | An employee certifies and submits a timesheet. | `timesheetId`, `employeeId`, `employeeNumber`, `employeeName`, `periodStart`, `periodEnd`, `totalHours`, `status` |
| `timesheet.approved` | A timesheet's last approval step approves it. | same as above |
| `invoice.approved` | An invoice or credit memo is approved and posted. | `invoiceId`, `number`, `type` (`TimeAndMaterials`, `CostPlus`, `FixedPriceMilestone`, `TrueUp`, `CreditMemo`), `companyId`, `contractId`, `customerId`, `invoiceDate`, `dueDate`, `total`, `amountPaid`, `balance`, `currencyCode`, `status` |
| `invoice.sent` | An approved invoice is first marked sent. | same as above |
| `invoice.paid` | Receipts pay an invoice in full. | same as above |
| `bill.approved` | A vendor bill finishes its approval. | `billId`, `number`, `companyId`, `vendorId`, `vendorInvoiceNumber`, `invoiceDate`, `dueDate`, `amount`, `paidAmount`, `balance`, `currencyCode`, `status` |
| `bill.paid` | A payment run pays a bill in full. | same as above |
| `purchase_order.approved` | A purchase order is approved and issued to the vendor. | `purchaseOrderId`, `number`, `vendorId`, `orderDate`, `revision`, `total`, `status` |
| `project.created` | A project is set up (by hand or from a won opportunity). | `projectId`, `code`, `name`, `companyId`, `contractId`, `projectManagerId`, `status`, `start`, `finish` |
| `employee.hired` | An employee record is added. | `employeeId`, `employeeNumber`, `displayName`, `email`, `companyId`, `orgUnitId`, `supervisorId`, `hireDate`, `terminationDate` |
| `employee.terminated` | A termination date is set or changed (setup or HR offboarding). | same as above |
| `opportunity.stage_changed` | An opportunity moves stage (gate decisions, won, lost, no bid, reopen). | `opportunityId`, `number`, `name`, `fromStage`, `toStage`, `estimatedValue`, `estimatedAwardDate` |
| `period.closed` | A fiscal period is closed (period close or the fiscal calendar). | `fiscalPeriodId`, `fiscalYear`, `number`, `name`, `start`, `end`, `status` |
| `shipment.shipped` | A sales order shipment is confirmed and its stock issued. | `shipmentId`, `number`, `shipmentNumber`, `companyId`, `salesOrderId`, `salesOrderNumber`, `customerId`, `contractId`, `shipDate`, `carrier`, `trackingNumber`, `isFinal`, `value` |
| `rfq.awarded` | Lines of an RFQ are awarded into purchase orders. | `rfqId`, `number`, `title`, `awardId`, `vendorsInvited`, `quotesReceived`, `soleSource`, `awardedTotal`, `purchaseOrderNumbers` |
| `ncr.dispositioned` | A nonconformance's approved disposition is carried out and the NCR closed. | `ncrId`, `number`, `partId`, `lot`, `serial`, `quantity`, `disposition`, `vendorId`, `workOrderId` |
| `ping` | **Send a test** in the admin page. | `message`, `endpointId` |

Example `invoice.paid`:

```json
{
  "id": "0192f5a1-7c3e-7d4b-9a51-1c0f2b3d4e5f",
  "type": "invoice.paid",
  "version": 1,
  "occurredAt": "2026-10-19T14:20:00Z",
  "subject": "Invoice",
  "subjectId": "0192f5a0-1111-7000-8000-000000000001",
  "actor": "finance@example.com",
  "data": {
    "invoiceId": "0192f5a0-1111-7000-8000-000000000001",
    "number": "INV-1001-0007",
    "type": "TimeAndMaterials",
    "companyId": "00000000-0000-0000-0000-00000000c001",
    "contractId": "0192f4ff-2222-7000-8000-000000000002",
    "customerId": "0192f4ff-3333-7000-8000-000000000003",
    "invoiceDate": "2026-09-30",
    "dueDate": "2026-10-30",
    "total": 48250.00,
    "amountPaid": 48250.00,
    "balance": 0,
    "currencyCode": null,
    "status": "Paid"
  }
}
```

## For developers adding events

Add the type to `Domain/Integrations/IntegrationEvents.cs` (`IntegrationEventType` and its data record, ids and summary
fields only), the same key to `Contracts/Integrations/IntegrationContracts.cs` (`WebhookEventCatalog`), a builder to
`IntegrationEventPayloads`, and raise it next to the audit row, before `SaveChangesAsync`:

```csharp
IntegrationOutbox.Raise(db, IntegrationEventType.InvoiceApproved, IntegrationEventPayloads.Invoice(invoice), audit.Actor);
await db.SaveChangesAsync(cancellationToken);
```

Then document it in the table above.
