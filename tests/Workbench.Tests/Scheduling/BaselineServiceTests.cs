using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Scheduling;

public sealed class BaselineServiceTests
{
    [Fact]
    public async Task Capture_snapshots_dates_that_later_edits_dont_change()
    {
        var factory = TestDb.CreateFactory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var sheets = new SheetService(factory, clock);
        var items = new ItemService(factory, clock);
        var baselines = new BaselineService(factory, clock);
        var sheetId = (await sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;
        var start = new DateOnly(2026, 10, 5);
        var task = await items.CreateAsync(sheetId, new CreateItemRequest("Design", Start: start, Finish: start.AddDays(4)));
        await items.CreateAsync(sheetId, new CreateItemRequest("Undated"));

        var first = await baselines.CaptureAsync(sheetId, new CaptureBaselineRequest(null));
        await items.UpdateAsync(sheetId, task.Id, new UpdateItemRequest("Design", start.AddDays(7), start.AddDays(11), null, task.Values));
        clock.Now = clock.Now.AddDays(1);
        var second = await baselines.CaptureAsync(sheetId, new CaptureBaselineRequest("  Re-plan "));

        Assert.Equal("Baseline 2026-10-01", first.Name);
        Assert.Equal(2, first.EntryCount);
        Assert.Equal(["Re-plan", "Baseline 2026-10-01"], (await baselines.ListAsync(sheetId)).Select(b => b.Name));
        var entry = (await baselines.GetAsync(sheetId, first.Id)).Entries.Single(e => e.ItemId == task.Id);
        Assert.Equal(start, entry.Start);
        Assert.Equal(start.AddDays(4), entry.Finish);
        Assert.Equal(start.AddDays(7), (await baselines.GetAsync(sheetId, second.Id)).Entries.Single(e => e.ItemId == task.Id).Start);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => baselines.GetAsync(Guid.NewGuid(), first.Id));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
