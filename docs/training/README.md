# Pilot training

Material for training pilot users on Workbench, one hour per role. Everything is plain Markdown so it can be printed,
pasted into a slide deck or shared as is.

| Role | Who | Agenda | Quick-reference card |
| --- | --- | --- | --- |
| Employee | Everyone who records time | [Employee training: one hour](agenda-employee.md) | [Workbench quick reference: Employee](card-employee.md) |
| Manager | Supervisors and project managers | [Manager training: one hour](agenda-manager.md) | [Workbench quick reference: Manager](card-manager.md) |
| Finance | Accounting, billing, AP, payroll | [Finance training: one hour](agenda-finance.md) | [Workbench quick reference: Finance](card-finance.md) |
| Administrator | Whoever sets up users and the organization | [Administrator training: one hour](agenda-admin.md) | [Workbench quick reference: Administrator](card-admin.md) |

The hands-on parts of every agenda use [Exercise script (demo data)](exercise-script.md), which runs against the demo data.

## Preparing a training environment

1. Start Workbench in Development with a fresh database (delete `src/Workbench.Web/workbench.db` first), so every
   session starts from the same demo data:
   ```bash
   dotnet run --project src/Workbench.Web --launch-profile http
   ```
2. Trainees sign in at `/account/dev-signin` as one of the demo people:

   | Sign in as | Person | What they can do |
   | --- | --- | --- |
   | `analyst@example.com` | Alex Rivera, Business Analyst II | Employee; reports to Pat |
   | `viewer@example.com` | Val Chen, Administrative I | Employee, part-time (20 h), paid hourly; reports to Pat |
   | `pm@example.com` | Pat Morgan, Program Manager III | Finance, manager of project 1001, supervisor of Alex, Dana and Val |
   | `dev@workbench.local` | Dana Lee, Systems Engineer II | Administrator; reports to Pat |

3. The demo company has one contract, **W56KGU-26-C-0001 Enterprise Modernization Support** for the U.S. Army PEO EIS
   (CLIN 0001 cost-plus-fixed-fee, CLIN 0002 cost, no fee), and project **1001 Enterprise Modernization** with the tasks
   1001.01.01 Planning and kickoff, 1001.01.02 Requirements, 1001.01.03 Baseline schedule and 1001.01.04 Execution.
   Indirect and leave codes are under **IND** (Overhead, General and administrative, Bid and proposal) and **LV**
   (Paid time off, Holiday). The fiscal year runs October to September.
4. Several people can share one demo login in a classroom, but changes are shared too. For a large class, give each
   table its own copy of the server, or reset the database between sessions.

## Tips for trainers

- Start every session on the **Home** page: the to-do list is the thread the rest of the hour hangs on.
- Show the **?** button early. Most "how do I…" questions after go-live are answered by the help panel for the page.
- Let people take the welcome tour themselves on first sign-in; you can replay it from the help panel.
- End by having everyone send one piece of feedback with **Report a problem / Send feedback**, so they know the route
  for later and you collect impressions from the pilot.
