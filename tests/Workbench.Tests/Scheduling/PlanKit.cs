using Workbench.Application;
using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Leave;

namespace Workbench.Tests.Scheduling;

/// <summary>The scheduling services over one test database, with helpers to make plans quickly.</summary>
internal sealed class PlanKit
{
    // Monday 5 October 2026.
    public static readonly DateOnly Mon = new(2026, 10, 5);

    public PlanKit()
    {
        Factory = TestDb.CreateFactory();
        Items = new ItemService(Factory, TimeProvider.System);
        Links = new DependencyService(Factory);
        Plan = new PlanScheduleService(Factory, Items, TimeProvider.System);
        Baselines = new BaselineService(Factory, TimeProvider.System);
    }

    public IWorkbenchDbContextFactory Factory { get; }
    public ItemService Items { get; }
    public DependencyService Links { get; }
    public PlanScheduleService Plan { get; }
    public BaselineService Baselines { get; }

    public async Task<Guid> SheetAsync(string name = "Plan", bool scheduling = true) =>
        (await new SheetService(Factory, TimeProvider.System).CreateAsync(new CreateSheetRequest(name, null, scheduling))).Id;

    /// <summary>A task of <paramref name="days"/> working days (Mon–Fri, no holidays) from <paramref name="start"/>; 0 is a milestone.</summary>
    public async Task<ItemDto> TaskAsync(Guid sheetId, string title, DateOnly? start, int days, Guid? parent = null)
    {
        DateOnly? finish = start is { } s ? WorkCalendar.Weekdays.Finish(s, days) : null;
        return await Items.CreateAsync(sheetId, new CreateItemRequest(title, parent, Start: start, Finish: finish, IsMilestone: days == 0 && start is not null));
    }

    public async Task<ItemDto> GetAsync(Guid sheetId, Guid id) => (await Items.ListAsync(sheetId)).Single(i => i.Id == id);

    public async Task<(DateOnly? Start, DateOnly? Finish)> DatesAsync(Guid sheetId, Guid id)
    {
        var item = await GetAsync(sheetId, id);
        return (item.Start, item.Finish);
    }

    public async Task HolidayAsync(DateOnly date, string name)
    {
        await using var db = await Factory.CreateAsync();
        db.Set<Holiday>().Add(new Holiday { Date = date, Name = name });
        await db.SaveChangesAsync();
    }

    public Task LinkAsync(Guid sheetId, Guid predecessor, Guid successor, DependencyKind type = DependencyKind.FinishToStart, int lag = 0) =>
        Links.AddAsync(sheetId, new CreateDependencyRequest(predecessor, successor, type, lag));
}
