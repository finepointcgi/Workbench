# Commercial billing

<p class="roles">Finance · project managers read</p>

How to bill a **commercial agreement**: a contract with no CLINs, no funding and no ceiling. It uses the same invoice
pages as government billing (draft, approve and post, send, record payment), so the general ledger, revenue recognition
and AR aging see commercial invoices like any other. Government contracts are untouched: nothing here changes how a
contract with CLINs bills.

## Set up an agreement

1. Create the contract as usual (customer, number, dates) with **no CLINs**, a project, and bill rates if it bills hours.
2. Open **Billing & AR › Contracts and milestones**, pick the contract, and select **Set up billing** on *How this agreement bills*.
3. Choose the model. A contract that has CLINs can't take one: it bills the government way.

The contract's type decides the revenue account the invoices post to (fixed fee, retainer and recurring use *firm fixed
price* revenue, 4040; time and materials uses 4020). Pick the type that matches how you report revenue.

## Billing models

| Model | What it bills | When |
|---|---|---|
| **Time and materials** | Approved hours at the contract's rate card, plus approved expenses at cost | When you create the invoice from **New invoice** or **Ready to bill** |
| **Fixed fee with milestones** | Each milestone's amount | When the milestone is marked complete |
| **Monthly retainer** | A fixed amount per period (monthly, quarterly, every six months or yearly) | Each period, from its start |
| **Recurring or subscription charge** | A fixed amount per period between a start and an end date (managed services) | Each period, from its start |

### Time and materials on a rate card

The same as T&M on a government CLIN, without the CLIN: every approved hour on the contract's projects bills at the
labor category's bill rate on the work date, and approved, allowable expenses bill at cost. Hours without a rate are
reported, not billed. There is **no funding or ceiling check**, because there are no CLINs to hold one.

### Fixed fee with milestones

Add the milestones (description, amount, due date). **Mark complete** on a milestone makes it billable and, unless you
turn that off in the model's settings, creates a **draft invoice** for it right away. Nothing is posted until you
approve the draft. A milestone is billed once; voiding its invoice frees it to be billed again. A milestone on an
invoice can't be edited, reopened or deleted.

### Monthly retainer

A fixed amount per period, counted from the start date you choose (a retainer starting on the 15th bills the 15th to the 14th).
If you set an end date, the last period is cut at that date and **prorated by days**.

**Unused hours** are off by default: the retainer is just the amount. Turn them on to track hours against the retainer:

- *Track hours; unused hours expire* — each period has its included hours; unused ones are lost.
- *Track hours; unused hours roll over* — unused hours carry into the next periods.

The invoice line says how many of the available hours were used. Hours over what is available bill at the **overage
rate** you set (if you leave it blank, they are reported but not billed). Hours come from approved timesheets on the
contract's projects.

### Recurring or subscription charges

Set the amount, cadence, start date and (optionally) end date, and the text to print ("Managed services").

### Generating each period's invoice

Retainer and recurring periods that have started and have no invoice are listed under *Periods ready to invoice* on the
agreement. **Create draft invoices** makes one draft per period. A background run does the same every few hours for
plans that have *Create each period's draft invoice automatically* on (the default), and tells Finance how many drafts
are ready. **Drafts are never approved automatically.** A period is billed once: asking for the same period again is
refused, and voiding an invoice makes its period due again.

## Payments and receivables

**Customer payments** records a receipt and applies it to one or more of the customer's open invoices (it posts debit
cash, credit AR billed, like any receipt).

- **Partial payments** leave the invoice *Partially paid* with the balance still open.
- **Overpayment as a credit.** Enter an *Amount received* larger than what you apply. The extra is kept as a **customer
  credit** and shown under *Customer credits*; **Apply** it to later invoices by entering amounts on those invoices. The
  receipt already credited AR, so applying a credit posts nothing new. Voiding the receipt removes the credit.
  Foreign-currency receipts must be applied in full.
- **Write-off.** On an open invoice, **Write off** takes an amount and a **reason** (required, audited) and posts
  debit *7040 bad debt expense* (an unallowable account), credit AR billed. A write-off that clears the balance marks
  the invoice *Paid*. **Reverse write-off** undoes it with a reason. An invoice with a write-off can't be voided until the
  write-off is reversed. Foreign-currency invoices can't be written off yet.

### A/R aging

**AR aging** shows each customer's open balance as current, 1–30, 31–60, 61–90 and 90+ days past due, by customer and
by contract, as of a date. Unapplied customer credits are shown beside it, with the **net receivable** (which equals the
GL's AR account).

### Payment reminders

Finance is notified (in the bell, and by email according to each person's notification preference) when an invoice is
**due soon** (3 days ahead by default; 0 turns it off under *Invoice settings*) and when it goes **overdue** (once for
each aging bucket). **Send payment reminders** on the aging page sends them now; otherwise they go out automatically.
Workbench does not email your customers or take card payments: remittance is by the details on the invoice.

## The invoice your customer sees

**Print** opens the **Commercial invoice** layout for agreements with a billing model (and for every invoice when you
choose it as the default). It has your company name and address, the customer, the agreement and period, the lines
(no CLINs, funding, SF 1034 or certification blocks), the totals, the **payment terms and due date**, and how to pay. Use
the toolbar to switch to the government layouts (*SF 1034 / SF 1035*, *Our invoice layout (CLINs)*) on any invoice;
**Print / save as PDF** saves it as a PDF from your browser.

Set what it says under **Billing & AR › Invoice settings**:

| Setting | Printed as |
|---|---|
| Company name on invoices, remit-to address, billing contact | The seller block (falls back to the `Billing` configuration) |
| How to pay | The *How to pay* box: ACH or wire details, lockbox, who checks are payable to |
| Payment terms text | *Terms* ("Net 30. Late balances accrue 1.5% a month.") |
| Default payment terms (days) | The due date, for customers and contracts that don't set their own |
| Invoice footer | Under the *How to pay* box |
| Remind Finance before due (days) | Not printed: when the due-soon reminder goes out |
| Invoice layout | Which layout opens by default on contracts without a billing model |

## Who can do what

Commercial billing uses the existing billing permissions. **Bill customers** sets up agreements, milestones and invoice
settings and generates invoices. **Record customer payments** records receipts, applies credits and writes off invoices.
**See receivables** reads the aging, write-offs and sends reminders. A project manager can read the billing model and
milestones of contracts their projects are on, and print their invoices; nothing else.

## Not included

- Card or online payment processing. Remittance is by the details on the invoice.
- Emailing invoices or reminders to your customers (reminders go to Finance).
- Gating by module: billing is available to every organization.
