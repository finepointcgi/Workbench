using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Identity;
using Workbench.Application.Odcs;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.PerDiem;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.PerDiem;

/// <summary>Computes a saved per diem against the tables in force. Shared by the service and the submit guard.</summary>
public sealed class TravelPerDiemComputer(IOptions<PerDiemOptions> options)
{
    public LodgingOveragePolicy Policy => options.Value.LodgingOveragePolicy;

    public async Task<IReadOnlyList<PerDiemDayDto>> ComputeAsync(IWorkbenchDbContext db, TravelPerDiem perDiem, CancellationToken ct)
    {
        var stops = Stops(perDiem);
        if (stops.Count == 0)
        {
            return [];
        }

        var tables = await PerDiemTables.LoadAsync(db, ct);
        var claims = perDiem.Days.ToDictionary(d => d.Date, d => new TravelDayInput(d.Date, d.LodgingClaimed, d.LodgingTaxClaimed, d.MieClaimed,
            d.BreakfastProvided, d.LunchProvided, d.DinnerProvided, d.Justification));
        return PerDiemCalculator.Compute(stops, claims, tables.Resolve, perDiem.LodgingTaxesSeparate, Policy, options.Value.JustifiedLodgingCapPercent);
    }

    public static IReadOnlyList<TravelDestinationDto> Stops(TravelPerDiem perDiem) =>
        perDiem.Destinations.OrderBy(d => d.Order)
            .Select(d => new TravelDestinationDto((PerDiemTable)(int)d.Kind, d.Country, d.State, d.Destination, d.ArriveOn, d.DepartOn))
            .ToList();

    public static async Task<TravelPerDiem?> LoadAsync(IWorkbenchDbContext db, Guid reportId, bool tracking, CancellationToken ct)
    {
        var query = db.Set<TravelPerDiem>().Include(p => p.Destinations).Include(p => p.Days).AsQueryable();
        return await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(p => p.ExpenseReportId == reportId, ct);
    }
}

