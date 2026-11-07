# Resourcing

<p class="roles">Resource managers see every project · Project managers see their own · Everyone sees My staffing</p>

**Projects › Resourcing** shows who is staffed where, week by week, against their capacity: a person's standard hours
less leave and holidays.

<figure markdown="span">
  ![The Resourcing heat map: people down the side, weeks across the top, each cell showing hours staffed of hours available, shaded from free to overbooked](../assets/screenshots/resourcing.png){ .shot }
  <figcaption>The heat map: each cell is hours staffed of hours available that week.</figcaption>
</figure>

| Tab | What it shows |
| --- | --- |
| **My staffing** | Your own assignments by week (everyone sees this, as **My work › My staffing**). |
| **Heat map** | Everyone (or one project's people) by week. Cells are shaded **Has room**, **Fully booked** or **Overbooked**; choose the start week, the number of weeks and the project. |
| **Project staffing** | Each project's assignments by person and labor category, against the budget. |
| **Utilization** | Billable and total hours against capacity, by person and org unit, for a period. |
| **Unstaffed budget & find people** | Budgeted hours nobody is staffed to yet, and people with the right labor category and free time to fill them. |

**Staff a person** adds an assignment: the person, project task, labor category, hours and dates. Workbench spreads the
hours over the working weeks. Assignments made on a project's [Budget & staffing tab](projects.md#budget-staffing) show
here too, and the other way around.

**Suggest who** is in the assign dialog, under the task. Pick the task (and, if you know them, the hours and dates), then
choose **Suggest who**. Workbench shows the best three people with a sentence on why: free hours in those dates, skills
the task's name mentions (a task called "SQL migration" favors people with the SQL skill), and the labor category. It
leaves out people who are fully booked in the window or already on the task. If the administrator has turned on the
[assistant](../admin-guide/assistant.md), it picks and explains from the top six; otherwise the rules alone decide. It is
advice only: **Choose** fills in the person, and the overbooking, budget and labor-category checks still run before you
save. Resource managers, administrators and the project's manager can ask.

Thresholds (what counts as full, how much non-billable time to allow, the staffing tolerance) are set by your
administrator: see [Configuration](../operations/configuration.md).
