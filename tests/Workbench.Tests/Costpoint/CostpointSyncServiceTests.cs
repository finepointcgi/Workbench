using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Costpoint;
using Workbench.Costpoint;
using Workbench.Domain.Costpoint;

namespace Workbench.Tests.Costpoint;

public sealed class CostpointSyncServiceTests
{
    private readonly IWorkbenchDbContextFactory _factory = TestDb.CreateFactory();
    private readonly FakeSource _source = new();

    private CostpointSyncService NewSync() => new(_factory, _source, TimeProvider.System);

    [Fact]
    public async Task Second_run_with_the_same_data_changes_nothing()
    {
        var sync = NewSync();

        var first = await sync.RunAsync();
        var second = await sync.RunAsync();

        Assert.Equal(CpSyncStatus.Succeeded, first.Status);
        Assert.True(first.Inserted > 0);
        Assert.Equal(CpSyncStatus.Succeeded, second.Status);
        Assert.Equal((0, 0, 0), (second.Inserted, second.Updated, second.Deleted));

        await using var db = await _factory.CreateAsync();
        Assert.Equal(1, await db.Set<CpProject>().CountAsync());
        Assert.Equal(2, await db.Set<CpWbsElement>().CountAsync());
        Assert.Equal(2, await db.Set<CpSyncRun>().CountAsync());
    }

    [Fact]
    public async Task Changed_and_removed_rows_are_updated_and_deleted()
    {
        var sync = NewSync();
        await sync.RunAsync();

        _source.Wbs.RemoveAt(1);
        _source.Projects[0] = _source.Projects[0] with { Name = "Renamed" };
        _source.Actuals.Add(new CpActualRecord("P1", "P1.01", "2026-09", 2, 200));
        var run = await sync.RunAsync();

        Assert.Equal((1, 1, 1), (run.Inserted, run.Updated, run.Deleted));
        await using var db = await _factory.CreateAsync();
        Assert.Equal("Renamed", (await db.Set<CpProject>().SingleAsync()).Name);
        Assert.Equal(["P1.01"], await db.Set<CpWbsElement>().Select(w => w.WbsId).ToListAsync());
    }

    [Fact]
    public async Task Duplicate_actual_rows_are_summed()
    {
        _source.Actuals.Add(new CpActualRecord("P1", "P1.01", "2026-10", 4, 400));

        var run = await NewSync().RunAsync();

        Assert.Equal(1, run.Actuals);
        await using var db = await _factory.CreateAsync();
        var actual = await db.Set<CpActual>().SingleAsync();
        Assert.Equal((14m, 1400m), (actual.Hours, actual.Cost));
    }

    [Fact]
    public async Task Failure_is_recorded_and_leaves_the_mirror_untouched()
    {
        var sync = NewSync();
        await sync.RunAsync();
        _source.FailActuals = true;

        var run = await sync.RunAsync();

        Assert.Equal(CpSyncStatus.Failed, run.Status);
        Assert.Contains("export unavailable", run.Error);
        Assert.NotNull(run.FinishedAtUtc);
        await using var db = await _factory.CreateAsync();
        Assert.Equal(2, await db.Set<CpWbsElement>().CountAsync());
        Assert.Equal(CpSyncStatus.Failed, (await db.Set<CpSyncRun>().OrderByDescending(r => r.StartedAtUtc).FirstAsync()).Status);
    }

    private sealed class FakeSource : ICostpointSource
    {
        public List<CpProjectRecord> Projects { get; } = [new("P1", "Project 1", null, null, null, true)];

        public List<CpWbsRecord> Wbs { get; } = [new("P1", "P1.01", null, "Design", 1, true), new("P1", "P1.02", null, "Build", 1, true)];

        public List<CpActualRecord> Actuals { get; } = [new("P1", "P1.01", "2026-10", 10, 1000)];

        public bool FailActuals { get; set; }

        public string Name => "Fake";

        public Task<IReadOnlyList<CpProjectRecord>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CpProjectRecord>>(Projects.ToList());

        public Task<IReadOnlyList<CpWbsRecord>> GetWbsAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CpWbsRecord>>(Wbs.Where(w => w.ProjectId == projectId).ToList());

        public Task<IReadOnlyList<CpEmployeeRecord>> GetEmployeesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CpEmployeeRecord>>([new("E1", "Pat", "pat@example.com", null, "PM", true)]);

        public Task<IReadOnlyList<CpLaborCategoryRecord>> GetLaborCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CpLaborCategoryRecord>>([new("PM", "Program Manager")]);

        public Task<IReadOnlyList<CpBudgetRecord>> GetBudgetsAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CpBudgetRecord>>([new(projectId, "P1.01", null, 20, 3000)]);

        public Task<IReadOnlyList<CpActualRecord>> GetActualsAsync(string projectId, string fromPeriod, string toPeriod, CancellationToken cancellationToken = default) =>
            FailActuals
                ? throw new IOException("export unavailable")
                : Task.FromResult<IReadOnlyList<CpActualRecord>>(Actuals.Where(a => a.ProjectId == projectId).ToList());
    }
}
