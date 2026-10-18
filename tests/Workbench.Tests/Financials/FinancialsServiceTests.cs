using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Financials;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Financials;

public sealed class FinancialsServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);
    private readonly IWorkbenchDbContextFactory _factory = TestDb.CreateFactory();
    private readonly BudgetService _budgets;
    private readonly ProjectFinancialsService _financials;
    private readonly FinancialExportService _exports;

    public FinancialsServiceTests()
    {
        var clock = new FixedClock(new DateTimeOffset(Today.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero));
        var audit = new AuditRecorder(new FinanceUser(), clock);
        _budgets = new BudgetService(_factory, audit, clock);
        _financials = new ProjectFinancialsService(_factory, clock);
        _exports = new FinancialExportService(_financials, clock);
    }

    private sealed record Setup(Project Project, Contract Contract, WbsElement Root, WbsElement Leaf, WbsElement Leaf2, Employee Alex, LaborCategory Category);

    private async Task<Setup> SeedAsync(ContractType type = ContractType.CostPlusFixedFee, Guid? sheetId = null)
    {
        await using var db = await _factory.CreateAsync();
        var customer = new Customer { Code = "ARMY", Name = "Army" };
        var contract = new Contract
        {
            CustomerId = customer.Id, ContractNumber = "W1", Name = "Support", Type = type,
            PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2028, 9, 30), CeilingValue = 100_000, FundedValue = 10_000,
        };
        var category = new LaborCategory { Code = "BA2", Name = "Analyst" };
        var alex = new Employee { EmployeeNumber = "E1", Email = "alex@example.com", DisplayName = "Alex", DefaultLaborCategoryId = category.Id, HireDate = new DateOnly(2020, 1, 1) };
        var project = new Project { Code = "1001", Name = "Modernization", ContractId = contract.Id, Start = new DateOnly(2026, 9, 1), Finish = new DateOnly(2026, 12, 31), SheetId = sheetId };
        var root = new WbsElement { ProjectId = project.Id, Code = "1001.01", Name = "Root", IsChargeable = false };
        var leaf = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = "1001.01.01", Name = "Planning", Order = 0 };
        var leaf2 = new WbsElement { ProjectId = project.Id, ParentId = root.Id, Code = "1001.01.02", Name = "Requirements", Order = 1 };
        var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
        var prior = new FiscalYear { Year = 2026, Start = new DateOnly(2025, 10, 1), End = new DateOnly(2026, 9, 30) };
        db.Set<Customer>().Add(customer);
        db.Set<Contract>().Add(contract);
        db.Set<ContractLaborRate>().Add(new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = category.Id, EffectiveFrom = new DateOnly(2026, 1, 1), HourlyBillRate = 120m });
        db.Set<LaborCategory>().Add(category);
        db.Set<Employee>().Add(alex);
        db.Set<EmployeeCostRate>().AddRange(
            new EmployeeCostRate { EmployeeId = alex.Id, EffectiveFrom = new DateOnly(2026, 1, 1), HourlyCost = 50m },
            new EmployeeCostRate { EmployeeId = alex.Id, EffectiveFrom = new DateOnly(2026, 10, 8), HourlyCost = 60m });
        db.Set<Project>().Add(project);
        db.Set<WbsElement>().AddRange(root, leaf, leaf2);
        db.Set<ChargeAssignment>().Add(new ChargeAssignment { WbsElementId = leaf.Id, EmployeeId = alex.Id });
        db.Set<FiscalYear>().AddRange(year, prior);
        foreach (var y in new[] { prior, year })
        {
            db.Set<FiscalPeriod>().AddRange(Enumerable.Range(0, 12).Select(i => new FiscalPeriod
            {
                FiscalYearId = y.Id, Number = i + 1, Name = $"{y.Start.AddMonths(i):yyyy-MM}", Start = y.Start.AddMonths(i), End = y.Start.AddMonths(i + 1).AddDays(-1),
            }));
        }

        await db.SaveChangesAsync();
        return new Setup(project, contract, root, leaf, leaf2, alex, category);
    }

    private async Task AddTimesheetAsync(Setup s, TimesheetStatus status, DateOnly workDate, decimal hours, Guid? wbsId = null)
    {
        await using var db = await _factory.CreateAsync();
        var sheet = new Timesheet { EmployeeId = s.Alex.Id, PeriodStart = workDate, PeriodEnd = workDate.AddDays(6), Status = status };
        sheet.Entries.Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbsId ?? s.Leaf.Id, LaborCategoryId = s.Category.Id, WorkDate = workDate, Hours = hours });
        db.Set<Timesheet>().Add(sheet);
        await db.SaveChangesAsync();
    }

    private async Task AddOdcAsync(Setup s, OdcStatus status, decimal amount, bool unallowable = false)
    {
        await using var db = await _factory.CreateAsync();
        db.Set<OdcEntry>().Add(new OdcEntry
        {
            ProjectId = s.Project.Id, WbsElementId = s.Leaf2.Id, Type = OdcType.Travel, IncurredOn = new DateOnly(2026, 10, 2), Amount = amount,
            Description = "Trip", SubmittedBy = "alex@example.com", Status = status, IsUnallowable = unallowable,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Only_approved_time_and_approved_allowable_odcs_count_as_actuals()
    {
        var s = await SeedAsync();
        await AddTimesheetAsync(s, TimesheetStatus.Approved, new DateOnly(2026, 10, 7), 8);  // $50/h
        await AddTimesheetAsync(s, TimesheetStatus.Approved, new DateOnly(2026, 10, 8), 8);  // $60/h after the mid-period raise
        await AddTimesheetAsync(s, TimesheetStatus.Submitted, new DateOnly(2026, 10, 9), 5);
        await AddTimesheetAsync(s, TimesheetStatus.Draft, new DateOnly(2026, 10, 12), 3);
        await AddTimesheetAsync(s, TimesheetStatus.Rejected, new DateOnly(2026, 10, 13), 2);
        await AddOdcAsync(s, OdcStatus.Approved, 100);
        await AddOdcAsync(s, OdcStatus.Approved, 50, unallowable: true);
        await AddOdcAsync(s, OdcStatus.Submitted, 70);
        await AddOdcAsync(s, OdcStatus.Rejected, 20);

        var summary = await _financials.GetProjectSummaryAsync(s.Project.Id);

        Assert.Equal(16m, summary.Totals.ActualHours);
        Assert.Equal(880m, summary.Totals.ActualLaborCost);
        Assert.Equal(100m, summary.Totals.ActualOdcCost);
        Assert.Equal(980m, summary.Totals.ActualCost);
        Assert.Equal(1_920m, summary.Totals.BillableValue);
        Assert.False(summary.ShowsBillableValue);
        Assert.Equal(980m, summary.Funding!.Spent);
        Assert.Equal(9_020m, summary.Funding.FundedRemaining);

        var rows = await _financials.GetWbsFinancialsAsync(s.Project.Id);
        Assert.Equal(980m, rows.Single(r => r.Code == "1001.01").Figures.ActualCost);
        Assert.Equal(100m, rows.Single(r => r.Code == "1001.01.02").Figures.ActualCost);
    }

    [Fact]
    public async Task Time_and_materials_contracts_consume_funding_at_billable_value()
    {
        var s = await SeedAsync(ContractType.TimeAndMaterials);
        await AddTimesheetAsync(s, TimesheetStatus.Approved, new DateOnly(2026, 10, 7), 10);
        await AddOdcAsync(s, OdcStatus.Approved, 100);

        var summary = await _financials.GetProjectSummaryAsync(s.Project.Id);

        Assert.True(summary.ShowsBillableValue);
        Assert.Equal(1_200m, summary.Totals.BillableValue);
        Assert.Equal(1_300m, summary.Funding!.Spent);
    }

    [Fact]
    public async Task Financials_use_the_current_budget_and_a_selected_forecast_for_etc()
    {
        var s = await SeedAsync();
        await AddTimesheetAsync(s, TimesheetStatus.Approved, new DateOnly(2026, 10, 7), 10); // $500
        var baseline = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Award", BudgetVersionKind.Baseline));
        await _budgets.SaveLinesAsync(baseline.Version.Id, [new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, null, 20)]);
        var current = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Working", BudgetVersionKind.Current));
        await _budgets.SaveLinesAsync(current.Version.Id, [new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, null, 40)]);
        var forecast = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("EAC", BudgetVersionKind.Forecast));
        await _budgets.SaveLinesAsync(forecast.Version.Id,
        [
            new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, new DateOnly(2026, 9, 1), 5),   // past period: not ETC
            new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, new DateOnly(2026, 11, 1), 50),
        ]);

        var byDefault = await _financials.GetProjectSummaryAsync(s.Project.Id);
        Assert.Equal("Working", byDefault.BudgetVersionName);
        Assert.Equal(40m, byDefault.Totals.BudgetHours);
        Assert.Equal(2_400m, byDefault.Totals.BudgetAmount); // 40 h at today's $60 planning rate
        Assert.Equal(1_900m, byDefault.Totals.EtcAmount);
        Assert.Equal(2_400m, byDefault.Totals.EacAmount);

        var withForecast = await _financials.GetProjectSummaryAsync(s.Project.Id, forecastVersionId: forecast.Version.Id);
        Assert.Equal(3_000m, withForecast.Totals.EtcAmount); // 50 h at $60 (November's rate)
        Assert.Equal(3_500m, withForecast.Totals.EacAmount);
        Assert.Equal(-1_100m, withForecast.Totals.VacAmount);

        var onBaseline = await _financials.GetProjectSummaryAsync(s.Project.Id, baseline.Version.Id);
        Assert.Equal(20m, onBaseline.Totals.BudgetHours);

        await Assert.ThrowsAsync<ArgumentException>(() => _financials.GetProjectSummaryAsync(s.Project.Id, forecastVersionId: current.Version.Id));
    }

    [Fact]
    public async Task A_locked_baseline_cant_be_edited_repriced_or_duplicated()
    {
        var s = await SeedAsync();
        var baseline = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Award", BudgetVersionKind.Baseline));
        await _budgets.SaveLinesAsync(baseline.Version.Id, [new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, null, 20)]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Another", BudgetVersionKind.Baseline)));

        var locked = await _budgets.LockBaselineAsync(baseline.Version.Id, "IBR complete");
        Assert.True(locked.IsLocked);
        Assert.Equal("finance@example.com", locked.LockedBy);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _budgets.SaveLinesAsync(baseline.Version.Id, [new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, null, 99)]));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.RepriceAsync(baseline.Version.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.LockBaselineAsync(baseline.Version.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.PromoteToCurrentAsync(baseline.Version.Id));
        Assert.Equal(20m, (await _budgets.GetVersionAsync(baseline.Version.Id))!.Version.TotalHours);

        // A copy of the locked baseline is editable.
        var copy = await _budgets.CreateVersionAsync(s.Project.Id,
            new CreateBudgetVersionRequest("Working", BudgetVersionKind.Current, BudgetVersionSource.CopyVersion, baseline.Version.Id));
        Assert.False(copy.Version.IsLocked);
        Assert.Equal(baseline.Version.Id, copy.Version.SourceVersionId);
        Assert.Equal(1_200m, copy.Version.TotalAmount);

        await using var db = await _factory.CreateAsync();
        var actions = await db.Set<AuditEvent>().Where(a => a.EntityId == baseline.Version.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(["Created", "LinesSaved", "Locked"], actions.Order());
        Assert.Equal("IBR complete", (await db.Set<AuditEvent>().SingleAsync(a => a.Action == "Locked")).Reason);
    }

    [Fact]
    public async Task Labor_lines_are_priced_at_the_planning_rate_and_only_reprice_changes_stored_amounts()
    {
        var s = await SeedAsync();
        var version = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Working", BudgetVersionKind.Current));
        var saved = await _budgets.SaveLinesAsync(version.Version.Id,
        [
            new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, new DateOnly(2026, 9, 1), 10), // phased into September: $50 then
            new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, null, 10),                    // unphased: today's $60
            new(s.Leaf2.Id, BudgetCostKind.Odc, null, BudgetOdcType.Travel, null, 0, 750),
        ]);
        Assert.Equal([500m, 600m, 750m], saved.Lines.OrderBy(l => l.Amount).Select(l => l.Amount));

        // A new cost rate doesn't silently change stored amounts...
        await using (var db = await _factory.CreateAsync())
        {
            db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = s.Alex.Id, EffectiveFrom = new DateOnly(2026, 8, 1), HourlyCost = 55m });
            await db.SaveChangesAsync();
        }

        var resaved = await _budgets.SaveLinesAsync(version.Version.Id, saved.Lines.Select(l =>
            new SaveBudgetLineRequest(l.WbsElementId, l.CostType, l.LaborCategoryId, l.OdcType, l.PeriodStart, l.Hours, l.Amount)).ToList());
        Assert.Equal(1_850m, resaved.Version.TotalAmount);

        // ...until an explicit reprice.
        var repriced = await _budgets.RepriceAsync(version.Version.Id);
        Assert.Equal(550m + 600m + 750m, repriced.Version.TotalAmount);
    }

    [Fact]
    public async Task Budget_lines_are_validated()
    {
        var s = await SeedAsync();
        var version = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Working", BudgetVersionKind.Current));

        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.SaveLinesAsync(version.Version.Id, [new(s.Root.Id, BudgetCostKind.Labor, s.Category.Id, null, null, 5)]));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.SaveLinesAsync(version.Version.Id, [new(s.Leaf.Id, BudgetCostKind.Labor, s.Category.Id, null, new DateOnly(2026, 10, 15), 5)]));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.SaveLinesAsync(version.Version.Id, [new(s.Leaf.Id, BudgetCostKind.Odc, null, null, null, 0, 10)]));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.SaveLinesAsync(version.Version.Id, [new(Guid.NewGuid(), BudgetCostKind.Odc, null, BudgetOdcType.Travel, null, 0, 10)]));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Second", BudgetVersionKind.Current)));

        // ODC budget may sit on a parent element; duplicate keys are merged.
        var saved = await _budgets.SaveLinesAsync(version.Version.Id,
        [
            new(s.Root.Id, BudgetCostKind.Odc, null, BudgetOdcType.Other, null, 0, 10),
            new(s.Root.Id, BudgetCostKind.Odc, null, BudgetOdcType.Other, null, 0, 15),
        ]);
        Assert.Equal(25m, Assert.Single(saved.Lines).Amount);
    }

    [Fact]
    public async Task Promoting_a_forecast_makes_it_current_and_demotes_the_old_current()
    {
        var s = await SeedAsync();
        var current = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("Working", BudgetVersionKind.Current));
        var forecast = await _budgets.CreateVersionAsync(s.Project.Id,
            new CreateBudgetVersionRequest("Rebaseline", BudgetVersionKind.Forecast, BudgetVersionSource.CopyVersion, current.Version.Id));

        var promoted = await _budgets.PromoteToCurrentAsync(forecast.Version.Id);

        Assert.Equal(BudgetVersionKind.Current, promoted.Kind);
        var versions = await _budgets.ListVersionsAsync(s.Project.Id);
        Assert.Equal([("Rebaseline", BudgetVersionKind.Current), ("Working", BudgetVersionKind.Forecast)], versions.Select(v => (v.Name, v.Kind)));
        await Assert.ThrowsAsync<ArgumentException>(() => _budgets.PromoteToCurrentAsync(forecast.Version.Id));
    }

    [Fact]
    public async Task Creating_a_version_from_the_plan_sheet_sums_planned_hours_and_budget_of_each_linked_item()
    {
        var sheetId = Guid.NewGuid();
        var hours = new Column { SheetId = sheetId, Name = "Planned hours", Type = ColumnType.Number, Order = 0 };
        var budget = new Column { SheetId = sheetId, Name = "Budget", Type = ColumnType.Currency, Order = 1 };
        var owner = new Column { SheetId = sheetId, Name = "Owner", Type = ColumnType.Person, Order = 2 };
        var phase = new Item { SheetId = sheetId, Title = "Phase 1", Values = new() { [hours.Id.ToString()] = "999" } };
        var kickoff = new Item { SheetId = sheetId, ParentId = phase.Id, Title = "Kickoff", Values = new() { [hours.Id.ToString()] = "8", [budget.Id.ToString()] = "1200", [owner.Id.ToString()] = "alex@example.com" } };
        var review = new Item { SheetId = sheetId, ParentId = phase.Id, Title = "Review", Values = new() { [hours.Id.ToString()] = "60.5", [budget.Id.ToString()] = "9000" } };
        var travel = new Item { SheetId = sheetId, Title = "Travel", Values = new() { [budget.Id.ToString()] = "2500" } };
        await using (var db = await _factory.CreateAsync())
        {
            // The sheet first: the project's plan-sheet link is a foreign key.
            var workspace = new Workspace { Name = "General" };
            db.Workspaces.Add(workspace);
            db.Sheets.Add(new Sheet { Id = sheetId, Name = "Plan", WorkspaceId = workspace.Id });
            db.Columns.AddRange(hours, budget, owner);
            db.Items.AddRange(phase, kickoff, review, travel);
            await db.SaveChangesAsync();
        }

        var s = await SeedAsync(sheetId: sheetId);
        await using (var db = await _factory.CreateAsync())
        {
            var leaf = await db.Set<WbsElement>().SingleAsync(w => w.Id == s.Leaf.Id);
            leaf.ItemId = phase.Id;
            var leaf2 = await db.Set<WbsElement>().SingleAsync(w => w.Id == s.Leaf2.Id);
            leaf2.ItemId = travel.Id;
            var root = await db.Set<WbsElement>().SingleAsync(w => w.Id == s.Root.Id);
            root.ItemId = review.Id; // not a leaf: skipped so it isn't counted twice
            await db.SaveChangesAsync();
        }

        var created = await _budgets.CreateVersionAsync(s.Project.Id, new CreateBudgetVersionRequest("From plan", BudgetVersionKind.Baseline, BudgetVersionSource.PlanSheet));

        Assert.Equal(2, created.Lines.Count);
        var labor = created.Lines.Single(l => l.CostType == BudgetCostKind.Labor);
        Assert.Equal(s.Leaf.Id, labor.WbsElementId);
        Assert.Equal(68.5m, labor.Hours);
        Assert.Equal(10_200m, labor.Amount);
        Assert.Equal(s.Category.Id, labor.LaborCategoryId); // from the kickoff owner's default category
        var odc = created.Lines.Single(l => l.CostType == BudgetCostKind.Odc);
        Assert.Equal((s.Leaf2.Id, BudgetOdcType.Other, 2_500m), (odc.WbsElementId, odc.OdcType!.Value, odc.Amount));
        Assert.Equal(12_700m, created.Version.TotalAmount);
    }

    [Fact]
    public async Task Csv_exports_include_every_wbs_row_and_period()
    {
        var s = await SeedAsync();
        await AddTimesheetAsync(s, TimesheetStatus.Approved, new DateOnly(2026, 10, 7), 8);

        var wbs = await _exports.ExportWbsCsvAsync(s.Project.Id);
        var lines = wbs.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("text/csv", wbs.ContentType);
        Assert.StartsWith("1001-wbs-financials-", wbs.FileName, StringComparison.Ordinal);
        Assert.Equal(5, lines.Length); // header, three elements, total
        Assert.StartsWith("1001.01.01,Planning,1,0.00,8.00,", lines[2], StringComparison.Ordinal);

        var burn = await _exports.ExportTimePhasedCsvAsync(s.Project.Id);
        Assert.Contains("2026-10,2026-10-01,2026-10-31,0.00,8.00,400.00,0.00,400.00,0.00,400.00", burn.Content, StringComparison.Ordinal);
    }

    private sealed class FinanceUser : ICurrentUser
    {
        public string Email => "finance@example.com";
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
