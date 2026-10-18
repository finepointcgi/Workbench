using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Billing;
using Workbench.Application.Evm;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Collaboration;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Scheduling;

namespace Workbench.Tests.Evm;

/// <summary>
/// Project 1001 with a plan sheet ("Status" and "% complete" columns), four WBS leaves under 1001.01 and a locked Baseline
/// budget: A $10,000 (Oct), B $20,000 (Oct–Dec), C $30,000 (Nov–Jan), D $12,000 (Oct–Mar), plus $5,000 on the root.
/// Today is 2026-11-16, in fiscal period 2026-11. Actual cost is approved ODCs (direct cost, no indirect rates).
/// </summary>
internal sealed class EvmKit
{
    public static readonly DateOnly Today = new(2026, 11, 16);

    public IWorkbenchDbContextFactory Factory { get; } = TestDb.CreateFactory();
    public MutableClock Clock { get; } = new(new DateTimeOffset(Today.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero));
    public EvmService Evm { get; }
    public EvmChangeRequestService Changes { get; }
    public EvmExportService Exports { get; }

    public Project Project { get; private set; } = default!;
    public Dictionary<string, WbsElement> Wbs { get; } = [];
    public Dictionary<string, Item> Items { get; } = [];
    public Column Status { get; private set; } = default!;
    public Column Percent { get; private set; } = default!;
    public Dictionary<string, FiscalPeriod> Periods { get; private set; } = [];

    public EvmKit()
    {
        var audit = new AuditRecorder(new User(), Clock);
        var costs = new BillingCostSource(Array.Empty<IBurdenedCostService>());
        Evm = new EvmService(Factory, audit, costs, Clock);
        Changes = new EvmChangeRequestService(Factory, audit, Clock);
        Exports = new EvmExportService(Factory, costs, Clock);
    }

    public static async Task<EvmKit> CreateAsync()
    {
        var kit = new EvmKit();
        await kit.SeedAsync();
        return kit;
    }

    private async Task SeedAsync()
    {
        await using var db = await Factory.CreateAsync();
        var workspace = new Workspace { Name = "Program Office" };
        var sheet = new Sheet { Workspace = workspace, Name = "Plan", SchedulingEnabled = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        Status = new Column { Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Done"] };
        Percent = new Column { Name = "% complete", Type = ColumnType.Number, Order = 1 };
        sheet.Columns.AddRange([Status, Percent]);
        (string Code, DateOnly Start, DateOnly Finish)[] tasks =
        [
            ("A", new(2026, 10, 1), new(2026, 10, 30)),
            ("B", new(2026, 10, 1), new(2026, 12, 31)),
            ("C", new(2026, 11, 2), new(2027, 1, 29)),
            ("D", new(2026, 10, 1), new(2027, 3, 31)),
        ];
        var order = 0;
        foreach (var (code, start, finish) in tasks)
        {
            var item = new Item { Title = $"Task {code}", Order = order++, Start = start.AddDays(7), Finish = finish.AddDays(7) };
            sheet.Items.Add(item);
            Items[code] = item;
        }

        var c1 = new Item { Title = "C design review", ParentId = Items["C"].Id, Order = 0, Start = new(2026, 11, 2), Finish = new(2026, 11, 30) };
        var c2 = new Item { Title = "C delivery", ParentId = Items["C"].Id, Order = 1, Start = new(2026, 12, 1), Finish = new(2027, 1, 29) };
        sheet.Items.AddRange([c1, c2]);
        Items["C1"] = c1;
        Items["C2"] = c2;
        db.Workspaces.Add(workspace);
        db.Sheets.Add(sheet);

        var baseline = new Baseline { SheetId = sheet.Id, Name = "PMB schedule", CapturedAtUtc = DateTime.UtcNow };
        baseline.Entries.AddRange(tasks.Select(t => new BaselineEntry { ItemId = Items[t.Code].Id, Start = t.Start, Finish = t.Finish }));
        db.Set<Baseline>().Add(baseline);

        var pm = new Employee { EmployeeNumber = "E1", Email = "pm@example.com", DisplayName = "Pat", HireDate = new(2020, 1, 1) };
        db.Set<Employee>().Add(pm);
        Project = new Project { Code = "1001", Name = "Modernization", ProjectManagerId = pm.Id, SheetId = sheet.Id, Start = new(2026, 10, 1), Finish = new(2027, 3, 31) };
        db.Set<Project>().Add(Project);
        var root = new WbsElement { ProjectId = Project.Id, Code = "1001.01", Name = "Root", IsChargeable = false };
        Wbs["root"] = root;
        db.Set<WbsElement>().Add(root);
        foreach (var (code, _, _) in tasks)
        {
            var leaf = new WbsElement { ProjectId = Project.Id, ParentId = root.Id, Code = $"1001.01.{code}", Name = $"Work {code}", ItemId = Items[code].Id, Order = order++ };
            Wbs[code] = leaf;
            db.Set<WbsElement>().Add(leaf);
        }

        var year = new FiscalYear { Year = 2027, Start = new(2026, 10, 1), End = new(2027, 9, 30) };
        db.Set<FiscalYear>().Add(year);
        Periods = Enumerable.Range(0, 12).Select(i => new FiscalPeriod
        {
            FiscalYearId = year.Id, Number = i + 1, Name = $"{year.Start.AddMonths(i):yyyy-MM}", Start = year.Start.AddMonths(i), End = year.Start.AddMonths(i + 1).AddDays(-1),
        }).ToDictionary(p => p.Name);
        db.Set<FiscalPeriod>().AddRange(Periods.Values);

        var version = new BudgetVersion { ProjectId = Project.Id, Name = "Award baseline", Kind = BudgetKind.Baseline, IsLocked = true, CreatedBy = "seed", CreatedAtUtc = DateTime.UtcNow };
        foreach (var (code, amount) in new[] { ("A", 10_000m), ("B", 20_000m), ("C", 30_000m), ("D", 12_000m), ("root", 5_000m) })
        {
            version.Lines.Add(new BudgetLine { BudgetVersionId = version.Id, WbsElementId = Wbs[code].Id, CostType = BudgetCostType.Odc, OdcType = OdcType.Other, Amount = amount });
        }

        db.Set<BudgetVersion>().Add(version);
        await db.SaveChangesAsync();
    }

    public async Task CostAsync(string code, DateOnly on, decimal amount)
    {
        await using var db = await Factory.CreateAsync();
        db.Set<OdcEntry>().Add(new OdcEntry
        {
            ProjectId = Project.Id, WbsElementId = Wbs[code].Id, Type = OdcType.Other, IncurredOn = on, Amount = amount, Description = "Cost",
            Status = OdcStatus.Approved, SubmittedBy = "pm@example.com", CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task SetPlanAsync(string code, string? status = null, decimal? percent = null)
    {
        await using var db = await Factory.CreateAsync();
        var item = await db.Items.SingleAsync(i => i.Id == Items[code].Id);
        var values = new Dictionary<string, string?>(item.Values);
        if (status is not null)
        {
            values[Status.Id.ToString()] = status;
        }

        if (percent is { } p)
        {
            values[Percent.Id.ToString()] = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        item.Values = values;
        await db.SaveChangesAsync();
    }

    public sealed class User : ICurrentUser
    {
        public string Email => "finance@example.com";
    }
}

internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
