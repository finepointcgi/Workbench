using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for budgets and financials on project 1001: a locked Baseline, a Current budget and a
/// Forecast (labor phased over 12 fiscal periods), plus approved timesheets and ODCs from the previous and the current
/// fiscal period, and a few that must not count (a submitted timesheet, an unallowable and a submitted ODC).
/// Runs once, after <see cref="ProjectAccountingSeed"/>; adds the previous fiscal year if the calendar starts later.
/// </summary>
internal static class FinancialsSeed
{
    private const string Actor = "seed@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var project = await db.Set<Project>().FirstOrDefaultAsync(p => p.Code == "1001", cancellationToken);
        if (project is null || await db.Set<BudgetVersion>().AnyAsync(v => v.ProjectId == project.Id, cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var periods = await EnsurePreviousFiscalYearAsync(db, today.AddMonths(-1), cancellationToken);
        var current = periods.LastOrDefault(p => p.Start <= today);
        var previous = current is null ? null : periods.LastOrDefault(p => p.End < current.Start);
        if (current is null || previous is null)
        {
            return;
        }

        var wbs = await db.Set<WbsElement>().Where(w => w.ProjectId == project.Id).ToDictionaryAsync(w => w.Code, cancellationToken);
        var employees = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        var categories = await db.Set<LaborCategory>().ToDictionaryAsync(c => c.Code, cancellationToken);
        if (!wbs.ContainsKey("1001.01.04") || !employees.ContainsKey("pm@example.com") || !categories.ContainsKey("SE2"))
        {
            return;
        }

        var pm = employees["pm@example.com"];
        var analyst = employees["analyst@example.com"];
        var engineer = employees["dev@workbench.local"];

        // ---- Budgets: Baseline (locked), Current (scope growth on execution) and a Forecast ----
        var costRates = await db.Set<EmployeeCostRate>().ToListAsync(cancellationToken);
        decimal RateFor(string category)
        {
            var catId = categories[category].Id;
            var rates = employees.Values.Where(e => e.DefaultLaborCategoryId == catId)
                .Select(e => costRates.Where(r => r.EmployeeId == e.Id && r.EffectiveFrom <= today).OrderBy(r => r.EffectiveFrom).LastOrDefault()?.HourlyCost)
                .OfType<decimal>().ToList();
            return rates.Count == 0 ? 0 : Math.Round(rates.Average(), 4);
        }

        var phasing = periods.Where(p => p.Start >= previous.Start).Take(12).ToList();
        (string Wbs, string Category, decimal Hours)[] baselineLabor =
        [
            ("1001.01.01", "PM3", 120), ("1001.01.02", "BA2", 480), ("1001.01.02", "PM3", 60), ("1001.01.03", "SE2", 240), ("1001.01.03", "PM3", 40),
            ("1001.01.04", "SE2", 2400), ("1001.01.04", "BA2", 720), ("1001.01.04", "PM3", 240),
        ];

        BudgetVersion Version(string name, BudgetKind kind, DateTime created, IEnumerable<(string Wbs, string Category, decimal Hours)> labor, decimal travel)
        {
            var version = new BudgetVersion { ProjectId = project.Id, Name = name, Kind = kind, CreatedAtUtc = created, CreatedBy = Actor };
            foreach (var (code, category, hours) in labor)
            {
                var rate = RateFor(category);
                var perPeriod = Math.Round(hours / phasing.Count, 2);
                for (var i = 0; i < phasing.Count; i++)
                {
                    var h = i == phasing.Count - 1 ? hours - perPeriod * (phasing.Count - 1) : perPeriod;
                    version.Lines.Add(new BudgetLine
                    {
                        BudgetVersionId = version.Id,
                        WbsElementId = wbs[code].Id,
                        CostType = BudgetCostType.Labor,
                        LaborCategoryId = categories[category].Id,
                        PeriodStart = phasing[i].Start,
                        Hours = h,
                        PlanningRate = rate,
                        Amount = Math.Round(h * rate, 2, MidpointRounding.AwayFromZero),
                    });
                }
            }

            version.Lines.Add(new BudgetLine { BudgetVersionId = version.Id, WbsElementId = wbs["1001.01.04"].Id, CostType = BudgetCostType.Odc, OdcType = OdcType.Travel, Amount = travel });
            version.Lines.Add(new BudgetLine { BudgetVersionId = version.Id, WbsElementId = wbs["1001.01.03"].Id, CostType = BudgetCostType.Odc, OdcType = OdcType.Materials, Amount = 2_500m });
            db.Add(version);
            db.Add(new AuditEvent { EntityType = nameof(BudgetVersion), EntityId = version.Id, Action = "Created", Actor = Actor, OccurredAtUtc = created, Reason = "Development seed" });
            return version;
        }

        var baseline = Version("Contract award baseline", BudgetKind.Baseline, now.AddDays(-45), baselineLabor, 12_000m);
        baseline.IsLocked = true;
        baseline.LockedAtUtc = now.AddDays(-44);
        baseline.LockedBy = "pm@example.com";
        db.Add(new AuditEvent { EntityType = nameof(BudgetVersion), EntityId = baseline.Id, Action = "Locked", Actor = "pm@example.com", OccurredAtUtc = now.AddDays(-44), Reason = "PMB approved at IBR" });

        var currentLabor = baselineLabor.Select(l => l.Wbs == "1001.01.04" && l.Category == "SE2" ? l with { Hours = 2640 } : l).ToList();
        var currentBudget = Version("Working budget", BudgetKind.Current, now.AddDays(-20), currentLabor, 15_000m);
        currentBudget.SourceVersionId = baseline.Id;
        var forecastLabor = currentLabor.Select(l => l.Wbs == "1001.01.02" && l.Category == "BA2" ? l with { Hours = 560 } : l).ToList();
        var forecast = Version("October estimate to complete", BudgetKind.Forecast, now.AddDays(-2), forecastLabor, 15_000m);
        forecast.SourceVersionId = currentBudget.Id;

        // ---- Actuals: weekly timesheets from the start of the previous period through last week ----
        var firstMonday = previous.Start.AddDays(((int)DayOfWeek.Monday - (int)previous.Start.DayOfWeek + 7) % 7);
        (Employee Who, (string Wbs, decimal Hours)[] Daily)[] plan =
        [
            (pm, [("1001.01.01", 2m), ("1001.01.03", 1m)]),
            (analyst, [("1001.01.02", 6m)]),
            (engineer, [("1001.01.03", 4m), ("1001.01.04", 3m)]),
        ];
        var lastWeek = Enumerable.Range(0, 52).Select(i => firstMonday.AddDays(7 * i)).Where(m => m.AddDays(6) < today).LastOrDefault();
        for (var monday = firstMonday; monday <= lastWeek; monday = monday.AddDays(7))
        {
            foreach (var (who, daily) in plan)
            {
                // The analyst's latest week is still awaiting approval, so it isn't an actual yet.
                var approved = !(who == analyst && monday == lastWeek);
                var sheet = new Timesheet
                {
                    EmployeeId = who.Id,
                    PeriodStart = monday,
                    PeriodEnd = monday.AddDays(6),
                    Status = approved ? TimesheetStatus.Approved : TimesheetStatus.Submitted,
                    CertifiedAtUtc = now,
                    SubmittedAtUtc = now,
                    ApprovedById = approved && who != pm ? pm.Id : null,
                    ApprovedAtUtc = approved ? now : null,
                };
                for (var d = 0; d < 5; d++)
                {
                    foreach (var (code, hours) in daily)
                    {
                        sheet.Entries.Add(new TimeEntry
                        {
                            TimesheetId = sheet.Id,
                            WbsElementId = wbs[code].Id,
                            LaborCategoryId = who.DefaultLaborCategoryId!.Value,
                            WorkDate = monday.AddDays(d),
                            Hours = hours,
                            EnteredAtUtc = now,
                        });
                    }
                }

                db.Add(sheet);
            }
        }

        OdcEntry Odc(string code, OdcType type, DateOnly on, decimal amount, string description, OdcStatus status, bool unallowable = false, string? vendor = null) => new()
        {
            ProjectId = project.Id,
            WbsElementId = wbs[code].Id,
            Type = type,
            IncurredOn = on,
            Amount = amount,
            Description = description,
            Vendor = vendor,
            EmployeeId = vendor is null ? pm.Id : null,
            IsUnallowable = unallowable,
            Status = status,
            SubmittedBy = "pm@example.com",
            CreatedAtUtc = now,
            ApprovedBy = status == OdcStatus.Approved ? "pm@example.com" : null,
            ApprovedAtUtc = status == OdcStatus.Approved ? now : null,
        };
        db.AddRange(
            Odc("1001.01.04", OdcType.Travel, previous.Start.AddDays(9), 1_842.37m, "Site survey trip to Fort Belvoir", OdcStatus.Approved),
            Odc("1001.01.03", OdcType.Materials, Min(current.Start.AddDays(3), today), 640.00m, "Scheduling software license", OdcStatus.Approved, vendor: "Primavera Reseller LLC"),
            Odc("1001.01.04", OdcType.Other, previous.Start.AddDays(10), 125.00m, "Team dinner (alcohol)", OdcStatus.Approved, unallowable: true),
            Odc("1001.01.04", OdcType.Subcontract, Min(current.Start.AddDays(5), today), 900.00m, "Subcontractor invoice 1", OdcStatus.Submitted, vendor: "Acme Federal"));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>The calendar ordered by start, adding the fiscal year before the earliest one when no period covers <paramref name="needed"/>.</summary>
    internal static async Task<List<FiscalPeriod>> EnsurePreviousFiscalYearAsync(WorkbenchDbContext db, DateOnly needed, CancellationToken cancellationToken)
    {
        var years = await db.Set<FiscalYear>().ToListAsync(cancellationToken);
        if (years.Count > 0 && !await db.Set<FiscalPeriod>().AnyAsync(p => p.Start <= needed && p.End >= needed, cancellationToken))
        {
            var first = years.MinBy(y => y.Start)!;
            var start = first.Start.AddYears(-1);
            var year = new FiscalYear { Year = first.Year - 1, Start = start, End = first.Start.AddDays(-1) };
            db.Add(year);
            db.AddRange(Enumerable.Range(0, 12).Select(i => new FiscalPeriod
            {
                FiscalYearId = year.Id,
                Number = i + 1,
                Name = $"{start.AddMonths(i):yyyy-MM}",
                Start = start.AddMonths(i),
                End = start.AddMonths(i + 1).AddDays(-1),
                Status = PeriodStatus.Closed,
            }));
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await db.Set<FiscalPeriod>().ToListAsync(cancellationToken)).OrderBy(p => p.Start).ToList();
    }
}
