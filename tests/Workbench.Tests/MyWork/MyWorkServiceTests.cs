using Workbench.Application.MyWork;
using Workbench.Application.Sheets;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.MyWork;

public sealed class MyWorkServiceTests
{
    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Stands in for the auth-filtered sheet list: hides some sheets.</summary>
    internal sealed class FilteringSheetService(ISheetService inner, params Guid[] hidden) : ISheetService
    {
        public async Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            (await inner.ListAsync(cancellationToken)).Where(s => !hidden.Contains(s.Id)).ToList();

        public async Task<SheetDetail?> GetAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            hidden.Contains(sheetId) ? null : await inner.GetAsync(sheetId, cancellationToken);

        public Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(request, cancellationToken);
    }

    [Fact]
    public async Task Finds_my_items_on_visible_sheets_only()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var db = TestDb.CreateFactory();
        var sheets = new SheetService(db, clock);
        var columns = new ColumnService(db, clock);
        var items = new ItemService(db, clock);

        var schedule = await sheets.CreateAsync(new CreateSheetRequest("Schedule", null, true));
        var status = await columns.CreateAsync(schedule.Id, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Open", "Done"]));
        var owner = await columns.CreateAsync(schedule.Id, new CreateColumnRequest("Owner", ColumnKind.Person));
        await items.CreateAsync(schedule.Id, new CreateItemRequest("Mine", Finish: new DateOnly(2026, 10, 12),
            Values: new Dictionary<string, string?> { [owner.Id.ToString()] = "pm@example.com", [status.Id.ToString()] = "Open" }));
        await items.CreateAsync(schedule.Id, new CreateItemRequest("Theirs",
            Values: new Dictionary<string, string?> { [owner.Id.ToString()] = "other@example.com" }));

        var secret = await sheets.CreateAsync(new CreateSheetRequest("Secret", null, false));
        var secretOwner = await columns.CreateAsync(secret.Id, new CreateColumnRequest("Owner", ColumnKind.Person));
        await items.CreateAsync(secret.Id, new CreateItemRequest("Hidden",
            Values: new Dictionary<string, string?> { [secretOwner.Id.ToString()] = "pm@example.com" }));

        var service = new MyWorkService(new FilteringSheetService(sheets, secret.Id), items, new TestUser { Email = "pm@example.com" }, clock);
        var work = await service.GetAsync();

        var item = Assert.Single(work.Items);
        Assert.Equal(("Schedule", "Mine", "Open", MyWorkBucket.Overdue), (item.SheetName, item.Item.Title, item.Status, item.Bucket));
        Assert.Equal(status.Id, item.StatusColumn?.Id);
        Assert.Equal(new MyWorkCounts(1, 0, 1, 0), work.Counts);
        Assert.Equal(new DateOnly(2026, 10, 14), work.Today);
    }
}
