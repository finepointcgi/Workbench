# CMMC assessment

**Admin → Compliance → CMMC assessment** is your own assessment against the 110 security requirements of NIST SP 800-171
Revision 2, the basis of CMMC Level 2. It is for people who manage compliance (administrators).

## Assessing a requirement

Each row is one requirement, with its family and its points. Choose the pencil to open it:

- **Status**: Implemented, Not implemented, Not applicable, or Not assessed (where every requirement starts).
- **How it's met**, or why it isn't or doesn't apply.
- **Where the evidence is**, an owner and the date you assessed it.
- For **3.5.3** (multifactor authentication) and **3.13.11** (FIPS-validated cryptography), a **Partly met** box: those two
  lose 3 points instead of 5 when partly met.

Filter by family or status to work through one area at a time.

## The score

The score follows the DoD *NIST SP 800-171 DoD Assessment Methodology* (version 1.2.1): start from 110 and subtract 5, 3 or
1 point for each requirement marked **Not implemented**. Requirements not yet assessed cost nothing yet, so the page also
shows the score if every unassessed one failed (the lowest possible is −203).

3.12.4, the system security plan, carries no points, but the methodology says that without one no assessment can be made.

## Plan of action and milestones

For every requirement that isn't met, add a **plan of action** item: what will be done, who owns it and when. The summary
counts gaps with no plan, and items past their due date. Mark an item Done when it is finished, then change the
requirement's status when the fix is in place.

## Keep controlled content out

A system security plan is usually controlled unclassified information (CUI). Write where the evidence lives (a section of
the plan, a ticket, a policy name), not the evidence itself, unless this Workbench deployment is authorized to hold CUI.
