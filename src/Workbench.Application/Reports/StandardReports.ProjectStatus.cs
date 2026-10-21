using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Reports;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Reports;

public sealed partial class StandardReportService
{
    // Project status report: one block per project the user may read (its total, then its WBS in tree order).
    // Period actuals come from IBurdenedCostService for the date range; inception-to-date figures, ETC and EAC from the
    // project financials (direct) and burdened financials services; funded value from the CLINs the project is mapped to
    // (or the contract when it has none); billed from invoice lines on billed invoices.

    private async Task ProjectStatusAsync(ReportBuilder b, ReportAccessContext context, ReportParameters p, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(p, ReportDateRange.ThisMonth, capAtToday: false, ct);
        var basis = p.Basis ?? RateBasis.Provisional;
        b.Subtitle = $"Period {ReportDates.Describe(from, to)} · burdened at {ReportValues.Words(basis.ToString()).ToLowerInvariant()} rates for the period";
        b.Column("project", "Project").Column("wbs", "WBS").Column("name", "Name")
            .Column("budget", "Budget", ReportColumnType.Money)
            .Column("period_direct", "Period direct", ReportColumnType.Money)
            .Column("period_burdened", "Period burdened", ReportColumnType.Money)
            .Column("itd_hours", "ITD hours", ReportColumnType.Hours)
            .Column("itd_direct", "ITD direct", ReportColumnType.Money)
            .Column("itd_burdened", "ITD burdened", ReportColumnType.Money)
            .Column("etc", "ETC", ReportColumnType.Money)
            .Column("eac", "EAC", ReportColumnType.Money)
            .Column("eac_burdened", "EAC burdened", ReportColumnType.Money)
            .Column("variance", "Variance (budget − EAC)", ReportColumnType.Money)
            .Column("spent", "% spent", ReportColumnType.Percent)
            .Column("funded", "Funded", ReportColumnType.Money)
            .Column("billed", "Billed to date", ReportColumnType.Money);

        var projects = (await projectService.ListProjectsAsync(ct))
            .Where(x => context.CanReadProject(x.Id))
            .Where(x => p.ProjectId is { } id ? x.Id == id : x.Status != ProjectState.Closed)
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ToList();
        if (p.ProjectId is { } only && projects.Count == 0)
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the project's manager can see this project's status.");
        }

        var billed = await BilledByProjectAsync(projects.Select(x => x.Id).ToList(), ct);
        decimal tBudget = 0, tPeriodDirect = 0, tPeriodBurdened = 0, tHours = 0, tDirect = 0, tBurdened = 0, tEtc = 0, tEac = 0, tEacBurdened = 0, tFunded = 0, tBilled = 0;
        // Each project's figures come from four services (a hundred small queries): fetch a few projects at a time, then
        // build the rows in project order.
        var figures = new (IReadOnlyList<Contracts.Financials.WbsFinancialRowDto> Rows, BurdenedFinancialsDto Burdened, BurdenedCostResult Period, decimal? Funded)[projects.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, projects.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (i, token) =>
        {
            var id = projects[i].Id;
            figures[i] = (await financials.GetWbsFinancialsAsync(id, cancellationToken: token), await burdenedFinancials.GetProjectAsync(id, cancellationToken: token),
                await burdenedCost.GetProjectCostAsync(id, from, to, basis, token), await FundedValueAsync(projects[i], token));
        });

        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var (rows, burdened, period, funded) = figures[index];
            if (!burdened.HasRates)
            {
                b.Note("Some projects have no indirect rates for their fiscal years: burdened figures equal direct cost there.");
            }

            var burdenedById = burdened.Rows.ToDictionary(r => r.WbsElementId);
            var parentOf = rows.ToDictionary(r => r.WbsElementId, r => r.ParentId);
            var periodDirect = new Dictionary<Guid, decimal>();
            var periodBurdened = new Dictionary<Guid, decimal>();
            foreach (var line in period.Lines)
            {
                // Roll each charged element's period cost up to its ancestors.
                for (Guid? id = line.WbsElementId; id is { } current; id = parentOf.GetValueOrDefault(current))
                {
                    periodDirect[current] = periodDirect.GetValueOrDefault(current) + line.DirectLabor + line.DirectOdc;
                    periodBurdened[current] = periodBurdened.GetValueOrDefault(current) + line.TotalCost;
                    if (!parentOf.ContainsKey(current))
                    {
                        break;
                    }
                }
            }

