using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Application.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.TotalTime;

/// <summary>
/// Labor cost per time entry under the organization's labor cost method (<see cref="TotalTimeCosting"/>). Every module
/// that prices time (the GL posting, labor distribution, project financials, indirect rates, ICE, billing, reports,
/// payroll) reads entry costs from here so they agree to the cent. A whole timesheet is costed at once, because under
/// total time accounting one entry's cost depends on every hour on the sheet, indirect and leave included.
/// </summary>
public sealed class LaborCostBook
{
    private static readonly LaborCostBook EmptyBook = new([], [], []);

    private readonly Dictionary<Guid, CostedTimeEntry> _entries;

    private LaborCostBook(Dictionary<Guid, CostedTimeEntry> entries, Dictionary<Guid, SheetCost> sheets, Dictionary<Guid, Guid> sheetOfEntry)
    {
        _entries = entries;
        Sheets = sheets;
        SheetOfEntry = sheetOfEntry;
    }

    public static LaborCostBook Empty => EmptyBook;

    /// <summary>One timesheet costed, keyed by timesheet id.</summary>
    public IReadOnlyDictionary<Guid, SheetCost> Sheets { get; }

    public IReadOnlyDictionary<Guid, Guid> SheetOfEntry { get; }

    public sealed record SheetCost(Timesheet Sheet, Employee Employee, TimesheetCost Cost);

    public bool Contains(Guid entryId) => _entries.ContainsKey(entryId);

    public CostedTimeEntry? Entry(Guid entryId) => _entries.GetValueOrDefault(entryId);

    /// <summary>The entry's labor cost (0 for an unknown entry or one without a cost rate).</summary>
    public decimal Cost(Guid entryId) => _entries.TryGetValue(entryId, out var e) ? e.Cost : 0m;

    public decimal UncompensatedHours(Guid entryId) => _entries.TryGetValue(entryId, out var e) ? e.UncompensatedHours : 0m;

    public bool MissingRate(Guid entryId) => _entries.TryGetValue(entryId, out var e) && e.MissingRate;

    /// <summary>Cost ÷ hours for the entry (to 4 places): the hourly rate, or the effective rate under total time accounting.</summary>
    public decimal? Rate(Guid entryId) =>
        _entries.TryGetValue(entryId, out var e) && !e.MissingRate
            ? e.Hours != 0 && e.Cost != Money.Round(e.Hours * e.BaseRate!.Value) ? Money.RoundRate(e.Cost / e.Hours) : e.BaseRate
            : null;

    /// <summary>Costs every entry of the given timesheets (any status) by the method in effect on each period start.</summary>
    public static Task<LaborCostBook> LoadAsync(IWorkbenchDbContext db, IEnumerable<Guid> timesheetIds, CancellationToken ct) =>
        LoadAsync(db, timesheetIds, null, ct);

    /// <summary>As <see cref="LoadAsync(IWorkbenchDbContext, IEnumerable{Guid}, CancellationToken)"/>, with every sheet costed by <paramref name="method"/> instead.</summary>
    public static async Task<LaborCostBook> LoadAsync(IWorkbenchDbContext db, IEnumerable<Guid> timesheetIds, LaborCostMethod? method, CancellationToken ct)
    {
        var ids = timesheetIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return EmptyBook;
        }

        var sheets = new List<Timesheet>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
        {
            sheets.AddRange(await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).Where(t => chunk.Contains(t.Id)).ToListAsync(ct));
        }

        var employeeIds = sheets.Select(s => s.EmployeeId).Distinct().ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        var policies = method is null ? await LaborCostPolicies.LoadAsync(db, ct) : [];
        var entries = new Dictionary<Guid, CostedTimeEntry>();
        var costs = new Dictionary<Guid, SheetCost>();
        var sheetOf = new Dictionary<Guid, Guid>();
        foreach (var sheet in sheets)
        {
            if (!employees.TryGetValue(sheet.EmployeeId, out var employee))
            {
                continue;
            }

            var cost = Cost(sheet, employee, method ?? LaborCostPolicies.MethodOn(policies, sheet.PeriodStart), rates);
            costs[sheet.Id] = new SheetCost(sheet, employee, cost);
            foreach (var e in cost.Entries)
            {
                entries[e.EntryId] = e;
                sheetOf[e.EntryId] = sheet.Id;
            }
        }

        return new LaborCostBook(entries, costs, sheetOf);
    }

    /// <summary>Costs one timesheet (its entries must be loaded).</summary>
    public static TimesheetCost Cost(Timesheet sheet, Employee employee, LaborCostMethod method, EffectiveRateTable<Guid> rates) =>
        TotalTimeCosting.Cost(method, employee.IsExempt, TimekeepingClock.StandardHours(employee, sheet.PeriodStart, sheet.PeriodEnd),
            sheet.Entries.OrderBy(e => e.WorkDate).ThenBy(e => e.EnteredAtUtc).ThenBy(e => e.Id).Select(e => new TimeCostInput(e.Id, e.WorkDate, e.Hours)).ToList(),
            d => rates.Find(employee.Id, d));
}

/// <summary>The effective-dated labor cost method (<see cref="LaborCostPolicy"/>). No row means Standard.</summary>
public static class LaborCostPolicies
{
    public static async Task<List<LaborCostPolicy>> LoadAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        (await db.Set<LaborCostPolicy>().AsNoTracking().ToListAsync(ct)).OrderBy(p => p.EffectiveFrom).ThenBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).ToList();

    /// <summary>The method in effect on <paramref name="date"/> (a timesheet's period start). Policies must be ordered (see <see cref="LoadAsync"/>).</summary>
    public static LaborCostMethod MethodOn(IReadOnlyList<LaborCostPolicy> ordered, DateOnly date) =>
        ordered.LastOrDefault(p => p.EffectiveFrom <= date)?.Method ?? LaborCostMethod.Standard;

    public static async Task<LaborCostMethod> MethodOnAsync(IWorkbenchDbContext db, DateOnly date, CancellationToken ct) =>
        MethodOn(await LoadAsync(db, ct), date);
}