/// <summary>
/// Per diem on travel expense reports. The traveler edits their own draft or rejected report; their supervisor (the
/// approver), Finance and Admin may read it. Generated lines go through <see cref="IExpenseReportService"/>, so the usual
/// expense rules (charge codes, open periods, receipts) apply to them.
/// </summary>
public sealed class TravelPerDiemService(
    IWorkbenchDbContextFactory dbFactory,
    TravelPerDiemComputer computer,
    IExpenseReportService reports,
    IProjectService projects,
    OdcWorkflow workflow,
    AccessControl access) : ITravelPerDiemService
{
    public async Task<TravelPerDiemDto> GetAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindReportAsync(db, expenseReportId, cancellationToken);
        await EnsureCanReadAsync(db, report, cancellationToken);
        return await ToDtoAsync(db, report, await TravelPerDiemComputer.LoadAsync(db, report.Id, false, cancellationToken), cancellationToken);
    }

    public async Task<TravelPerDiemDto> SaveAsync(Guid expenseReportId, SaveTravelPerDiemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableOwnReportAsync(db, expenseReportId, cancellationToken);
        var stops = request.Destinations.Select(d => d with
        {
            Country = string.IsNullOrWhiteSpace(d.Country) ? "US" : d.Country.Trim(),
            State = (d.State ?? "").Trim(),
            Destination = (d.Destination ?? "").Trim(),
        }).ToList();
        PerDiemCalculator.Validate(stops);
        foreach (var day in request.Days)
        {
            if (day.LodgingClaimed < 0 || day.LodgingTaxClaimed < 0 || day.MieClaimed < 0)
            {
                throw new ArgumentException($"{day.Date:yyyy-MM-dd}: amounts can't be negative.");
            }
        }

        var start = stops[0].ArriveOn;
        var codes = await projects.ListChargeCodesAsync(report.EmployeeId, start, cancellationToken);
        if (codes.All(c => c.WbsElementId != request.WbsElementId))
        {
            throw new ArgumentException($"Pick one of your charge codes on {start:yyyy-MM-dd} for the per diem lines.");
        }

        var perDiem = await TravelPerDiemComputer.LoadAsync(db, report.Id, true, cancellationToken);
        var now = workflow.UtcNow;
        if (perDiem is null)
        {
            perDiem = new TravelPerDiem { ExpenseReportId = report.Id, UpdatedBy = workflow.Actor, CreatedAtUtc = now };
            db.Set<TravelPerDiem>().Add(perDiem);
        }
        else
        {
            db.Set<TravelDestination>().RemoveRange(perDiem.Destinations);
            db.Set<TravelDay>().RemoveRange(perDiem.Days);
            perDiem.Destinations.Clear();
            perDiem.Days.Clear();
        }

        perDiem.WbsElementId = request.WbsElementId;
        perDiem.LodgingTaxesSeparate = request.LodgingTaxesSeparate;
        perDiem.UpdatedAtUtc = now;
        perDiem.AppliedAtUtc = null; // The lines no longer reflect the trip until they're regenerated.
        perDiem.UpdatedBy = workflow.Actor;
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            var destination = new TravelDestination
            {
                TravelPerDiemId = perDiem.Id, Order = i, Kind = (PerDiemRateKind)(int)s.Kind, Country = s.Country, State = s.State,
                Destination = s.Destination, ArriveOn = s.ArriveOn, DepartOn = s.DepartOn,
            };
            perDiem.Destinations.Add(destination);
            db.Set<TravelDestination>().Add(destination);
        }

        var end = stops[^1].DepartOn;
        foreach (var d in request.Days.Where(d => d.Date >= start && d.Date <= end).GroupBy(d => d.Date).Select(g => g.Last()))
        {
            var day = new TravelDay
            {
                TravelPerDiemId = perDiem.Id, Date = d.Date, LodgingClaimed = d.LodgingClaimed, LodgingTaxClaimed = d.LodgingTaxClaimed, MieClaimed = d.MieClaimed,
                BreakfastProvided = d.BreakfastProvided, LunchProvided = d.LunchProvided, DinnerProvided = d.DinnerProvided,
                Justification = string.IsNullOrWhiteSpace(d.Justification) ? null : d.Justification.Trim()[..Math.Min(d.Justification.Trim().Length, 1000)],
            };
            perDiem.Days.Add(day);
            db.Set<TravelDay>().Add(day);
        }

        workflow.Audit.Record(db, nameof(TravelPerDiem), perDiem.Id, "Saved", after: new
        {
            report.Id,
            Destinations = stops.Select(s => $"{s.Destination} {s.ArriveOn:yyyy-MM-dd}–{s.DepartOn:yyyy-MM-dd}"),
            perDiem.LodgingTaxesSeparate,
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, report, perDiem, cancellationToken);
    }

    public async Task<TravelPerDiemDto> ApplyToReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        TravelPerDiem perDiem;
        IReadOnlyList<PerDiemDayDto> days;
        List<PerDiemLine> previous;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            await FindEditableOwnReportAsync(db, expenseReportId, cancellationToken);
            perDiem = await TravelPerDiemComputer.LoadAsync(db, expenseReportId, false, cancellationToken)
                ?? throw new ArgumentException("Enter the trip's destinations first.");
            days = await computer.ComputeAsync(db, perDiem, cancellationToken);
            previous = await db.Set<PerDiemLine>().AsNoTracking().Where(l => l.TravelPerDiemId == perDiem.Id).ToListAsync(cancellationToken);
        }

        await RemoveLinesAsync(expenseReportId, previous, cancellationToken);
        var created = new List<PerDiemLine>();
        foreach (var group in Stays(days))
        {
            var first = group[0];
            var last = group[^1];
            var nights = group.Count(d => d.HasLodging);
            var span = first.Date == last.Date ? $"{first.Date:MMM d}" : $"{first.Date:MMM d}–{last.Date:MMM d}";
            async Task AddAsync(PerDiemLineKind kind, decimal amount, string description, bool unallowable = false, string? reason = null)
            {
                if (amount <= 0)
                {
                    return;
                }

                var line = await reports.AddLineAsync(expenseReportId,
                    new SaveExpenseLineRequest(perDiem.WbsElementId, OdcCostType.Travel, first.Date, amount, Trim(description), null, unallowable, reason), cancellationToken);
                created.Add(new PerDiemLine
                {
                    TravelPerDiemId = perDiem.Id, ExpenseReportId = expenseReportId, OdcEntryId = line.Id, Kind = kind,
                    FirstDate = first.Date, LastDate = last.Date, Amount = amount,
                });
            }

            await AddAsync(PerDiemLineKind.Lodging, group.Sum(d => d.LodgingAllowable),
                $"Lodging · {first.Location} · {nights} night{(nights == 1 ? "" : "s")} {span} (max {first.LodgingMax:C}/night)");
            await AddAsync(PerDiemLineKind.LodgingTax, group.Sum(d => d.LodgingTaxAllowable), $"Lodging taxes · {first.Location} · {span}");
            await AddAsync(PerDiemLineKind.LodgingJustified, group.Sum(d => d.LodgingJustified),
                $"Lodging above the per diem maximum (justified) · {first.Location} · {span}");
            var deducted = group.Any(d => d.MealDeduction > 0) ? "; provided meals deducted" : "";
            var partial = group.Any(d => d.MieFactor < 1) ? "; 75% on travel days" : "";
            await AddAsync(PerDiemLineKind.Mie, group.Sum(d => d.MieAllowable),
                $"M&IE per diem · {first.Location} · {group.Count} day{(group.Count == 1 ? "" : "s")} {span} ({first.MieRate:C}/day{partial}{deducted})");
            var pending = group.Any(d => d.LodgingStatus == PerDiemAmountStatus.RequiresJustification);
            await AddAsync(PerDiemLineKind.Unallowable, group.Sum(d => d.LodgingUnallowable + d.MieUnallowable),
                $"Claimed above the per diem allowance · {first.Location} · {span}", true,
                pending ? "Lodging above the per diem maximum without a justification (FAR 31.205-46)" : "Above the per diem allowance (FAR 31.205-46)");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var tracked = await db.Set<TravelPerDiem>().FirstAsync(p => p.Id == perDiem.Id, cancellationToken);
            tracked.AppliedAtUtc = workflow.UtcNow;

            db.Set<PerDiemLine>().AddRange(created);
            workflow.Audit.Record(db, nameof(TravelPerDiem), perDiem.Id, "Applied", after: new { Lines = created.Count, Total = created.Sum(l => l.Amount) });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(expenseReportId, cancellationToken);
    }

    public async Task RemoveAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        List<PerDiemLine> lines;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            await FindEditableOwnReportAsync(db, expenseReportId, cancellationToken);
            lines = await db.Set<PerDiemLine>().AsNoTracking().Where(l => l.ExpenseReportId == expenseReportId).ToListAsync(cancellationToken);
        }

        await RemoveLinesAsync(expenseReportId, lines, cancellationToken);
        await using var write = await dbFactory.CreateAsync(cancellationToken);
        var perDiem = await TravelPerDiemComputer.LoadAsync(write, expenseReportId, true, cancellationToken);
        if (perDiem is not null)
        {
            write.Set<TravelPerDiem>().Remove(perDiem);
            workflow.Audit.Record(write, nameof(TravelPerDiem), perDiem.Id, "Removed");
            await write.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RemoveLinesAsync(Guid reportId, IReadOnlyList<PerDiemLine> lines, CancellationToken ct)
    {
        foreach (var line in lines)
        {
            try
            {
                await reports.RemoveLineAsync(reportId, line.OdcEntryId, ct);
            }
            catch (WorkbenchNotFoundException)
            {
                // Already removed by hand.
            }
        }

        if (lines.Count > 0)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var ids = lines.Select(l => l.Id).ToList();
            db.Set<PerDiemLine>().RemoveRange(await db.Set<PerDiemLine>().Where(l => ids.Contains(l.Id)).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Consecutive days at the same location and rate, one group per stay.</summary>
    private static List<List<PerDiemDayDto>> Stays(IReadOnlyList<PerDiemDayDto> days)
    {
        var groups = new List<List<PerDiemDayDto>>();
        foreach (var day in days)
        {
            if (groups.Count > 0 && groups[^1][^1].Location == day.Location && groups[^1][^1].LodgingMax == day.LodgingMax && groups[^1][^1].MieRate == day.MieRate)
            {
                groups[^1].Add(day);
            }
            else
            {
                groups.Add([day]);
            }
        }

        return groups;
    }

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500];

    private async Task<TravelPerDiemDto> ToDtoAsync(IWorkbenchDbContext db, ExpenseReport report, TravelPerDiem? perDiem, CancellationToken ct)
    {
        if (perDiem is null)
        {
            return new TravelPerDiemDto(report.Id, false, null, null, true, computer.Policy, [], [], PerDiemCalculator.Totals([]), null, false, 0, []);
        }

        var days = await computer.ComputeAsync(db, perDiem, ct);
        var wbsCode = await db.Set<WbsElement>().AsNoTracking().Where(w => w.Id == perDiem.WbsElementId).Select(w => w.Code).FirstOrDefaultAsync(ct);
        var notes = new List<string>();
        var pending = days.Count(d => d.LodgingStatus == PerDiemAmountStatus.RequiresJustification);
        if (pending > 0)
        {
            notes.Add($"{pending} night{(pending == 1 ? "" : "s")} of lodging above the maximum need{(pending == 1 ? "s" : "")} a justification before the report can be submitted.");
        }

        var outOfDate = perDiem.AppliedAtUtc is null;
        return new TravelPerDiemDto(report.Id, true, perDiem.WbsElementId, wbsCode, perDiem.LodgingTaxesSeparate, computer.Policy,
            TravelPerDiemComputer.Stops(perDiem), days, PerDiemCalculator.Totals(days), perDiem.AppliedAtUtc, outOfDate, pending, notes);
    }

    private static async Task<ExpenseReport> FindReportAsync(IWorkbenchDbContext db, Guid reportId, CancellationToken ct) =>
        await db.Set<ExpenseReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw WorkbenchNotFoundException.For("Expense report", reportId);

    private async Task<ExpenseReport> FindEditableOwnReportAsync(IWorkbenchDbContext db, Guid reportId, CancellationToken ct)
    {
        var report = await FindReportAsync(db, reportId, ct);
        var me = await workflow.RequireCurrentEmployeeAsync(db, ct);
        if (report.EmployeeId != me.Id)
        {
            throw new WorkbenchForbiddenException("Only the traveler can change the per diem on their expense report.");
        }

        OdcWorkflow.EnsureEditable(report.Status, "expense report");
        return report;
    }

    private async Task EnsureCanReadAsync(IWorkbenchDbContext db, ExpenseReport report, CancellationToken ct)
    {
        var me = await workflow.FindCurrentEmployeeAsync(db, ct);
        if (me is not null && (report.EmployeeId == me.Id || (await OdcWorkflow.ResolveReportApproverAsync(db, report, ct))?.Id == me.Id))
        {
            return;
        }

        if (!(await access.GetOrgRolesAsync(ct)).IsFinance())
        {
            throw new WorkbenchForbiddenException("Only the traveler, their supervisor, Finance and administrators can see this per diem.");
        }
    }
}

/// <summary>
/// Blocks submitting an expense report whose per diem has lodging above the maximum without a justification, or whose
/// lines weren't regenerated after the trip changed. Reports without a per diem pass straight through.
/// </summary>
public sealed class PerDiemSubmitGuard(IExpenseReportService inner, IWorkbenchDbContextFactory dbFactory, TravelPerDiemComputer computer) : IExpenseReportService
{
    public async Task<ExpenseReportDto> SubmitAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await TravelPerDiemComputer.LoadAsync(db, reportId, false, cancellationToken) is { } perDiem && perDiem.Destinations.Count > 0)
            {
                var days = await computer.ComputeAsync(db, perDiem, cancellationToken);
                var pending = days.Count(d => d.LodgingStatus == PerDiemAmountStatus.RequiresJustification);
                if (pending > 0)
                {
                    throw new ArgumentException($"{pending} night{(pending == 1 ? "" : "s")} of lodging above the per diem maximum need{(pending == 1 ? "s" : "")} a justification.");
                }

                if (perDiem.AppliedAtUtc is null)
                {
                    throw new ArgumentException("The trip changed after the per diem lines were generated: choose \"Update expense lines\" first.");
                }
            }
        }

        return await inner.SubmitAsync(reportId, cancellationToken);
    }

    public Task<IReadOnlyList<ExpenseReportDto>> ListMyReportsAsync(CancellationToken cancellationToken = default) => inner.ListMyReportsAsync(cancellationToken);

    public Task<ExpenseReportDetailDto> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default) => inner.GetReportAsync(reportId, cancellationToken);

    public Task<ExpenseReportDto> CreateReportAsync(SaveExpenseReportRequest request, CancellationToken cancellationToken = default) => inner.CreateReportAsync(request, cancellationToken);

    public Task<ExpenseReportDto> UpdateReportAsync(Guid reportId, SaveExpenseReportRequest request, CancellationToken cancellationToken = default) =>
        inner.UpdateReportAsync(reportId, request, cancellationToken);

    public Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default) => inner.DeleteReportAsync(reportId, cancellationToken);

    public Task<OdcEntryDto> AddLineAsync(Guid reportId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default) =>
        inner.AddLineAsync(reportId, request, cancellationToken);

    public Task<OdcEntryDto> UpdateLineAsync(Guid reportId, Guid lineId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default) =>
        inner.UpdateLineAsync(reportId, lineId, request, cancellationToken);

    public Task RemoveLineAsync(Guid reportId, Guid lineId, CancellationToken cancellationToken = default) => inner.RemoveLineAsync(reportId, lineId, cancellationToken);

    public Task<OdcEntryDto> AttachReceiptAsync(Guid reportId, Guid lineId, string fileName, Stream content, CancellationToken cancellationToken = default) =>
        inner.AttachReceiptAsync(reportId, lineId, fileName, content, cancellationToken);
}

/// <summary>Per diem lines that need no receipt of their own (M&amp;IE, taxes and overage; the lodging line carries the folio).</summary>
public static class PerDiemReceipts
{
    public static Task<bool> IsExemptAsync(IWorkbenchDbContext db, Guid odcEntryId, CancellationToken ct) =>
        db.Set<PerDiemLine>().AsNoTracking().AnyAsync(l => l.OdcEntryId == odcEntryId && l.Kind != PerDiemLineKind.Lodging, ct);
}
