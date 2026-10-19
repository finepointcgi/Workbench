using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Migration;

/// <summary>
/// What a comparison covers: the fiscal period, the date range of its amounts (<see cref="From"/>–<see cref="To"/>),
/// the aging date and whether projects are compared at the top level.
/// </summary>
public sealed record ReconScope(FiscalPeriodDto Period, FiscalPeriodDto FirstPeriod, DateOnly From, DateOnly To, DateOnly AsOf, bool ProjectLevel);

/// <summary>A drill-down's Workbench side: columns and rows (invariant strings) plus an optional note.</summary>
public sealed record ReconDetail(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? Note = null);

/// <summary>
/// Workbench's figures for each reconciled report, read the way Workbench's own reports read them (the trial balance,
/// AR and AP aging, burdened cost and rate calculation come from those services), plus the detail behind one key.
/// </summary>
public sealed class ReconciliationSources(IServiceProvider services, IWorkbenchDbContextFactory dbFactory)
{
    private const int MaxDetailRows = 2000;

    public Task<IReadOnlyList<WorkbenchFigure>> LoadAsync(ReconReportKind kind, ReconScope scope, CancellationToken ct) => kind switch
    {
        ReconReportKind.TrialBalance => TrialBalanceAsync(scope, ct),
        ReconReportKind.ProjectCost => ProjectCostAsync(scope, ct),
        ReconReportKind.LaborHours => LaborHoursAsync(scope, ct),
        ReconReportKind.ArAging => ArAgingAsync(scope, ct),
        ReconReportKind.ApAging => ApAgingAsync(scope, ct),
        ReconReportKind.RevenueByProject => RevenueAsync(scope, ct),
        ReconReportKind.IndirectRates => RatesAsync(scope, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public Task<ReconDetail> DetailAsync(ReconReportKind kind, ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct) => kind switch
    {
        ReconReportKind.TrialBalance => TrialBalanceDetailAsync(scope, matches, ct),
        ReconReportKind.ProjectCost => ProjectCostDetailAsync(scope, matches, ct),
        ReconReportKind.LaborHours => LaborDetailAsync(scope, matches, ct),
        ReconReportKind.ArAging => ArDetailAsync(scope, matches, ct),
        ReconReportKind.ApAging => ApDetailAsync(scope, matches, ct),
        ReconReportKind.RevenueByProject => RevenueDetailAsync(scope, matches, ct),
        ReconReportKind.IndirectRates => RatesDetailAsync(scope, matches, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- Trial balance ----

    private async Task<IReadOnlyList<WorkbenchFigure>> TrialBalanceAsync(ReconScope scope, CancellationToken ct)
    {
        var tb = await Get<IAccountingReportService>().GetTrialBalanceAsync(scope.Period.Id, ct);
        var accounts = (await Get<IChartOfAccountsService>().ListAccountsAsync(true, ct)).ToDictionary(a => a.Id);
        var figures = new List<WorkbenchFigure>();
        var rollups = new Dictionary<Guid, decimal[]>();
        foreach (var row in tb.Rows)
        {
            decimal[] values = [row.Ending, row.Opening, row.PeriodDebit, row.PeriodCredit];
            figures.Add(new WorkbenchFigure([row.AccountNumber], row.AccountName, Values(values)));

            // Summary accounts are compared only when the Costpoint report lists them.
            for (var parent = accounts.GetValueOrDefault(row.AccountId)?.ParentId; parent is { } p && accounts.ContainsKey(p); parent = accounts[p].ParentId)
            {
                var sum = rollups.TryGetValue(p, out var s) ? s : rollups[p] = new decimal[4];
                for (var i = 0; i < 4; i++)
                {
                    sum[i] += values[i];
                }
            }
        }

        figures.AddRange(rollups.Select(r => new WorkbenchFigure([accounts[r.Key].Number], accounts[r.Key].Name, Values(r.Value), OnlyIfInFile: true)));
        return figures;

        static Dictionary<string, decimal> Values(decimal[] v) => new() { ["Ending"] = v[0], ["Opening"] = v[1], ["PeriodDebit"] = v[2], ["PeriodCredit"] = v[3] };
    }

    private async Task<ReconDetail> TrialBalanceDetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct)
    {
        var accounts = await Get<IChartOfAccountsService>().ListAccountsAsync(true, ct);
        var account = accounts.FirstOrDefault(a => matches([a.Number]));
        if (account is null)
        {
            return new ReconDetail(["Account"], [], "Workbench has no account with this number.");
        }

        var ids = Descendants(accounts, account.Id);
        var report = Get<IAccountingReportService>();
        var rows = new List<IReadOnlyList<string>>();
        decimal opening = 0;
        foreach (var id in ids)
        {
            var ledger = await report.GetLedgerDetailAsync(new LedgerQuery(id, scope.Period.Start, scope.Period.End, Take: MaxDetailRows), ct);
            opening += ledger.OpeningBalance;
            rows.AddRange(ledger.Lines.Select(l => (IReadOnlyList<string>)[l.EntryNumber, Day(l.Date), l.Source.ToString(), l.AccountNumber, l.OrgUnitCode ?? "", l.ProjectCode ?? "",
                l.WbsCode ?? "", l.Description, Money(l.Debit), Money(l.Credit)]));
        }

        return new ReconDetail(["Entry", "Date", "Source", "Account", "Org", "Project", "WBS", "Description", "Debit", "Credit"],
            rows.OrderBy(r => r[1], StringComparer.Ordinal).ThenBy(r => r[0], StringComparer.Ordinal).ToList(),
            $"Ledger lines in {scope.Period.Name}{(ids.Count > 1 ? $" for {account.Number} and the {ids.Count - 1} account(s) under it" : "")}. Opening balance {Money(opening)}.");
    }

    private static List<Guid> Descendants(IReadOnlyList<AccountDto> accounts, Guid id)
    {
        var result = new List<Guid> { id };
        for (var i = 0; i < result.Count; i++)
        {
            result.AddRange(accounts.Where(a => a.ParentId == result[i]).Select(a => a.Id));
        }

        return result;
    }

    // ---- Project cost by element ----

    private async Task<List<(string Project, string ProjectName, BurdenedCostLine Line)>> BurdenedLinesAsync(ReconScope scope, CancellationToken ct)
    {
        var burdened = services.GetService<IBurdenedCostService>()
            ?? throw new ArgumentException("Burdened cost isn't available: the indirect rates module isn't registered.");
        var projects = await Get<IProjectService>().ListProjectsAsync(ct);
        var lines = new List<(string, string, BurdenedCostLine)>();
        foreach (var project in projects)
        {
            var result = await burdened.GetProjectCostAsync(project.Id, scope.From, scope.To, RateBasis.Provisional, ct);
            lines.AddRange(result.Lines.Select(l => (project.Code, project.Name, l)));
        }

        return lines;
    }

    private async Task<IReadOnlyList<WorkbenchFigure>> ProjectCostAsync(ReconScope scope, CancellationToken ct)
    {
        var lines = await BurdenedLinesAsync(scope, ct);
        var figures = new List<WorkbenchFigure>();
        foreach (var (project, name, line) in lines)
        {
            var values = new Dictionary<string, decimal>
            {
                ["DirectLabor"] = line.DirectLabor,
                ["Fringe"] = line.Fringe,
                ["Overhead"] = line.Overhead,
                ["GeneralAndAdministrative"] = line.GeneralAndAdministrative,
                ["DirectOdc"] = line.DirectOdc,
                ["Total"] = line.TotalCost,
            };
            if (scope.ProjectLevel)
            {
                figures.Add(new WorkbenchFigure([project], name, values));
                continue;
            }

            figures.Add(new WorkbenchFigure([line.WbsCode], null, values));
            foreach (var ancestor in Ancestors(line.WbsCode))
            {
                figures.Add(new WorkbenchFigure([ancestor], ancestor == project ? name : null, values, OnlyIfInFile: true));
            }
        }

        return figures;
    }

    private async Task<ReconDetail> ProjectCostDetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct)
    {
        var lines = (await BurdenedLinesAsync(scope, ct)).Where(l => matches([l.Line.WbsCode])).ToList();
        return new ReconDetail(["WBS", "Hours", "Direct labor", "Fringe", "Overhead", "G&A", "ODCs", "Total", "Unallowable (excluded)"],
            lines.OrderBy(l => l.Line.WbsCode, StringComparer.Ordinal).Select(l => (IReadOnlyList<string>)[l.Line.WbsCode, Money(l.Line.DirectLaborHours), Money(l.Line.DirectLabor),
                Money(l.Line.Fringe), Money(l.Line.Overhead), Money(l.Line.GeneralAndAdministrative), Money(l.Line.DirectOdc), Money(l.Line.TotalCost), Money(l.Line.UnallowableExcluded)]).ToList(),
            $"Approved cost {Day(scope.From)} to {Day(scope.To)} burdened at provisional rates.");
    }

    /// <summary>"2001.02.01" → "2001.02", "2001".</summary>
    private static IEnumerable<string> Ancestors(string code)
    {
        for (var dot = code.LastIndexOf('.'); dot > 0; dot = code.LastIndexOf('.', dot - 1))
        {
            yield return code[..dot];
        }
    }

    // ---- Labor distribution ----

    private sealed record LaborEntry(string EmployeeNumber, string EmployeeName, string ProjectCode, string WbsCode, string LaborCategory, DateOnly Date, decimal Hours, decimal Rate, decimal Cost);

    private async Task<List<LaborEntry>> LaborEntriesAsync(ReconScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var rows = await (from e in db.Set<TimeEntry>().AsNoTracking()
                          join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                          join emp in db.Set<Employee>().AsNoTracking() on t.EmployeeId equals emp.Id
                          join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                          join p in db.Set<Project>().AsNoTracking() on w.ProjectId equals p.Id
                          join l in db.Set<LaborCategory>().AsNoTracking() on e.LaborCategoryId equals l.Id
                          where t.Status == TimesheetStatus.Approved && e.WorkDate >= scope.From && e.WorkDate <= scope.To && e.Hours != 0
                          select new { emp.Id, emp.EmployeeNumber, emp.DisplayName, ProjectCode = p.Code, WbsCode = w.Code, Plc = l.Code, e.WorkDate, e.Hours, EntryId = e.Id, e.TimesheetId })
            .ToListAsync(ct);
        var book = await TotalTime.LaborCostBook.LoadAsync(db, rows.Select(r => r.TimesheetId), ct); // Labor cost by the labor cost method.
        return rows.Select(r => new LaborEntry(r.EmployeeNumber, r.DisplayName, r.ProjectCode, r.WbsCode, r.Plc, r.WorkDate, r.Hours,
            book.Rate(r.EntryId) ?? 0m, book.Cost(r.EntryId))).ToList();
    }

    private async Task<IReadOnlyList<WorkbenchFigure>> LaborHoursAsync(ReconScope scope, CancellationToken ct)
    {
        var figures = new List<WorkbenchFigure>();
        foreach (var e in await LaborEntriesAsync(scope, ct))
        {
            var values = new Dictionary<string, decimal> { ["Hours"] = e.Hours, ["LaborCost"] = e.Cost };
            if (scope.ProjectLevel)
            {
                figures.Add(new WorkbenchFigure([e.EmployeeNumber, e.ProjectCode], e.EmployeeName, values));
                continue;
            }

            figures.Add(new WorkbenchFigure([e.EmployeeNumber, e.WbsCode], e.EmployeeName, values));
            figures.AddRange(Ancestors(e.WbsCode).Select(a => new WorkbenchFigure([e.EmployeeNumber, a], e.EmployeeName, values, OnlyIfInFile: true)));
        }

        return figures;
    }

    private async Task<ReconDetail> LaborDetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct)
    {
        var entries = (await LaborEntriesAsync(scope, ct)).Where(e => matches([e.EmployeeNumber, e.WbsCode])).OrderBy(e => e.Date).ThenBy(e => e.WbsCode, StringComparer.Ordinal).ToList();
        return new ReconDetail(["Date", "Employee", "WBS", "PLC", "Hours", "Cost rate", "Labor cost"],
            entries.Take(MaxDetailRows).Select(e => (IReadOnlyList<string>)[Day(e.Date), $"{e.EmployeeNumber} {e.EmployeeName}", e.WbsCode, e.LaborCategory, Money(e.Hours),
                e.Rate.ToString("0.0000", CultureInfo.InvariantCulture), Money(e.Cost)]).ToList(),
            $"Approved time entries {Day(scope.From)} to {Day(scope.To)}; cost at the rate in force on each day.");
    }

    // ---- AR and AP aging ----

    private async Task<IReadOnlyList<WorkbenchFigure>> ArAgingAsync(ReconScope scope, CancellationToken ct)
    {
        var aging = await Get<IArReportService>().GetAgingAsync(scope.AsOf, ct);
        var codes = (await Get<IContractService>().ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.Code);
        return aging.Customers.Select(r => new WorkbenchFigure([codes.GetValueOrDefault(r.CustomerId) ?? r.CustomerName], r.CustomerName, new Dictionary<string, decimal>
        {
            ["Current"] = r.Buckets.Current,
            ["Days1To30"] = r.Buckets.Days1To30,
            ["Days31To60"] = r.Buckets.Days31To60,
            ["Days61To90"] = r.Buckets.Days61To90,
            ["Over90"] = r.Buckets.Over90,
            ["Total"] = r.Buckets.Current + r.Buckets.Days1To30 + r.Buckets.Days31To60 + r.Buckets.Days61To90 + r.Buckets.Over90,
        })).ToList();
    }

    private async Task<ReconDetail> ArDetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct)
    {
        var aging = await Get<IArReportService>().GetAgingAsync(scope.AsOf, ct);
        var codes = (await Get<IContractService>().ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.Code);
        var invoices = aging.Invoices.Where(i => matches([codes.GetValueOrDefault(i.CustomerId) ?? i.CustomerName])).ToList();
        return new ReconDetail(["Invoice", "Contract", "Invoice date", "Due", "Days past due", "Total", "Paid", "Credited", "Open"],
            invoices.Select(i => (IReadOnlyList<string>)[i.Number, i.ContractNumber, Day(i.InvoiceDate), Day(i.DueDate), i.DaysPastDue.ToString(CultureInfo.InvariantCulture),
                Money(i.Total), Money(i.AmountPaid), Money(i.AmountCredited), Money(i.Balance)]).ToList(),
            $"Open invoices as of {Day(scope.AsOf)}; days past due from the due date.");
    }

