using Workbench.Application;
using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts.Sheets;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Scheduling;

/// <summary>Automatic WBS codes on a project's plan: numbered under the parent, kept in step with the outline.</summary>
public sealed class WbsNumberingTests
{
    private readonly IWorkbenchDbContextFactory _factory = TestDb.CreateFactory();
    private readonly ItemService _items;
    private readonly PlanScheduleService _plan;

    public WbsNumberingTests()
    {
        _items = new ItemService(_factory, TimeProvider.System);
        _plan = new PlanScheduleService(_factory, _items, TimeProvider.System);
    }

    private async Task<Guid> ProjectPlanAsync(string code = "2001")
    {
        var sheetId = (await new SheetService(_factory, TimeProvider.System).CreateAsync(new CreateSheetRequest($"{code} plan", null, true))).Id;
        await using var db = await _factory.CreateAsync();
        db.Set<Project>().Add(new Project { Code = code, Name = "Apollo", SheetId = sheetId });
        await db.SaveChangesAsync();
        return sheetId;
    }

    private async Task<Dictionary<string, string?>> CodesAsync(Guid sheetId) =>
        (await _items.ListAsync(sheetId)).ToDictionary(i => i.Title, i => i.CostpointWbsId);

    private async Task<ItemDto> AddAsync(Guid sheetId, string title, Guid? parent = null) =>
        await _items.CreateAsync(sheetId, new CreateItemRequest(title, parent));

    [Fact]
    public async Task New_tasks_are_numbered_under_their_parent()
    {
        var sheet = await ProjectPlanAsync();
        var manage = await AddAsync(sheet, "Manage");
        var build = await AddAsync(sheet, "Build");
        await AddAsync(sheet, "Design", build.Id);
        await AddAsync(sheet, "Code", build.Id);
        await _items.CreateAsync(sheet, new CreateItemRequest("Kickoff", manage.Id));

        var codes = await CodesAsync(sheet);
        Assert.Equal("2001.01", codes["Manage"]);
        Assert.Equal("2001.01.01", codes["Kickoff"]);
        Assert.Equal("2001.02", codes["Build"]);
        Assert.Equal("2001.02.01", codes["Design"]);
        Assert.Equal("2001.02.02", codes["Code"]);
    }

    [Fact]
    public async Task Indent_outdent_and_insert_renumber_the_codes()
    {
        var sheet = await ProjectPlanAsync();
        await AddAsync(sheet, "Manage");
        var build = await AddAsync(sheet, "Build");
        var design = await AddAsync(sheet, "Design", build.Id);
        var test = await AddAsync(sheet, "Test");

        await _plan.IndentAsync(sheet, build.Id); // Build under Manage, Design with it
        var codes = await CodesAsync(sheet);
        Assert.Equal("2001.01.01", codes["Build"]);
        Assert.Equal("2001.01.01.01", codes["Design"]);
        Assert.Equal("2001.02", codes["Test"]);

        await _plan.OutdentAsync(sheet, design.Id); // back up a level, after Build
        Assert.Equal("2001.01.02", (await CodesAsync(sheet))["Design"]);

        await _items.CreateAsync(sheet, new CreateItemRequest("Plan", null, 0)); // inserted first: everything after moves along
        codes = await CodesAsync(sheet);
        Assert.Equal("2001.01", codes["Plan"]);
        Assert.Equal("2001.02", codes["Manage"]);
        Assert.Equal("2001.02.01", codes["Build"]);
        Assert.Equal("2001.03", codes["Test"]);
        _ = test;
    }

    [Fact]
    public async Task Typed_codes_and_charge_codes_are_kept_and_numbering_works_around_them()
    {
        var sheet = await ProjectPlanAsync();
        var manage = await AddAsync(sheet, "Manage");
        var travel = await AddAsync(sheet, "Travel");
        var build = await AddAsync(sheet, "Build");
        await using (var db = await _factory.CreateAsync())
        {
            // Build is already a charge code.
            var project = db.Set<Project>().Single(p => p.SheetId == sheet);
            db.Set<WbsElement>().Add(new WbsElement { ProjectId = project.Id, Code = "2001.03", Name = "Build", ItemId = build.Id });
            await db.SaveChangesAsync();
        }

        await _items.UpdateAsync(sheet, travel.Id, new UpdateItemRequest("Travel", null, null, "2001-ODC", travel.Values));
        await _items.CreateAsync(sheet, new CreateItemRequest("Report", null, 0)); // first: the others would move along

        var codes = await CodesAsync(sheet);
        Assert.Equal("2001.01", codes["Report"]);
        Assert.Equal("2001.02", codes["Manage"]);
        Assert.Equal("2001-ODC", codes["Travel"]); // typed
        Assert.Equal("2001.03", codes["Build"]); // a charge code keeps its code

        // Clearing a typed code gives the task an automatic one again (2001.03 is taken).
        await _items.UpdateAsync(sheet, travel.Id, new UpdateItemRequest("Travel", null, null, null, travel.Values));
        Assert.Equal("2001.04", (await CodesAsync(sheet))["Travel"]);
        _ = manage;
    }

    [Fact]
    public async Task Sheets_that_are_not_a_project_plan_are_not_numbered()
    {
        var sheetId = (await new SheetService(_factory, TimeProvider.System).CreateAsync(new CreateSheetRequest("Tracker", null, true))).Id;
        await AddAsync(sheetId, "Anything");
        Assert.Null((await CodesAsync(sheetId))["Anything"]);
    }

    [Fact]
    public void Next_skips_used_codes()
    {
        Assert.Equal("1001.02.04", WbsNumbering.Next("1001.02", ["1001.02.01", "1001.02.03", "1001-X"]));
        Assert.Equal("1001.01", WbsNumbering.Next("1001", []));
    }
}
