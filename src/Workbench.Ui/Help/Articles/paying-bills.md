---
title: Pay vendor bills
summary: Enter a bill, match it to its purchase order, approve it and pay it in a payment run.
category: Finance
audience: finance
keywords: ap accounts payable bill vendor payment run ach check three way match po
order: 42
---
## Enter the bill

1. Open **Finance › Payables & purchasing › Bills** and select **New bill**, or open a purchase order and select **Create bill from PO**.
2. Enter the vendor, invoice number, dates and lines. A line from a purchase order takes its charge code from the PO.
3. Select **Submit**. It goes to the right approvers (often the project manager for project costs).

## Three-way match

For PO lines, Workbench checks the bill against the purchase order and what was received ([three-way match](term:three-way-match)). If the quantity or price doesn't agree, the bill appears under **Match exceptions** and can't be approved until it's fixed or Finance selects **Override** with a reason.

## Pay

1. Open **Payment runs** and select **Create payment run**. Pick the bank account and a due-date cutoff; approved bills due by then are included.
2. Review the list and remove anything you want to hold.
3. Export the **ACH CSV** (and **Positive pay CSV** if your bank uses it) or print checks.
4. Select **Record payments and post**.

Payments post to the general ledger and show on the bank reconciliation.
