using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Scheduling;

/// <summary>Named baselines and the plan's variance from them, in working days on the plan's calendar.</summary>
public sealed class BaselineVarianceTests
{
    private static readonly DateOnly Mon = PlanKit.Mon;
    private static readonly DateOnly ColumbusDay = new(2026, 10, 12);

    private readonly PlanKit _kit = new();

    [Fact]
    public async Task Variance_counts_working_days_the_plan_has_moved_since_the_baseline()
    {
        await _kit.HolidayAsync(ColumbusDay, "Columbus Day");
        var sheet = await _kit.SheetAsync();
        var phase = await _kit.TaskAsync(sheet, "Phase 1", null, 0);
        var design = await _kit.TaskAsync(sheet, "Design", Mon, 5, phase.Id);
        var build = await _kit.TaskAsync(sheet, "Build", Mon.AddDays(7), 3, phase.Id); // starts on the holiday: Tue 13–Wed 14 once the link reflows it
        await _kit.LinkAsync(sheet, design.Id, build.Id);
        var approved = await _kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest("Approved"));

        await _kit.Plan.SetDurationAsync(sheet, design.Id, 7); // two days longer: Build slips two working days
        var late = await _kit.TaskAsync(sheet, "Added later", Mon, 2);

        var comparison = await _kit.Baselines.CompareAsync(sheet, approved.Id);

        Assert.Equal("Approved", comparison.Baseline.Name);
        var d = comparison.For(design.Id)!;
        Assert.Equal((Mon, Mon.AddDays(4)), (d.BaselineStart!.Value, d.BaselineFinish!.Value));
        Assert.Equal((0, 2, 2), (d.StartVarianceDays!.Value, d.FinishVarianceDays!.Value, d.DurationVarianceDays!.Value));
        var b = comparison.For(build.Id)!;
        Assert.Equal((2, 2, 0), (b.StartVarianceDays!.Value, b.FinishVarianceDays!.Value, b.DurationVarianceDays!.Value)); // the holiday isn't counted
        var p = comparison.For(phase.Id)!;
        Assert.Equal(2, p.FinishVarianceDays); // the summary spans its subtasks' baseline dates
        var added = comparison.For(late.Id)!;
        Assert.False(added.InBaseline);
        Assert.Null(added.FinishVarianceDays);
        Assert.Equal(2, comparison.FinishVarianceDays);
        Assert.Equal([phase.Id, design.Id, build.Id, late.Id], comparison.Tasks.Select(t => t.ItemId)); // outline order
    }

    [Fact]
    public async Task A_plan_keeps_several_named_baselines_and_compares_with_any()
    {
        var sheet = await _kit.SheetAsync();
        var task = await _kit.TaskAsync(sheet, "Task", Mon, 5);
        var first = await _kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest("Proposal"));
        await _kit.Plan.SetDurationAsync(sheet, task.Id, 8);
        var second = await _kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest("Award"));
        await _kit.Plan.SetDurationAsync(sheet, task.Id, 10);

        Assert.Equal(5, (await _kit.Baselines.CompareAsync(sheet, first.Id)).For(task.Id)!.FinishVarianceDays);
        Assert.Equal(2, (await _kit.Baselines.CompareAsync(sheet, second.Id)).For(task.Id)!.FinishVarianceDays);

        await _kit.Baselines.DeleteAsync(sheet, first.Id);
        Assert.Equal(["Award"], (await _kit.Baselines.ListAsync(sheet)).Select(x => x.Name));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => _kit.Baselines.CompareAsync(sheet, first.Id));
        var other = await _kit.SheetAsync("Other");
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => _kit.Baselines.DeleteAsync(other, second.Id));
    }

    [Fact]
    public async Task Earlier_than_the_baseline_is_negative()
    {
        var sheet = await _kit.SheetAsync();
        var task = await _kit.TaskAsync(sheet, "Task", Mon.AddDays(7), 5);
        var baseline = await _kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest(null));
        var item = await _kit.GetAsync(sheet, task.Id);
        await _kit.Items.UpdateAsync(sheet, task.Id, new Workbench.Contracts.Sheets.UpdateItemRequest("Task", Mon, Mon.AddDays(4), null, item.Values));

        var row = (await _kit.Baselines.CompareAsync(sheet, baseline.Id)).For(task.Id)!;
        Assert.Equal((-5, -5, 0), (row.StartVarianceDays!.Value, row.FinishVarianceDays!.Value, row.DurationVarianceDays!.Value));
    }

    [Fact]
    public async Task Viewers_compare_and_editors_delete()
    {
        await using var seeded = await SeededHost.CreateAsync();
        IBaselineService As(string user) => seeded.As(user).Host.Get<IBaselineService>(); // As switches the host's signed-in user
        var baseline = await As(SeededHost.Editor).CaptureAsync(seeded.SheetId, new CaptureBaselineRequest("Approved"));

        Assert.NotEmpty((await As(SeededHost.Viewer).CompareAsync(seeded.SheetId, baseline.Id)).Tasks);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => As(SeededHost.Viewer).DeleteAsync(seeded.SheetId, baseline.Id));
        await As(SeededHost.Editor).DeleteAsync(seeded.SheetId, baseline.Id);
        Assert.DoesNotContain(await As(SeededHost.Viewer).ListAsync(seeded.SheetId), b => b.Id == baseline.Id);
    }
}
