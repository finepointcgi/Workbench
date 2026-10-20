# EDI sample files (ANSI X12 004010)

What Workbench sends and reads with vendors (Admin › Integrations › EDI). The tests in
`tests/Workbench.Tests/Purchasing/EdiTests.cs` apply these against a fresh database where `PO-000001` is an issued order
to ACME (four rugged laptops at 2,600) and ACME's EDI partner has interchange id `ZZ/ACMEFED`. Our interchange id is
`WORKBENCH` (`Edi:InterchangeId`).

| File | Direction | What it does in Workbench |
| --- | --- | --- |
| `850-purchase-order.x12` | out | The shape of the 850 Workbench writes when an order is issued: BEG, ITD net days, N1*ST, PO1 + PID + DTM*002 per line, CTT, AMT*TT. |
| `855-acknowledgement.x12` | in | BAK02 `AD` (accepted, no change) for PO-000001 with a promised date (ACK04 `068`, ACK05) and a note (MSG). |
| `856-ship-notice.x12` | in | Shipment `ACME-SHP-7781`: 2 of line 1 (LIN01 = PO line, SN102 = quantity), UPS tracking in REF*CN, ship and delivery dates. |
| `810-invoice.x12` | in | Invoice `ACME-INV-5521` for 2 of line 1 at 2,600 (TDS 520000 cents). Lands as a draft AP bill for the three-way match. |

Rules worth knowing: the vendor is identified by ISA05/ISA06 (its partner record), and ISA08 must be our id. A
transaction set already applied for that partner (same ISA13/ST02) is logged as a duplicate and not applied twice. Files
in a partner's `inbound` folder move to `inbound/processed` or `inbound/failed` once read.
