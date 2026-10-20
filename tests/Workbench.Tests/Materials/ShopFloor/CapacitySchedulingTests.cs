using Workbench.Contracts.Materials;

namespace Workbench.Tests.Materials.ShopFloor;

/// <summary>Finite capacity scheduling: calendars, forward and backward loading, late flags, the weekly load chart, pins and queue order.</summary>
public sealed class CapacitySchedulingTests
{
    /// <summary>A Monday well after today, so reschedules (which run from today) start from it too.</summary>
    private static readonly DateOnly Monday = NextMonday(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14));

    private static DateOnly NextMonday(DateOnly d) => d.AddDays((8 - (int)d.DayOfWeek) % 7);

    private static async Task<(WorkCenterDto Asm, WorkCenterDto Test, PartSummaryDto Pin)> SetupAsync(MaterialsKit kit)
    {
        var asm = await kit.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("ASM", "Assembly", HoursPerDay: 8));
        var test = await kit.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("TST", "Test", HoursPerDay: 8));
        var pin = await kit.PartAsync("PIN");
        return (asm, test, pin);
    }

    /// <summary>A released order: <paramref name="quantity"/> × 4 h at ASM (and × 2 h at TST when <paramref name="test"/> is given).</summary>
    private static Task<WorkOrderDto> OrderAsync(MaterialsKit kit, string number, decimal quantity, WorkCenterDto asm, WorkCenterDto? test, PartSummaryDto pin, DateOnly due) =>
        kit.ReleasedOrderAsync(number, quantity, [(pin.Id, 1, null)],
            test is null ? [(10, asm.Id, 0, 4)] : [(10, asm.Id, 0, 4), (20, test.Id, 0, 2)], start: Monday, due: due);

    private static ScheduledOperationDto Op(CapacityScheduleDto s, string wo, int seq) => s.Operations.Single(o => o.WorkOrderNumber == wo && o.Sequence == seq);

    [Fact]
    public async Task Forward_scheduling_loads_each_work_center_to_capacity_in_queue_order_and_flags_late_operations()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var (asm, test, pin) = await SetupAsync(kit);
        var first = await OrderAsync(kit, "ONE", 3, asm, test, pin, Monday.AddDays(4));
        var second = await OrderAsync(kit, "TWO", 3, asm, null, pin, Monday.AddDays(1));

        var s = await kit.Capacity().RunAsync(new RunScheduleRequest(ScheduleMode.Forward, true, Monday));
        // TWO is due first, so it's first in the ASM queue: Monday 8 h + Tuesday 4 h. ONE gets Tuesday's other 4 h and Wednesday.
        Assert.Equal((Monday, Monday.AddDays(1), false), (Op(s, second.Summary.Number, 10).Start, Op(s, second.Summary.Number, 10).End, Op(s, second.Summary.Number, 10).IsLate));
        Assert.Equal((Monday.AddDays(1), Monday.AddDays(2)), (Op(s, first.Summary.Number, 10).Start, Op(s, first.Summary.Number, 10).End));
        // Operation 20 can't start before operation 10 ends.
        Assert.Equal((Monday.AddDays(2), Monday.AddDays(2), 6m), (Op(s, first.Summary.Number, 20).Start, Op(s, first.Summary.Number, 20).End, Op(s, first.Summary.Number, 20).Hours));
        var week = s.Load.Single(l => l.Code == "ASM").Weeks[0];
        Assert.Equal((Monday, 40m, 24m, false), (week.WeekStart, week.Capacity, week.Load, week.IsOverloaded));
        Assert.Equal(0, s.LateCount);

        // Two units working in parallel, and a shutdown on Tuesday.
        await kit.Capacity().SaveCalendarAsync(asm.Id, new SaveWorkCenterCalendarRequest("YYYYYNN", 8, 2));
        var calendar = await kit.Capacity().SaveExceptionAsync(asm.Id, new SaveCapacityExceptionRequest(Monday.AddDays(1), 0, "Shutdown"));
        Assert.Equal((16m, 1), (calendar.DailyCapacity, calendar.Exceptions.Count));
        s = await kit.Capacity().RunAsync(new RunScheduleRequest(ScheduleMode.Forward, true, Monday));
        Assert.Equal((Monday, Monday), (Op(s, second.Summary.Number, 10).Start, Op(s, second.Summary.Number, 10).End)); // 12 of Monday's 16 h.
        Assert.Equal((Monday, Monday.AddDays(2)), (Op(s, first.Summary.Number, 10).Start, Op(s, first.Summary.Number, 10).End)); // 4 h Monday, nothing Tuesday, 8 h Wednesday.
        Assert.Equal(64m, s.Load.Single(l => l.Code == "ASM").Weeks[0].Capacity); // Four days at 16 h.

        // A work order due before its work can finish is late.
        await kit.Capacity().DeleteExceptionAsync(calendar.Exceptions[0].Id);
        var rush = await OrderAsync(kit, "RUSH", 10, asm, null, pin, Monday);
        s = await kit.Capacity().RunAsync(new RunScheduleRequest(ScheduleMode.Forward, true, Monday));
        Assert.True(Op(s, rush.Summary.Number, 10).IsLate);
        Assert.True(s.LateCount >= 1);
    }

    [Fact]
    public async Task Backward_scheduling_finishes_on_the_due_date_and_falls_back_to_forward_when_it_cannot_fit()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var (asm, test, pin) = await SetupAsync(kit);
        var friday = Monday.AddDays(4);
        var one = await OrderAsync(kit, "ONE", 3, asm, test, pin, friday);
        var s = await kit.Capacity().RunAsync(new RunScheduleRequest(ScheduleMode.Backward, true, Monday));
        Assert.Equal((friday, friday, 6m), (Op(s, one.Summary.Number, 20).Start, Op(s, one.Summary.Number, 20).End, Op(s, one.Summary.Number, 20).Hours));
        Assert.Equal((Monday.AddDays(3), friday), (Op(s, one.Summary.Number, 10).Start, Op(s, one.Summary.Number, 10).End)); // 8 h Friday, 4 h Thursday.
        Assert.Equal(ScheduleMode.Backward, s.Direction);

        // 48 h of assembly due on Tuesday can't fit in Monday and Tuesday: it goes forward from the start and is late.
        var big = await OrderAsync(kit, "BIG", 12, asm, null, pin, Monday.AddDays(1));
        s = await kit.Capacity().RunAsync(new RunScheduleRequest(ScheduleMode.Backward, true, Monday));
        Assert.Equal(Monday, Op(s, big.Summary.Number, 10).Start);
        Assert.True(Op(s, big.Summary.Number, 10).IsLate);
    }

    [Fact]
    public async Task Pinned_operations_can_overload_a_week_infinite_loading_shows_overload_and_the_queue_can_be_resequenced()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var (asm, _, pin) = await SetupAsync(kit);
        var a = await OrderAsync(kit, "AAA", 10, asm, null, pin, Monday.AddDays(20)); // 40 h each
        var b = await OrderAsync(kit, "BBB", 10, asm, null, pin, Monday.AddDays(21));
        var s = await kit.Capacity().RunAsync(new RunScheduleRequest(ScheduleMode.Forward, true, Monday));
        Assert.Equal((Monday, Monday.AddDays(7)), (Op(s, a.Summary.Number, 10).Start, Op(s, b.Summary.Number, 10).Start));
        Assert.All(s.Load.Single(l => l.Code == "ASM").Weeks, w => Assert.False(w.IsOverloaded));

        // Resequence: BBB first.
        s = await kit.Capacity().ResequenceAsync(asm.Id, new ResequenceRequest([Op(s, b.Summary.Number, 10).Id, Op(s, a.Summary.Number, 10).Id]));
        Assert.Equal((1, Monday), (Op(s, b.Summary.Number, 10).QueuePosition, Op(s, b.Summary.Number, 10).Start));
        Assert.Equal(Monday.AddDays(7), Op(s, a.Summary.Number, 10).Start);

        // The planner drags AAA onto BBB's week: both are pinned there, and the week is overloaded.
        s = await kit.Capacity().MoveAsync(Op(s, a.Summary.Number, 10).Id, new MoveOperationRequest(Monday));
        s = await kit.Capacity().MoveAsync(Op(s, b.Summary.Number, 10).Id, new MoveOperationRequest(Monday));
        Assert.True(Op(s, a.Summary.Number, 10).IsPinned);
        var week = (await kit.Capacity().GetAsync(Monday)).Load.Single(l => l.Code == "ASM").Weeks[0];
        Assert.Equal((80m, 40m, true), (week.Load, week.Capacity, week.IsOverloaded));

        // Unpinned again, finite loading spreads them back out; infinite loading stacks them and shows the overload.
        s = await kit.Capacity().MoveAsync(Op(s, a.Summary.Number, 10).Id, new MoveOperationRequest(null));
        s = await kit.Capacity().MoveAsync(Op(s, b.Summary.Number, 10).Id, new MoveOperationRequest(null));
        Assert.False(Op(s, a.Summary.Number, 10).IsPinned);
        s = await kit.Capacity().RunAsync(new RunScheduleRequest(ScheduleMode.Forward, false, Monday));
        Assert.True((await kit.Capacity().GetAsync(Monday)).Load.Single(l => l.Code == "ASM").Weeks[0].IsOverloaded);
        Assert.False(s.Finite);
    }
}
