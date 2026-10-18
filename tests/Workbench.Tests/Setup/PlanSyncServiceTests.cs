using Workbench.Application.ProjectAccounting;
using Workbench.Application.Sheets;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Setup;

public sealed class PlanSyncServiceTests
{
    private readonly ProjectService _projects;
    private readonly SheetService _sheets;
    private readonly ItemService _items;
    private readonly PlanSyncService _sync;
    private readonly AuditLogService _audit;

    public PlanSyncServiceTests()
    {
        var factory = TestDb.CreateFactory();
        var recorder = new AuditRecorder(new TestUser { Email = "finance@example.com" }, TimeProvider.System);
        _projects = new ProjectService(factory, recorder);
        _sheets = new SheetService(factory, TimeProvider.System);
        _items = new ItemService(factory, TimeProvider.System);
        _sync = new PlanSyncService(factory, recorder, _sheets, _items, _projects);
        _audit = new AuditLogService(factory);
    }

    /// <summary>1001.01 › (1001.01.01 › 1001.01.01.01, 1001.01.02).</summary>
    private async Task<(ProjectDto Project, WbsElementDto Root, WbsElementDto Design, WbsElementDto Detail, WbsElementDto Build)> NewProjectAsync()
    {
        var project = await _projects.CreateProjectAsync(new SaveProjectRequest("1001", "Modernization", null, null, ProjectState.Active, null, null));
        var root = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(null, "1001.01", "Program", IsChargeable: false));
        var design = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.01", "Design"));
        var detail = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(design.Id, "1001.01.01.01", "Detailed design"));
        var build = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.02", "Build"));
        return (project, root, design, detail, build);
    }

    [Fact]
    public async Task Create_plan_sheet_mirrors_the_wbs_tree_and_links_everything()
    {
        var (project, root, design, detail, build) = await NewProjectAsync();

        var updated = await _sync.CreatePlanSheetAsync(project.Id);

        Assert.NotNull(updated.SheetId);
        var sheet = await _sheets.GetAsync(updated.SheetId!.Value);
        Assert.True(sheet!.Summary.SchedulingEnabled);
        var items = await _items.ListAsync(updated.SheetId.Value);
        var wbs = (await _projects.ListWbsAsync(project.Id)).ToDictionary(w => w.Id);
        Assert.Equal(4, items.Count);
        foreach (var element in wbs.Values)
        {
            var item = Assert.Single(items, i => i.Id == element.ItemId);
            Assert.Equal(element.Name, item.Title);
            Assert.Equal(element.Code, item.CostpointWbsId);
            Assert.Equal(element.ParentId is { } p ? wbs[p].ItemId : null, item.ParentId);
        }

        Assert.Equal(["Design", "Build"], items.Where(i => i.ParentId == wbs[root.Id].ItemId).OrderBy(i => i.Order).Select(i => i.Title));
        Assert.Empty((await _sync.DiffAsync(project.Id)).Differences);
        Assert.Contains(await _audit.ListAsync("Project", project.Id), a => a.Action == "PlanSheetLinked");
        await Assert.ThrowsAsync<ArgumentException>(() => _sync.CreatePlanSheetAsync(project.Id));
    }

    [Fact]
    public async Task Diff_reports_missing_tasks_unlinked_tasks_and_renames()
    {
        var (project, root, design, _, build) = await NewProjectAsync();
        var sheetId = (await _sync.CreatePlanSheetAsync(project.Id)).SheetId!.Value;
        var items = await _items.ListAsync(sheetId);
        var designItem = items.Single(i => i.Title == "Design");
        await _items.UpdateAsync(sheetId, designItem.Id, new UpdateItemRequest("Design and prototyping", null, null, designItem.CostpointWbsId, designItem.Values));
        var extra = await _items.CreateAsync(sheetId, new CreateItemRequest("Test", items.Single(i => i.Title == "Program").Id));
        var added = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.03", "Deploy"));

        var report = await _sync.DiffAsync(project.Id);

        Assert.Equal(3, report.Differences.Count);
        var missing = Assert.Single(report.Differences, d => d.Kind == PlanDifferenceKind.WbsWithoutTask);
        Assert.Equal(added.Id, missing.WbsElementId);
        Assert.True(missing.CanApplyToPlan);
        Assert.False(missing.CanApplyToWbs);
        var unlinked = Assert.Single(report.Differences, d => d.Kind == PlanDifferenceKind.TaskWithoutWbs);
        Assert.Equal(extra.Id, unlinked.ItemId);
        var renamed = Assert.Single(report.Differences, d => d.Kind == PlanDifferenceKind.Renamed);
        Assert.Equal((design.Id, "Design", "Design and prototyping"), (renamed.WbsElementId!.Value, renamed.WbsName, renamed.ItemTitle));
        Assert.True(renamed.CanApplyToPlan && renamed.CanApplyToWbs);
    }

    [Fact]
    public async Task Apply_wbs_to_plan_creates_tasks_under_their_parent_and_renames_tasks()
    {
        var (project, _, design, detail, _) = await NewProjectAsync();
        var sheetId = (await _sync.CreatePlanSheetAsync(project.Id)).SheetId!.Value;
        var designItem = (await _items.ListAsync(sheetId)).Single(i => i.Title == "Design");
        await _items.UpdateAsync(sheetId, designItem.Id, new UpdateItemRequest("Renamed in plan", null, null, null, designItem.Values));
        var added = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(detail.Id, "1001.01.01.01.01", "Interfaces"));

        var report = await _sync.ApplyAsync(project.Id, new ApplyPlanSyncRequest(PlanSyncDirection.WbsToPlan));

        Assert.Empty(report.Differences);
        var items = await _items.ListAsync(sheetId);
        Assert.Equal("Design", items.Single(i => i.Id == designItem.Id).Title);
        Assert.Equal("1001.01.01", items.Single(i => i.Id == designItem.Id).CostpointWbsId);
        var wbs = (await _projects.ListWbsAsync(project.Id)).ToDictionary(w => w.Id);
        var newItem = items.Single(i => i.Id == wbs[added.Id].ItemId);
        Assert.Equal(("Interfaces", "1001.01.01.01.01", wbs[detail.Id].ItemId), (newItem.Title, newItem.CostpointWbsId, newItem.ParentId));
        Assert.Equal("Design", wbs[design.Id].Name);
    }

    [Fact]
    public async Task Apply_plan_to_wbs_renames_elements_and_creates_elements_for_new_tasks()
    {
        var (project, root, design, _, build) = await NewProjectAsync();
        var sheetId = (await _sync.CreatePlanSheetAsync(project.Id)).SheetId!.Value;
        var items = await _items.ListAsync(sheetId);
        var buildItem = items.Single(i => i.Title == "Build");
        await _items.UpdateAsync(sheetId, buildItem.Id, new UpdateItemRequest("Build and integrate", null, null, buildItem.CostpointWbsId, buildItem.Values));
        var test = await _items.CreateAsync(sheetId, new CreateItemRequest("Test", items.Single(i => i.Title == "Program").Id));
        var unit = await _items.CreateAsync(sheetId, new CreateItemRequest("Unit tests", test.Id));
        var topLevel = await _items.CreateAsync(sheetId, new CreateItemRequest("Transition"));

        var report = await _sync.ApplyAsync(project.Id, new ApplyPlanSyncRequest(PlanSyncDirection.PlanToWbs));

        Assert.Empty(report.Differences);
        var wbs = await _projects.ListWbsAsync(project.Id);
        Assert.Equal("Build and integrate", wbs.Single(w => w.Id == build.Id).Name);
        var testElement = wbs.Single(w => w.ItemId == test.Id);
        Assert.Equal(("1001.01.03", root.Id, "Test"), (testElement.Code, testElement.ParentId!.Value, testElement.Name));
        var unitElement = wbs.Single(w => w.ItemId == unit.Id);
        Assert.Equal(("1001.01.03.01", testElement.Id), (unitElement.Code, unitElement.ParentId!.Value));
        Assert.Equal("1001.02", wbs.Single(w => w.ItemId == topLevel.Id).Code);
        Assert.Equal("1001.01.03.01", (await _items.ListAsync(sheetId)).Single(i => i.Id == unit.Id).CostpointWbsId);
    }

    [Fact]
    public async Task Apply_only_touches_the_selected_differences()
    {
        var (project, root, _, _, _) = await NewProjectAsync();
        await _sync.CreatePlanSheetAsync(project.Id);
        var a = await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.03", "A"));
        await _projects.CreateWbsElementAsync(project.Id, new SaveWbsElementRequest(root.Id, "1001.01.04", "B"));

        var report = await _sync.ApplyAsync(project.Id, new ApplyPlanSyncRequest(PlanSyncDirection.WbsToPlan, [$"wbs:{a.Id}"]));

        var left = Assert.Single(report.Differences);
        Assert.Equal("B", left.WbsName);
    }
}
