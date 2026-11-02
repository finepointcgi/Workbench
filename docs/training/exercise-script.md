# Exercise script (demo data)

Hands-on exercises for the pilot training agendas. They run against the demo data that a fresh Development database
starts with (see [Pilot training](README.md) for how to reset it and who to sign in as). Each exercise says who to sign in
as, the steps, and what trainees should see. Exercises build on each other within a role, so run them in order.

Dates in the demo data follow today's date, so the exact weeks you see will differ from the examples.

---

## Employee (sign in as `analyst@example.com`, Alex Rivera)

### E1. Take the tour and find your to-dos (3 min)

1. Sign in. The welcome tour starts on its own: step through it (Next, Next…, Done).
2. On **Home**, read the to-do list. Alex should see **Start your timesheet**, a draft expense report for a DC program
   review with receipts missing, and a purchase order to receive.
3. Select **?** at the top. The panel shows *Getting around Workbench*. Close it with Escape.

**Check:** everyone can say what's overdue for Alex and where the help button is.

### E2. Enter today's hours (4 min)

1. Select **Start your timesheet** on Home (or open **Time**).
2. On the row **Requirements (1001.01.02)**, type `6` in today's box.
3. Select **Add row**, choose **Bid and proposal (IND.BP)** and enter `2` for today.
4. Select **Save**.

**Check:** the day total is 8.0 and the timesheet is a **Draft**.

### E3. Certify and submit (4 min)

1. Fill the rest of the period's working days (8 h each, any mix of Requirements and Planning and kickoff).
2. Select **Certify and submit**, read the statement, tick **I certify this timesheet** and confirm.

**Check:** the banner reads **Submitted, waiting on Pat Morgan**. Discuss: what would the warning have said with fewer
than 40 hours?

### E4. Correct a submitted day (6 min)

1. Click into one of the submitted hours. Workbench offers to **Recall** the timesheet.
2. Reason: `Charged Monday to Requirements instead of Planning and kickoff`. Confirm.
3. Move Monday's hours to **Planning and kickoff (1001.01.01)**, **Save**, and **Certify and submit** again.
4. Open **History** for the timesheet to see the change and its reason.

**Check:** the history shows old hours, new hours, who, when and why.

### E5. Finish the expense report (8 min)

1. From Home, open the **DC program review** expense report.
2. Attach a receipt image (any small PNG or JPG) to each line flagged as missing one.
3. Select **Add per diem**, **Add a destination**: Washington, DC, for the trip dates; **Save and compute**, then
   **Update expense lines**.
4. Select **Submit for approval**.

**Check:** the report is submitted and the receipts to-do is gone from Home. Discuss: why don't per diem meal lines need
receipts?

### E6. Request a day off (4 min)

1. Open **Leave** and note the Paid time off balance.
2. Select **Request leave**: Paid time off, a day next month, 8 h, note `Family event`. **Send request**.

**Check:** the request shows as pending. Discuss: on the day itself, what goes on the timesheet? (8 h on Paid time off.)

### E7. Send feedback (2 min)

1. Open the account menu (top right) › **Report a problem / Send feedback**.
2. Choose **Send feedback or an idea**, write one sentence about today's session, and **Send**.
3. Open the account menu › **My support requests**.

**Check:** the request is listed with a reference such as SR-000001 and status **New**.

---

## Manager (sign in as `pm@example.com`, Pat Morgan)

Run E3 and E5 as Alex first (or have a trainer do it) so there is something to approve.

### M1. Clear the inbox (5 min)

1. On **Home**, look at **Waiting on you**. Alex's timesheet should be listed.
2. Open **Approvals** and select the timesheet. Review hours by day and the correction reason from E4.
3. Select **Approve**.

**Check:** the item disappears and the Approvals count goes down.

### M2. Return an expense report (4 min)

1. In **Approvals**, open Alex's expense report.
2. Select **Return** with the comment `Please add the taxi receipt from Oct 6`.

**Check:** sign in as Alex in another browser: the report is back in draft with Pat's comment.

### M3. Delegate before time off (3 min)

