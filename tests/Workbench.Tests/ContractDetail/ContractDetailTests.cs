using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts;
using Workbench.Application.ContractDetail;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;
using ChangeKind = Workbench.Contracts.Contracts.ModificationChangeKind;

namespace Workbench.Tests.ContractDetail;

/// <summary>
/// Contract detail over <see cref="ProjectAccountingFixture"/>: contract C1 gets CLIN 0001 (CPFF, value 10,000), and a new
/// direct WBS element 1001.02 (no assignment needed) is mapped to it. Costs are dated relative to today so the burn window is stable.
/// </summary>
public sealed class ContractDetailTests : IAsyncLifetime
{
    private SeededHost _seeded = default!;
    private ProjectAccountingFixture _fx = default!;
    private Guid _clin1;
    private Guid _wbs;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.UtcNow);

    private IClinService Clins => _seeded.Host.Get<IClinService>();
    private IContractModificationService Mods => _seeded.Host.Get<IContractModificationService>();
    private IFundingService Funding => _seeded.Host.Get<IFundingService>();

    public async Task InitializeAsync()
    {
        _seeded = await SeededHost.CreateAsync();
        _fx = await ProjectAccountingFixture.SeedAsync(_seeded);
        _seeded.As(Finance);
        _clin1 = (await Clins.CreateClinAsync(_fx.Contract1, Clin("0001", 10_000m, start: _today.AddDays(-29)))).Id;
        _wbs = (await _seeded.Host.Get<IProjectService>().CreateWbsElementAsync(_fx.Project1,
            new SaveWbsElementRequest(null, "1001.02", "CLIN work", RequiresAssignment: false))).Id;
        await Clins.SetWbsClinAsync(_wbs, _clin1);
    }

    public async Task DisposeAsync() => await _seeded.DisposeAsync();

    private SaveClinRequest Clin(string number, decimal value, bool option = false, DateOnly? start = null, DateOnly? end = null) =>
        new(number, $"CLIN {number}", ClinPricingKind.CostPlusFixedFee, start ?? _today.AddYears(-1), end ?? _today.AddYears(2), value, 0.07m, option);

    private async Task<ContractModificationDto> ExecuteAsync(string number, params ModificationChangeDto[] changes)
    {
        var draft = await Mods.CreateDraftAsync(_fx.Contract1, new SaveModificationRequest(number, _today, $"{number} changes", changes));
        return await Mods.ExecuteAsync(draft.Id);
    }

    private static ModificationChangeDto Fund(string clin, decimal amount) => new(ChangeKind.Fund, clin, amount);

    private ModificationChangeDto AddClin(string clin, decimal value, bool option = false) =>
        new(ChangeKind.AddClin, clin, value, ClinDescription: $"New {clin}", Pricing: ClinPricingKind.CostPlusFixedFee, IsOption: option,
            PeriodStart: _today.AddYears(-1), PeriodEnd: _today.AddYears(2));

    private async Task<(Clin Clin, Contract Contract, int Actions)> ReadAsync(string number)
    {
        await using var db = await _seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var clin = await db.Set<Clin>().AsNoTracking().SingleAsync(c => c.ContractId == _fx.Contract1 && c.Number == number);
        var contract = await db.Set<Contract>().AsNoTracking().SingleAsync(c => c.Id == _fx.Contract1);
        var actions = await db.Set<FundingAction>().CountAsync(f => f.ContractId == _fx.Contract1);
        return (clin, contract, actions);
    }

    [Fact]
    public async Task Executing_a_modification_applies_every_change()
    {
        var mod = await ExecuteAsync("P00001",
            AddClin("0002", 5_000m), Fund("0001", 5_000m), Fund("0002", 1_000m), new ModificationChangeDto(ChangeKind.ChangeClinValue, "0001", 2_000m),
            new ModificationChangeDto(ChangeKind.ExtendPeriod, null, NewPeriodEnd: new DateOnly(2031, 12, 31)));

        Assert.Equal(ModificationState.Executed, mod.Status);
        Assert.Equal(6_000m, mod.FundingDelta);
        var (clin1, contract, actions) = await ReadAsync("0001");
        var (clin2, _, _) = await ReadAsync("0002");
        Assert.Equal((12_000m, 5_000m), (clin1.CeilingValue, clin1.FundedValue));
        Assert.Equal((5_000m, 1_000m), (clin2.CeilingValue, clin2.FundedValue));
        Assert.Equal(2, actions);
        Assert.Equal((17_000m, 6_000m), (contract.CeilingValue, contract.FundedValue));
        Assert.Equal(new DateOnly(2031, 12, 31), contract.PeriodEnd);

        var history = await Funding.ListFundingHistoryAsync(_fx.Contract1);
        Assert.All(history, h => Assert.Equal("P00001", h.ModificationNumber));
        Assert.Equal(5_000m, history.Single(h => h.ClinNumber == "0001").RunningTotal);
    }

    [Fact]
    public async Task A_failing_change_applies_nothing()
    {
        var draft = await Mods.CreateDraftAsync(_fx.Contract1, new SaveModificationRequest("P00001", _today, "Bad",
            [AddClin("0002", 5_000m), Fund("0001", 1_000m), Fund("0001", 50_000m)]));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Mods.ExecuteAsync(draft.Id));
        Assert.Contains("can't exceed its value", error.Message);

        var (clin1, contract, actions) = await ReadAsync("0001");
        Assert.Equal(0m, clin1.FundedValue);
        Assert.Equal(0, actions);
        Assert.Equal((10_000m, 0m), (contract.CeilingValue, contract.FundedValue));
        Assert.Single(await Clins.ListClinsAsync(_fx.Contract1));
        Assert.Equal(ModificationState.Draft, (await Mods.GetAsync(draft.Id))!.Status);
    }

    [Fact]
    public async Task An_executed_modification_is_immutable()
    {
        var mod = await ExecuteAsync("P00001", Fund("0001", 1_000m));
        var request = new SaveModificationRequest("P00001", _today, "Changed", [Fund("0001", 9_000m)]);

        await Assert.ThrowsAsync<ArgumentException>(() => Mods.UpdateDraftAsync(mod.Id, request));
        await Assert.ThrowsAsync<ArgumentException>(() => Mods.DeleteDraftAsync(mod.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => Mods.ExecuteAsync(mod.Id));
        Assert.Equal(1_000m, (await ReadAsync("0001")).Clin.FundedValue);

        // A correction is another modification.
        await ExecuteAsync("P00002", Fund("0001", -400m));
        Assert.Equal(600m, (await ReadAsync("0001")).Clin.FundedValue);
        Assert.Equal([1_000m, 600m], (await Funding.ListFundingHistoryAsync(_fx.Contract1)).Select(h => h.RunningTotal));
    }

    [Fact]
    public async Task Options_are_excluded_until_exercised_and_cant_be_funded_before()
    {
        await ExecuteAsync("P00001", AddClin("1001", 3_000m, option: true));
        var option = await ReadAsync("1001");
        Assert.True(option.Clin.IsOption);
        Assert.False(option.Clin.IsExercised);
        Assert.Equal(10_000m, option.Contract.CeilingValue);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync("P00002", Fund("1001", 500m)));
        Assert.Contains("isn't exercised", error.Message);

        await ExecuteAsync("P00003", new ModificationChangeDto(ChangeKind.ExerciseOption, "1001"), Fund("1001", 500m));
        var exercised = await ReadAsync("1001");
        Assert.True(exercised.Clin.IsExercised);
        Assert.Equal((13_000m, 500m), (exercised.Contract.CeilingValue, exercised.Contract.FundedValue));

        await Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync("P00004", new ModificationChangeDto(ChangeKind.ExerciseOption, "1001")));
        await Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync("P00005", new ModificationChangeDto(ChangeKind.ExerciseOption, "0001")));
    }

    [Fact]
    public async Task Slins_roll_up_to_their_clin_and_clins_to_the_contract()
    {
        await Clins.CreateClinAsync(_fx.Contract1, Clin("0003", 0m));
        await Clins.CreateClinAsync(_fx.Contract1, Clin("0003aa", 1_000m));
        await Clins.CreateClinAsync(_fx.Contract1, Clin("0003AB", 2_000m));
        await ExecuteAsync("P00001", Fund("0003AA", 400m), Fund("0001", 100m));

        var list = await Clins.ListClinsAsync(_fx.Contract1);
        Assert.Equal(["0001", "0003", "0003AA", "0003AB"], list.Select(c => c.Number));
        var parent = list.Single(c => c.Number == "0003");
        Assert.True(parent.HasSlins);
        Assert.Equal((3_000m, 400m), (parent.CeilingValue, parent.FundedValue));
        Assert.Equal("0003", list.Single(c => c.Number == "0003AB").ParentNumber);
        var contract = (await ReadAsync("0001")).Contract;
        Assert.Equal((13_000m, 500m), (contract.CeilingValue, contract.FundedValue));

        // A roll-up CLIN isn't funded or mapped directly, and its contract keeps the rolled-up totals on edit.
        await Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync("P00002", Fund("0003", 1m)));
        await Assert.ThrowsAsync<ArgumentException>(() => Clins.SetWbsClinAsync(_wbs, parent.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => Clins.CreateClinAsync(_fx.Contract1, Clin("0001AA", 1m)));
        var dto = (await _seeded.Host.Get<IContractService>().GetContractAsync(_fx.Contract1))!;
        var saved = await _seeded.Host.Get<IContractService>().UpdateContractAsync(_fx.Contract1, new SaveContractRequest(dto.CustomerId, dto.ContractNumber,
            "Renamed", dto.Type, dto.Status, dto.IsPrime, dto.PeriodStart, dto.PeriodEnd, 99m, 1m));
        Assert.Equal(("Renamed", 13_000m, 500m), (saved.Name, saved.CeilingValue, saved.FundedValue));
    }

    /// <summary>Approved: 10 h × 87.50 + a 125 ODC = 1,000 incurred yesterday. Submitted: 2 h = 175 committed.</summary>
    private async Task AddCostsAsync()
    {
        await using var db = await _seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var category = await db.Set<LaborCategory>().Select(c => c.Id).FirstAsync();
        var now = DateTime.UtcNow;
        var day = _today.AddDays(-1);
        var week = day.AddDays(-(int)day.DayOfWeek);
        // Whole weeks, so the hours stay under the 40-hour standard whatever day the suite runs (an exempt employee's hours
        // past the period's standard are uncompensated and cost nothing). One timesheet per employee and period start.
        foreach (var (status, hours, start, workDate) in new[] { (TimesheetStatus.Approved, 10m, week, day), (TimesheetStatus.Submitted, 2m, week.AddDays(-7), day.AddDays(-7)) })
        {
            var sheet = new Timesheet { EmployeeId = _fx.AnalystId, PeriodStart = start, PeriodEnd = start.AddDays(6), Status = status, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Set<Timesheet>().Add(sheet);
            db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = _wbs, LaborCategoryId = category, WorkDate = workDate, Hours = hours, EnteredAtUtc = now, UpdatedAtUtc = now });
        }

        db.Set<OdcEntry>().AddRange(
            new OdcEntry { ProjectId = _fx.Project1, WbsElementId = _wbs, Type = OdcType.Materials, IncurredOn = day, Amount = 125m, Description = "Cables", Status = OdcStatus.Approved, SubmittedBy = Finance, CreatedAtUtc = now },
            new OdcEntry { ProjectId = _fx.Project1, WbsElementId = _wbs, Type = OdcType.Materials, IncurredOn = day, Amount = 999m, Description = "Party", Status = OdcStatus.Approved, IsUnallowable = true, SubmittedBy = Finance, CreatedAtUtc = now });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Funding_status_reports_incurred_committed_remaining_and_exhaustion()
    {
        await AddCostsAsync();
        await ExecuteAsync("P00001", Fund("0001", 4_000m));

        var status = await Funding.GetFundingStatusAsync(_fx.Contract1);
        var row = Assert.Single(status.Clins);
        Assert.False(status.IsBurdened);
        Assert.Equal(1_000m, row.Incurred);
        Assert.Equal(175m, row.Committed);
        Assert.Equal(3_000m, row.Remaining);
        Assert.Equal(0.25m, row.PercentIncurred);
        Assert.Equal(0.2938m, row.PercentCommitted);
        Assert.Equal(33.33m, row.BurnPerDay);                    // 1,000 over the 30 days since the CLIN started
        Assert.Equal(_today.AddDays(91), row.ProjectedExhaustion); // ceil(3,000 / 33.33)
        Assert.Equal(FundingLevel.Ok, row.Level);

        var project = await Funding.GetProjectFundingStatusAsync(_fx.Project1);
        Assert.Equal(_clin1, Assert.Single(project!.Clins).ClinId);
        Assert.Null(await Funding.GetProjectFundingStatusAsync(await IndirectProjectAsync()));
    }

    private async Task<Guid> IndirectProjectAsync() =>
        (await _seeded.Host.Get<IProjectService>().CreateProjectAsync(new SaveProjectRequest("9000", "Overhead", null, null, ProjectState.Active, null, null))).Id;

    [Fact]
    public async Task The_75_percent_notice_is_sent_once_per_threshold_and_funding_level()
    {
        await AddCostsAsync();
        var mod = await ExecuteAsync("P00001", Fund("0001", 1_500m)); // 1,175 / 1,500 = 78%: execution runs the check

        var status = await Funding.GetFundingStatusAsync(_fx.Contract1);
        var notice = Assert.Single(status.Notices);
        Assert.Equal(75, notice.ThresholdPercent);
        Assert.StartsWith("Limitation of Funds: C1 CLIN 0001", notice.Title);
        Assert.Equal([Finance, Owner], notice.Recipients);
        Assert.Equal(FundingLevel.Warning, Assert.Single(status.Clins).Level);
        Assert.Empty(await Funding.CheckNoticesAsync(_fx.Contract1));
        Assert.Empty(await Funding.CheckNoticesAsync(_fx.Contract1));

        await using (var db = await _seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var sent = await db.Set<Notification>().Where(n => n.Kind == FundingNoticeChecker.NotificationKind).Select(n => n.RecipientEmail).ToListAsync();
            Assert.Equal([Finance, Owner], sent.Order());
        }

        // De-obligating to 1,300 (90%) changes the funded value: both thresholds are due again at the new level.
        await ExecuteAsync("P00002", Fund("0001", -200m));
        Assert.Equal([75, 90], (await Funding.GetFundingStatusAsync(_fx.Contract1)).Notices.Where(n => n.FundedValue == 1_300m).Select(n => n.ThresholdPercent).Order());
        Assert.Equal(ModificationState.Executed, mod.Status);
    }

    [Fact]
    public async Task Unexercised_options_and_out_of_period_clins_arent_charge_codes()
    {
        var projects = _seeded.Host.Get<IProjectService>();
        async Task<bool> ChargeableAsync(DateOnly date) =>
            (await projects.ListChargeCodesAsync(_fx.AnalystId, date)).Any(c => c.WbsElementId == _wbs);

        Assert.True(await ChargeableAsync(_today));
        Assert.False(await ChargeableAsync(_today.AddDays(-30)));  // before CLIN 0001 starts

        await ExecuteAsync("P00001", AddClin("1001", 3_000m, option: true));
        var option = (await Clins.ListClinsAsync(_fx.Contract1)).Single(c => c.Number == "1001");
        await Clins.SetWbsClinAsync(_wbs, option.Id);
        Assert.False(await ChargeableAsync(_today));

        await ExecuteAsync("P00002", new ModificationChangeDto(ChangeKind.ExerciseOption, "1001"));
        Assert.True(await ChargeableAsync(_today));
        Assert.False(await ChargeableAsync(_today.AddYears(3)));    // after the option's period ends
    }

    [Fact]
    public async Task Wbs_can_only_map_to_a_clin_on_the_projects_contract()
    {
        var other = await Clins.CreateClinAsync(_fx.Contract2, Clin("0001", 100m));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Clins.SetWbsClinAsync(_wbs, other.Id));
        Assert.Contains("isn't on project 1001's contract", error.Message);

        Assert.Null((await Clins.SetWbsClinAsync(_wbs, null)).ClinId);
        Assert.DoesNotContain(await Clins.ListWbsMappingsAsync(_fx.Project1), m => m.WbsElementId == _wbs && m.ClinId != null);
        Assert.Equal(["0001"], (await Clins.ListClinsForProjectAsync(_fx.Project1)).Select(c => c.Number));
    }

    [Fact]
    public async Task Reads_are_for_finance_and_the_contracts_managers_and_writes_for_finance()
    {
        var draft = await Mods.CreateDraftAsync(_fx.Contract1, new SaveModificationRequest("P00001", _today, "Funding", [Fund("0001", 100m)]));
        var other = await Clins.CreateClinAsync(_fx.Contract2, Clin("0001", 100m));

        _seeded.As(Owner); // manages project 1001 on C1, isn't Finance
        Assert.Single(await Clins.ListClinsAsync(_fx.Contract1));
        Assert.Single(await Mods.ListAsync(_fx.Contract1));
        Assert.NotNull(await Mods.GetAsync(draft.Id));
        Assert.Single((await Funding.GetFundingStatusAsync(_fx.Contract1)).Clins);
        Assert.Empty(await Funding.ListFundingHistoryAsync(_fx.Contract1));
        Assert.NotNull(await Funding.GetProjectFundingStatusAsync(_fx.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Clins.ListClinsAsync(_fx.Contract2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Clins.GetClinAsync(other.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Funding.GetFundingStatusAsync(_fx.Contract2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Clins.CreateClinAsync(_fx.Contract1, Clin("0002", 1m)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Clins.SetWbsClinAsync(_wbs, null));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Mods.CreateDraftAsync(_fx.Contract1, new SaveModificationRequest("P00009", _today, "x", [])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Mods.ExecuteAsync(draft.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Funding.CheckNoticesAsync(_fx.Contract1));

        _seeded.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Clins.ListClinsAsync(_fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Funding.GetProjectFundingStatusAsync(_fx.Project1));

        _seeded.As(Admin);
        Assert.Equal(ModificationState.Executed, (await Mods.ExecuteAsync(draft.Id)).Status);
    }
}
