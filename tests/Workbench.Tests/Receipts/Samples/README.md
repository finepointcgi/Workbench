# Sample receipts and hotel folios

Made-up documents for the receipt reading tests (`ReceiptParserTests`, `ReceiptLibraryTests`, `ReceiptComponentTests`).
The PDFs were written with PdfPig's `PdfDocumentBuilder` (Helvetica, one text run per cell), so they have a real text layer
laid out in columns the way hotel folios are. Names, addresses and numbers are fictional.

| File | Layout | What the tests expect |
| --- | --- | --- |
| `hilton-arlington-folio.pdf` | `M/d/yyyy` dates on every row, Charges/Credits columns, Visa payment | Arlington, VA; Oct 12–15 2026 (3 nights) at $239.00; room $717.00, lodging taxes $116.52, parking $135.00, room service $38.50; total $1,007.02. Under the DC rate ($258). |
| `marriott-san-diego-folio.pdf` | `Oct 19, 2026` dates, `$` amounts, bracketed Amex payment, "Nights: 2" | San Diego, CA; 2 nights at $289.00; taxes $72.26 (occupancy + tourism district); valet $110.00; total $760.26. $84 a night over San Diego's $205. |
| `hampton-huntsville-folio.pdf` | `12-Oct-26` dates, rate changes on night 2, negative card payment | Huntsville, AL; $119.00 then $129.00; taxes $27.28; market $6.75; total $282.03. Night 2 is $8 over Huntsville's $121. |
| `yellow-cab-receipt.pdf` | A taxi receipt | Vendor "Yellow Cab of DC", date 10/14/2026, total $42.80. |
| `scanned-folio.pdf` | A page with a drawn box and no text layer | Nothing read (the assistant is the only way to read scans). |
| `parking-receipt-photo.png` | A blank PNG standing in for a phone photo | Nothing read unless the assistant is on. |