1. In **Approvals**, select **Delegate**. Choose Dana Lee, from tomorrow for one week.

**Check:** the delegation is listed. Discuss: what does the record show when Dana approves?

### M4. Read the project page (8 min)

1. Open **Projects** › **1001 Enterprise Modernization**.
2. **Overview**: what's the health, and why? How much funding is used?
3. **Plan**: open the Gantt view, find the critical path.
4. **Financials** and **Budgets**: compare spend to budget; find the EAC.
5. **Ceilings**: is any task close to its hours ceiling? (The demo data has one near 90%.)

**Check:** each table names one thing the PM should act on this month.

### M5. Explain a variance (5 min)

1. On **Home**, find a **variance explanation** to-do (VAR) for 1001 and open it.
2. Fill in cause, impact and corrective action in plain words, and submit.

**Check:** the VAR is no longer listed as due.

---

## Finance (sign in as `pm@example.com`, Pat Morgan, who has the Finance role)

### F1. Walk the close checklist (8 min)

1. Open **Finance › Accounting › Period close** and pick last month.
2. Read each check. Select **Post backlog** and watch the posting check update.
3. Don't close it yet: discuss which checks would stop you and where their links go.

**Check:** everyone can name the bank reconciliation and revenue run checks and find the pages that clear them.

### F2. Bill an invoice (8 min)

1. **Finance › Billing & AR › Invoices** › **New invoice**: contract W56KGU-26-C-0001, last month.
2. Select **Preview lines**. Find the labor lines, the fee on CLIN 0001 and the absence of fee on CLIN 0002.
3. Select **Approve and post**, then download the **SF 1034**.
4. **Customer payments** › **Record payment** for the full amount against the invoice.

**Check:** AR aging no longer shows the invoice. Discuss: how would you fix a wrong posted invoice? (Void or credit memo.)

### F3. Pay a vendor bill (6 min)

1. **Payables & purchasing › Bills** › **New bill** for vendor ACME, invoice `ACME-1001`, one line charged to
   1001.01.04 Execution. **Submit**, then approve it from **Approvals**.
2. **Payment runs** › **Create payment run** with today as the cutoff. Export the **ACH CSV**, then
   **Record payments and post**.

**Check:** the bill shows as paid.

### F4. Reconcile the bank (6 min)

1. **Cash › Import statements**: pick the operating account and import the sample statement your trainer provides
   (any CSV with date, description and amount columns that include the ACME payment from F3).
2. **Reconcile** › **Auto-match**. Match anything left by hand. Use **Post and match** for a bank fee.
3. **Complete** when the difference is zero.

**Check:** the reconciliation is in **Reconciliation history** and the close checklist's bank check passes.

---

## Administrator (sign in as `dev@workbench.local`, Dana Lee)

### A1. Invite a user (5 min)

1. **Admin › Users and security** › **Invites**: invite `newhire@example.com`, name `New Hire`, role Employee.
2. On the **Users** tab, find Val Chen. Note her roles and that she's linked to her employee record.

**Check:** the invitation is listed with its expiry. Discuss: what happens when someone leaves? (Deactivate.)

### A2. Add a reminder to the timesheet workflow (6 min)

1. **Admin › Approval workflows** › Timesheet.
2. On the supervisor step, set a reminder after 2 business days. **Publish new version**.

**Check:** version 2 is published. Discuss: what happens to timesheets already submitted? (They keep version 1.)

### A3. Dry-run an import (6 min)

1. **Admin › Import**: Costpoint, Employees. Upload `samples/import/costpoint/employees.csv` from the
   repository.
2. Map the columns, **Save mapping and validate**, then run a **dry run**.

**Check:** the dry run lists what would be created and changed, and nothing was saved. Discuss rollback.

### A4. Answer a support request (3 min)

1. **Admin › Support requests** (or Ctrl+K, "support requests"). Open the feedback sent in E7.
2. Set the status to **Resolved**, reply `Thanks! Noted for the pilot review.` and **Save**.

**Check:** sign in as Alex: **My support requests** shows the reply and the status, and Alex has a notification.
