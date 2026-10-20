# Sample bank statements

The same September 2026 statement for the operating account ending 4321 in each format that cash management imports
(Cash › Import statements). Opening balance 50,000.00, closing balance 63,078.12, six items: an AR receipt by EFT,
check 1001, a vendor ACH payment, an incoming wire, the monthly service fee and interest.

| File | Format | Notes |
| --- | --- | --- |
| `operating-2026-09.csv` | CSV | Separate debit and credit columns, `MM/dd/yyyy` dates. Mapping: Date = `Posting Date`, Description = `Description`, Debit = `Debit`, Credit = `Credit`, Check number = `Check #`, Reference = `Bank Ref`. |
| `operating-2026-09.qfx` | OFX 1.02 (SGML, Quicken QFX) | Account 123456784321, ledger balance 63,078.12. |
| `operating-2026-09.bai` | BAI2 | One group per posting day (the 02 as-of date dates its items); 03 summaries with 010 opening and 015 closing ledger balances, 88 continuations (summary fields and detail text) and six 16 detail records. |

Importing a second, overlapping statement skips the items already imported (same date, amount and bank reference).
The development seed imports a separate statement for the current month that matches the seeded AR payment, the AP
check run and one bank fee.

## Outgoing payment files

`nacha-ccd-sample.ach` is a known-good NACHA ACH file, laid out field by field from the NACHA record formats, that
`BankFileFormatTests` compares the writer against byte for byte: a file header (immediate destination ` 054001725`,
origin `1123456789`, created 2026-10-19 14:30, file ID modifier `A`), one CCD batch (service class 220, company
`WORKBENCH FED`, entry description `VENDOR PAY`, effective 2026-10-20, ODFI `05400172`), a $1,500.00 checking credit to
routing 021000021 with a CCD+ remittance addenda (`RMR*IV*INV-1001**1500.00\`), a $250.75 savings credit to routing
011000015, the batch and file controls (3 entries and addenda, entry hash 0003200003, credits 175075 cents, one block),
and three lines of 9s to fill the block of ten 94-character records.