            var t = burdened.Totals;
            var pDirect = period.Lines.Sum(l => l.DirectLabor + l.DirectOdc);
            var pBurdened = period.Lines.Sum(l => l.TotalCost);
            var summary = rows.Where(r => r.ParentId is null).Select(r => r.Figures).ToList();
            var budget = summary.Sum(f => f.BudgetAmount);
            var hours = summary.Sum(f => f.ActualHours);
            var direct = summary.Sum(f => f.ActualCost);
            var etc = summary.Sum(f => f.EtcAmount);
            var eac = summary.Sum(f => f.EacAmount);
            var projectBilled = billed.GetValueOrDefault(project.Id);
            b.Add(ReportRowKind.Subtotal, 0, DrillKey.Of(("project", project.Id)), $"projects/{project.Id}/financials",
                project.Code, null, project.Name, budget, pDirect, pBurdened, hours, direct, t.BurdenedActual, etc, eac, t.BurdenedEac, budget - eac,
                ReportValues.Ratio(direct, budget), funded, projectBilled);
            foreach (var r in rows)
            {
                var f = r.Figures;
                var bf = burdenedById.GetValueOrDefault(r.WbsElementId)?.Figures;
                b.Add(ReportRowKind.Detail, r.Depth + 1, DrillKey.Of(("wbs", r.WbsElementId)), null,
                    project.Code, r.Code, r.Name, f.BudgetAmount, periodDirect.GetValueOrDefault(r.WbsElementId), periodBurdened.GetValueOrDefault(r.WbsElementId),
                    f.ActualHours, f.ActualCost, bf?.BurdenedActual ?? f.ActualCost, f.EtcAmount, f.EacAmount, bf?.BurdenedEac ?? f.EacAmount, f.VacAmount,
                    f.PercentSpent, null, null);
            }