    private async Task<IReadOnlyList<WorkbenchFigure>> ApAgingAsync(ReconScope scope, CancellationToken ct)
    {
        var aging = await Get<IApReportService>().GetAgingAsync(scope.AsOf, ct);
        return aging.Rows.Select(r => new WorkbenchFigure([r.VendorCode], r.VendorName, new Dictionary<string, decimal>
        {
            ["Current"] = r.Current,
            ["Days1To30"] = r.Days1To30,
            ["Days31To60"] = r.Days31To60,
            ["Days61To90"] = r.Days61To90,
            ["Over90"] = r.Over90,
            ["Total"] = r.Total,
            ["Retainage"] = r.Retainage,
        })).ToList();
    }

    private async Task<ReconDetail> ApDetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vendors = await db.Set<Vendor>().AsNoTracking().ToListAsync(ct);
        var ids = vendors.Where(v => matches([v.Code])).Select(v => v.Id).ToList();
        var bills = await db.Set<ApBill>().AsNoTracking()
            .Where(b => ids.Contains(b.VendorId) && b.Status == ApBillStatus.Posted && b.InvoiceDate <= scope.AsOf).OrderBy(b => b.DueDate).ToListAsync(ct);
        return new ReconDetail(["Voucher", "Vendor invoice", "Invoice date", "Due", "Days past due", "Amount", "Retainage", "Paid", "Open"],
            bills.Where(b => b.Balance != 0).Select(b => (IReadOnlyList<string>)[b.Number, b.InvoiceNumber, Day(b.InvoiceDate), Day(b.DueDate),
                Math.Max(0, scope.AsOf.DayNumber - b.DueDate.DayNumber).ToString(CultureInfo.InvariantCulture), Money(b.Amount), Money(b.RetainageAmount), Money(b.PaidAmount), Money(b.Balance)]).ToList(),
            $"Posted bills open as of {Day(scope.AsOf)} (open = amount − retainage − paid).");
    }

    // ---- Revenue by project ----

    private sealed record RevenueLine(string Entry, DateOnly Date, string Account, string? ProjectCode, string? WbsCode, string Description, decimal Debit, decimal Credit);

    private async Task<List<RevenueLine>> RevenueLinesAsync(ReconScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await (from l in db.Set<JournalLine>().AsNoTracking()
                      join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                      join a in db.Set<Account>().AsNoTracking() on l.AccountId equals a.Id
                      join p in db.Set<Project>().AsNoTracking() on l.ProjectId equals p.Id into pj
                      from p in pj.DefaultIfEmpty()
                      join w in db.Set<WbsElement>().AsNoTracking() on l.WbsElementId equals w.Id into wj
                      from w in wj.DefaultIfEmpty()
                      where a.Type == AccountType.Revenue && e.Status != JournalStatus.Draft && e.Date >= scope.From && e.Date <= scope.To
                      select new RevenueLine(e.Number, e.Date, a.Number, p == null ? null : p.Code, w == null ? null : w.Code, e.Description, l.Debit, l.Credit))
            .ToListAsync(ct);
    }

    private const string NoProject = "(no project)";

    private async Task<IReadOnlyList<WorkbenchFigure>> RevenueAsync(ReconScope scope, CancellationToken ct)
    {
        var figures = new List<WorkbenchFigure>();
        foreach (var line in await RevenueLinesAsync(scope, ct))
        {
            var values = new Dictionary<string, decimal> { ["Revenue"] = line.Credit - line.Debit };
            var code = scope.ProjectLevel ? line.ProjectCode : line.WbsCode ?? line.ProjectCode;
            figures.Add(new WorkbenchFigure([code ?? NoProject], null, values));
            if (!scope.ProjectLevel && line.WbsCode is { } wbs)
            {
                figures.AddRange(Ancestors(wbs).Select(a => new WorkbenchFigure([a], null, values, OnlyIfInFile: true)));
            }
        }

        return figures;
    }

    private async Task<ReconDetail> RevenueDetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct)
    {
        var lines = (await RevenueLinesAsync(scope, ct)).Where(l => matches([l.WbsCode ?? l.ProjectCode ?? NoProject])).OrderBy(l => l.Date).ThenBy(l => l.Entry, StringComparer.Ordinal).ToList();
        return new ReconDetail(["Entry", "Date", "Account", "Project", "WBS", "Description", "Debit", "Credit"],
            lines.Take(MaxDetailRows).Select(l => (IReadOnlyList<string>)[l.Entry, Day(l.Date), l.Account, l.ProjectCode ?? "", l.WbsCode ?? "", l.Description, Money(l.Debit), Money(l.Credit)]).ToList(),
            $"Posted lines on revenue accounts {Day(scope.From)} to {Day(scope.To)}.");
    }

    // ---- Indirect rates ----

    private static string PoolName(CostPool pool) => pool switch
    {
        CostPool.Fringe => "Fringe",
        CostPool.Overhead => "Overhead",
        _ => "G&A",
    };

    private Task<RateCalculationDto> RateCalculationAsync(ReconScope scope, CancellationToken ct) =>
        Get<IIndirectRateService>().GetRateCalculationAsync(scope.From < scope.Period.Start ? scope.FirstPeriod.Id : scope.Period.Id, scope.Period.Id, ct);

    private async Task<IReadOnlyList<WorkbenchFigure>> RatesAsync(ReconScope scope, CancellationToken ct) =>
        (await RateCalculationAsync(scope, ct)).Pools.Select(p => new WorkbenchFigure([PoolName(p.Pool)], p.Name, new Dictionary<string, decimal>
        {
            ["PoolCost"] = p.PoolCost,
            ["Base"] = p.BaseAmount,
            ["Rate"] = p.ActualRate ?? 0m,
        })).ToList();

    private async Task<ReconDetail> RatesDetailAsync(ReconScope scope, Func<IReadOnlyList<string>, bool> matches, CancellationToken ct)
    {
        var calc = await RateCalculationAsync(scope, ct);
        var points = calc.Trend.Where(t => matches([PoolName(t.Pool)])).ToList();
        return new ReconDetail(["Period", "Pool cost", "Base", "Month rate", "YTD pool cost", "YTD base", "YTD rate", "Provisional"],
            points.Select(t => (IReadOnlyList<string>)[t.PeriodName, Money(t.PoolCost), Money(t.BaseAmount), Rate(t.MonthRate), Money(t.YtdPoolCost), Money(t.YtdBase), Rate(t.YtdRate), Rate(t.ProvisionalRate)]).ToList(),
            $"From GL balances, {calc.FromPeriodName} to {calc.ToPeriodName}. {string.Join(" ", calc.Notes)}".Trim());

        static string Rate(decimal? r) => r is { } v ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "";
    }
}
