# Project risks

Each project has a **Risks** tab: a register of what could go wrong, how likely it is, what it would cost, and who is
dealing with it. It is for the project's manager, Finance and administrators.

## Adding a risk

Choose **Add a risk** and fill in:

- **Risk** and **Details**: what could happen.
- **Probability (%)**, **Cost impact ($)** and **Schedule impact (days)**: if it happens, how much does it cost and how
  late does it make the project. As you type, the dialog shows the **expected cost** (probability times cost impact) and
  the expected delay.
- **Category**, **Status** and **Response**: Mitigate, Avoid, Transfer or Accept, with a **Response plan** and an
  **Owner**.
- **Review on**: a date to look at the risk again. A live risk past its date is marked overdue.
- **Part and change IDs** and **Requirement IDs**: free text, for the IDs of parts, changes and requirements you track
  in other systems. Workbench shows them with the risk and doesn't look them up.

Risks are numbered per project: R-1, R-2 and so on.

## What the totals mean

The strip at the top adds up the **live** risks: those that are Open, Mitigating or Accepted.

- **Expected cost** is the sum of each risk's probability times its cost impact. **If all happen** is the sum of the
  cost impacts.
- **Expected delay** is the same for schedule days.
- **Management reserve** comes from the project's earned-value baseline. When there is one, Workbench says whether the
  reserve covers the expected cost of the risks, or by how much it falls short. Without a baseline it reads "No
  baseline yet".

A risk that **Occurred** or was **Closed** stops counting. Closed risks are hidden unless you turn on **Show closed**; to
keep a risk for the record, set it to Closed rather than deleting it.