            tBudget += budget; tPeriodDirect += pDirect; tPeriodBurdened += pBurdened; tHours += hours; tDirect += direct; tBurdened += t.BurdenedActual;
            tEtc += etc; tEac += eac; tEacBurdened += t.BurdenedEac; tFunded += funded ?? 0; tBilled += projectBilled;
        }

        if (projects.Count > 1)
        {
            b.Add(ReportRowKind.Total, 0, null, null, "Total", null, $"{projects.Count} projects", tBudget, tPeriodDirect, tPeriodBurdened, tHours, tDirect, tBurdened,
                tEtc, tEac, tEacBurdened, tBudget - tEac, ReportValues.Ratio(tDirect, tBudget), tFunded, tBilled);
        }

        if (projects.Count == 0)
        {
            b.Note("No open projects you can see.");
        }

        b.Note("ITD = inception to date (approved time and allowable ODCs). Revenue appears with revenue recognition; billed is invoiced to date.");
    }

    /// <summary>Funded value of the CLINs the project's WBS is mapped to, else its contract's funded value; null without a contract.</summary>
    private async Task<decimal?> FundedValueAsync(ProjectDto project, CancellationToken ct)
    {
        if (project.ContractId is null)
        {
            return null;
        }

        try
        {
            var status = await funding.GetProjectFundingStatusAsync(project.Id, ct);
            if (status is { Clins.Count: > 0 })
            {
                return status.Clins.Sum(c => c.FundedValue);
            }

            return (await contractService.GetContractAsync(project.ContractId.Value, ct))?.FundedValue;
        }
        catch (WorkbenchForbiddenException)
        {
            return null;
        }
    }

    /// <summary>Invoiced amounts per project: lines of approved, sent and paid invoices, less credit memos.</summary>
    private async Task<Dictionary<Guid, decimal>> BilledByProjectAsync(List<Guid> projectIds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var billedStates = new[] { InvoiceStatus.Approved, InvoiceStatus.Sent, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid };
        var lines = await (from l in db.Set<InvoiceLine>().AsNoTracking()
                           join i in db.Set<Invoice>() on l.InvoiceId equals i.Id
                           where l.ProjectId != null && projectIds.Contains(l.ProjectId.Value) && billedStates.Contains(i.Status)
                           select new { ProjectId = l.ProjectId!.Value, l.Amount, i.Type })
            .ToListAsync(ct);
        return lines.GroupBy(l => l.ProjectId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Type == InvoiceType.CreditMemo ? -Math.Abs(l.Amount) : l.Amount));
    }

    /// <summary>Approved time and ODCs behind a project or WBS row for the report's period.</summary>
    private async Task ProjectStatusDrillAsync(ReportBuilder b, ReportAccessContext context, ReportParameters p, DrillKey key, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(p, ReportDateRange.ThisMonth, capAtToday: false, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        Guid projectId;
        HashSet<Guid> elements;
        if (key.Id("wbs") is { } wbsId)
        {
            var root = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wbsId, ct) ?? throw WorkbenchNotFoundException.For("WBS element", wbsId);
            projectId = root.ProjectId;
            var all = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).Select(w => new { w.Id, w.ParentId }).ToListAsync(ct);
            elements = [wbsId];
            for (var added = true; added;)
            {
                added = false;
                foreach (var w in all.Where(w => w.ParentId is { } parent && elements.Contains(parent) && !elements.Contains(w.Id)))
                {
                    added = elements.Add(w.Id) || added;
                }
            }

            b.Subtitle = $"{root.Code} {root.Name} · {ReportDates.Describe(from, to)}";
        }
        else
        {
            projectId = key.RequireId("project");
            elements = (await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).Select(w => w.Id).ToListAsync(ct)).ToHashSet();
            b.Subtitle = ReportDates.Describe(from, to);
        }

        if (!context.CanReadProject(projectId))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the project's manager can see this project's cost.");
        }

        await CostDetailAsync(db, b, context, elements, from, to, ct);
    }

    /// <summary>
    /// Approved hours and allowable ODCs on the given elements. Per-entry labor cost would reveal individual cost rates,
    /// so it's listed for Finance and administrators only.
    /// </summary>
    private static async Task CostDetailAsync(IWorkbenchDbContext db, ReportBuilder b, ReportAccessContext context, HashSet<Guid> elements, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ids = elements.ToList();
        var time = await (from e in db.Set<TimeEntry>().AsNoTracking()
                          join t in db.Set<Timesheet>() on e.TimesheetId equals t.Id
                          where ids.Contains(e.WbsElementId) && t.Status == TimesheetStatus.Approved && e.WorkDate >= @from && e.WorkDate <= to
                          select new { e.WorkDate, t.EmployeeId, t.Id, EntryId = e.Id, e.WbsElementId, e.Hours, e.Comment })
            .ToListAsync(ct);
        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => ids.Contains(o.WbsElementId) && o.Status == OdcStatus.Approved && !o.IsUnallowable && o.IncurredOn >= from && o.IncurredOn <= to)
            .ToListAsync(ct);
        var lookups = await TimeLookups.LoadAsync(db, time.Select(x => x.EmployeeId).Concat(odcs.Where(o => o.EmployeeId != null).Select(o => o.EmployeeId!.Value)),
            elements, context.SeesLaborCost, ct, time.Select(x => x.Id));

        b.Column("date", "Date", ReportColumnType.Date).Column("kind", "Kind").Column("wbs", "WBS").Column("who", "Employee / vendor")
            .Column("description", "Description").Column("hours", "Hours", ReportColumnType.Hours).Column("amount", "Amount", ReportColumnType.Money);
        foreach (var x in time.OrderBy(x => x.WorkDate))
        {
            b.Add(ReportRowKind.Detail, 0, null, null, x.WorkDate, "Labor", lookups.Wbs.GetValueOrDefault(x.WbsElementId)?.Code,
                lookups.Employees.GetValueOrDefault(x.EmployeeId)?.DisplayName, x.Comment, x.Hours, context.SeesLaborCost ? lookups.CostOf(x.EntryId, x.EmployeeId, x.WorkDate, x.Hours) : null);
        }

        foreach (var o in odcs.OrderBy(o => o.IncurredOn))
        {
            b.Add(ReportRowKind.Detail, 0, null, null, o.IncurredOn, $"ODC · {ReportValues.Words(o.Type.ToString()).ToLowerInvariant()}", lookups.Wbs.GetValueOrDefault(o.WbsElementId)?.Code,
                o.Vendor ?? (o.EmployeeId is { } e ? lookups.Employees.GetValueOrDefault(e)?.DisplayName : null), o.Description, null, o.Amount);
        }

        b.Add(ReportRowKind.Total, 0, null, null, null, "Total", null, null, null, time.Sum(x => x.Hours),
            context.SeesLaborCost ? time.Sum(x => lookups.CostOf(x.EntryId, x.EmployeeId, x.WorkDate, x.Hours) ?? 0m) + odcs.Sum(o => o.Amount) : odcs.Sum(o => o.Amount));
        if (!context.SeesLaborCost)
        {
            b.Note("Labor cost per entry is shown to Finance and administrators only; the total is ODCs only.");
        }
    }
}
