---
title: Bill an invoice
summary: Create an invoice from approved time and costs, check it, post it and send it.
category: Finance
audience: finance
keywords: invoice billing ar cost plus t&m ffp milestone wawf sf 1034 payment
order: 41
---
## Create the invoice

1. Open **Finance › Billing & AR › Invoices** and select **New invoice**.
2. Pick the contract and the billing period. Workbench gathers what can be billed:
   - [T&M](term:tm): approved hours at each [labor category](term:labor-category)'s bill rate, plus materials.
   - [Cost-plus](term:cost-plus): allowable cost to date, [burdened](term:burdened) at provisional rates, plus [fee](term:fee), less what's already billed.
   - [FFP](term:ffp): completed milestones.
3. Select **Preview lines** to see them before anything is saved.

Hours and costs can only be billed once. [Unallowable](term:unallowable) costs are never included.

## Check it

- Totals stay within each [CLIN](term:clin)'s [funded](term:funded) amount and any [ceiling](term:ceiling).
- Add an adjustment if needed (**Add adjustment**) with a clear description.

## Post and send

1. Select **Approve and post**. The invoice posts to the general ledger.
2. Download it as **SF 1034** or a **WAWF CSV** for the government's invoicing system, and select **Mark sent**.

## When the customer pays

Open **Customer payments**, select **Record payment**, and apply it to the invoice. **AR aging** shows what's still outstanding; **Send overdue reminders** emails customers who are late.

To fix a posted invoice, **Void** it or issue a **Credit memo**; never edit it.
