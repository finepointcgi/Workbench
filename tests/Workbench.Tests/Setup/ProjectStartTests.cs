using ClosedXML.Excel;
using Workbench.Application;
using Workbench.Application.Scheduling;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Ceilings;
using Workbench.Domain.ProjectAccounting;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Setup;

/// <summary>Starting a project in one step: code and name, a starter outline or a copy, and the plan ready to edit.</summary>
public sealed class ProjectStartTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2);

    private static IProjectStartService Start(SeededHost seeded, string email = Admin) => seeded.As(email).Host.Get<IProjectStartService>();

    private static async Task<(IReadOnlyList<WbsElementDto> Wbs, IReadOnlyList<ItemDto> Items)> PlanAsync(SeededHost seeded, ProjectDto project)
    {
        var wbs = await seeded.Host.Get<IProjectService>().ListWbsAsync(project.Id);
        var items = await seeded.Host.Get<IItemService>().ListAsync(project.SheetId!.Value);
        return (wbs, items);
    }

    [Fact]
    public async Task A_starter_outline_creates_the_numbered_wbs_and_the_plan()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await ProjectAccountingFixture.SeedAsync(seeded);

        var project = await Start(seeded).StartAsync(new StartProjectRequest("3003", "Apollo", From: ProjectStartFrom.Template, TemplateKey: "phases", Start: Mon));

        Assert.NotNull(project.SheetId);
        var (wbs, items) = await PlanAsync(seeded, project);
        Assert.Equal(["3003.01", "3003.01.01", "3003.01.02", "3003.02", "3003.03", "3003.03.01"], wbs.Select(w => w.Code).Order(StringComparer.Ordinal));
        Assert.Equal("Kickoff", wbs.Single(w => w.Code == "3003.01.01").Name);
        Assert.All(wbs, w => Assert.NotNull(w.ItemId)); // every element is a plan task
        Assert.Equal("3003.01.02", items.Single(i => i.Title == "Status reporting").CostpointWbsId);
        Assert.All(items, i => Assert.Equal(Mon, i.Start));
    }

    [Fact]
    public async Task Copying_a_project_moves_its_plan_to_the_new_start_under_the_new_code()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await ProjectAccountingFixture.SeedAsync(seeded);
        var source = await Start(seeded).StartAsync(new StartProjectRequest("3003", "Apollo", From: ProjectStartFrom.Template, TemplateKey: "product", Start: Mon));
        var items = seeded.Host.Get<IItemService>();
        var plan = seeded.Host.Get<IPlanScheduleService>();
        var sourceItems = await items.ListAsync(source.SheetId!.Value);
        Guid Id(string title) => sourceItems.Single(i => i.Title == title).Id;
        await plan.SetDurationAsync(source.SheetId.Value, Id("Requirements"), 5);
        await plan.SetDurationAsync(source.SheetId.Value, Id("Design"), 3);
        await plan.SetPredecessorsAsync(source.SheetId.Value, Id("Design"), "1"); // after Requirements
        await items.CreateAsync(source.SheetId.Value, new CreateItemRequest("Design review", null, 2, Mon.AddDays(9), Mon.AddDays(9), IsMilestone: true));

        var copy = await Start(seeded).StartAsync(new StartProjectRequest("3004", "Apollo II", From: ProjectStartFrom.CopyProject,
            SourceProjectId: source.Id, Start: Mon.AddDays(28)));

        var (wbs, copied) = await PlanAsync(seeded, copy);
        Assert.Contains(wbs, w => w.Code == "3004.05.02" && w.Name == "Handover");
        var requirements = copied.Single(i => i.Title == "Requirements");
        var design = copied.Single(i => i.Title == "Design");
        Assert.Equal((Mon.AddDays(28), Mon.AddDays(32)), (requirements.Start!.Value, requirements.Finish!.Value));
        Assert.Equal((Mon.AddDays(35), Mon.AddDays(37)), (design.Start!.Value, design.Finish!.Value)); // still after Requirements, 3 days
        Assert.Equal("1", (await plan.GetAsync(copy.SheetId!.Value)).For(design.Id)!.Predecessors);
        var review = copied.Single(i => i.Title == "Design review"); // a plan task that isn't a WBS element is copied too
        Assert.True(review.IsMilestone);
        Assert.Equal(Mon.AddDays(37), review.Start);
    }

    [Fact]
    public async Task A_blank_plan_numbers_new_tasks_and_makes_them_charge_codes_in_one_step()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await ProjectAccountingFixture.SeedAsync(seeded);
        var project = await Start(seeded).StartAsync(new StartProjectRequest("3005", "Small job", Start: Mon));
        var items = seeded.Host.Get<IItemService>();
        var design = await items.CreateAsync(project.SheetId!.Value, new CreateItemRequest("Design"));
        await items.CreateAsync(project.SheetId.Value, new CreateItemRequest("Sketches", design.Id));

        var sync = seeded.Host.Get<IPlanSyncService>();
        var waiting = (await sync.DiffAsync(project.Id)).Differences.Where(d => d.Kind == PlanDifferenceKind.TaskWithoutWbs).ToList();
        Assert.Equal(2, waiting.Count);
        await sync.ApplyAsync(project.Id, new ApplyPlanSyncRequest(PlanSyncDirection.PlanToWbs, waiting.Select(d => d.Key).ToList()));

        var wbs = await seeded.Host.Get<IProjectService>().ListWbsAsync(project.Id);
        Assert.Equal(["3005.01", "3005.01.01"], wbs.Select(w => w.Code).Order(StringComparer.Ordinal)); // the codes the plan gave them
    }

    [Fact]
    public async Task Suggests_the_next_code_and_only_lets_project_creators_start_projects()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await ProjectAccountingFixture.SeedAsync(seeded);

        Assert.Equal("2003", await Start(seeded).SuggestCodeAsync()); // after 1001 and 2002
        Assert.NotEmpty(await Start(seeded).ListTemplatesAsync());
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Start(seeded, Viewer).StartAsync(new StartProjectRequest("9999", "Nope")));
        await Assert.ThrowsAsync<ArgumentException>(() => Start(seeded).StartAsync(new StartProjectRequest("", "No code")));
    }

    [Fact]
    public async Task Starting_from_a_contract_uses_its_dates_clins_and_labor_hours()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        Guid laborCategory;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var contract = db.Set<Contract>().Single(c => c.Id == fixture.Contract2);
            contract.PeriodStart = Mon;
            contract.PeriodEnd = Mon.AddYears(1);
            var base1 = new Clin { ContractId = contract.Id, Number = "0001", Description = "Engineering services", PeriodStart = Mon, PeriodEnd = Mon.AddYears(1) };
            var travel = new Clin { ContractId = contract.Id, Number = "0002", Description = "Travel", PeriodStart = Mon, PeriodEnd = Mon.AddYears(1) };
            db.Set<Clin>().AddRange(base1, travel);
            laborCategory = db.Set<LaborCategory>().Single().Id;
            db.Set<Ceiling>().Add(new Ceiling { Level = CeilingLevel.Clin, ContractId = contract.Id, ClinId = base1.Id, LaborCategoryId = laborCategory, HoursCeiling = 1200, CreatedBy = "test" });
            await db.SaveChangesAsync();
        }

        var project = await Start(seeded).StartAsync(new StartProjectRequest("3006", "Engineering", fixture.Contract2, ProjectStartFrom.Contract));

        Assert.Equal((Mon, Mon.AddYears(1)), (project.Start!.Value, project.Finish!.Value));
        var (wbs, items) = await PlanAsync(seeded, project);
        Assert.Equal(["3006.01 CLIN 0001 Engineering services", "3006.02 CLIN 0002 Travel"], wbs.OrderBy(w => w.Code).Select(w => $"{w.Code} {w.Name}"));
        Assert.Equal(2, items.Count);
        var mappings = await seeded.Host.Get<Workbench.Contracts.Contracts.IClinService>().ListWbsMappingsAsync(project.Id);
        Assert.Equal(2, mappings.Count(m => m.ClinId is not null));
        var budget = Assert.Single(await seeded.Host.Get<IBudgetService>().ListVersionsAsync(project.Id));
        var detail = await seeded.Host.Get<IBudgetService>().GetVersionAsync(budget.Id);
        var line = Assert.Single(detail!.Lines);
        Assert.Equal(("3006.01", laborCategory, 1200m), (line.WbsCode, line.LaborCategoryId!.Value, line.Hours));
    }

    [Fact]
    public async Task Starting_from_an_excel_plan_checks_it_first_and_numbers_its_tasks_under_the_new_code()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await ProjectAccountingFixture.SeedAsync(seeded);
        var content = PlanWorkbook.Write("x", []);
        using (var book = new XLWorkbook(new MemoryStream(content)))
        {
            var sheet = book.Worksheet("Plan");
            sheet.Cell(2, 3).Value = 1;
            sheet.Cell(2, 4).Value = "1001.07"; // another project's code
            sheet.Cell(2, 5).Value = "Design";
            sheet.Cell(2, 6).Value = 5;
            sheet.Cell(2, 7).Value = Mon.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(3, 3).Value = 2;
            sheet.Cell(3, 5).Value = "Sketches";
            sheet.Cell(3, 6).Value = 3;
            sheet.Cell(3, 7).Value = Mon.ToDateTime(TimeOnly.MinValue);
            using var stream = new MemoryStream();
            book.SaveAs(stream);
            content = stream.ToArray();
        }

        var file = new PlanImportRequest("plan.xlsx", content);
        var check = await Start(seeded).CheckImportAsync(file);
        Assert.False(check.HasErrors);
        Assert.Equal(2, check.Rows);

        var project = await Start(seeded).StartAsync(new StartProjectRequest("3007", "From Excel", From: ProjectStartFrom.ImportExcel, Import: file));

        var (_, items) = await PlanAsync(seeded, project);
        Assert.Equal("3007.01", items.Single(i => i.Title == "Design").CostpointWbsId);
        Assert.Equal("3007.01.01", items.Single(i => i.Title == "Sketches").CostpointWbsId);
        Assert.Equal(Mon, items.Single(i => i.Title == "Design").Start); // a summary: from Sketches
    }

    [Fact]
    public async Task The_person_who_starts_a_project_manages_it_and_lands_on_a_plan_they_can_edit()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        await ProjectManagerRoleTests.GrantAsync(seeded, Owner, Workbench.Contracts.Identity.OrgRoles.ProjectManager); // Create projects

        var project = await Start(seeded, Admin).StartAsync(new StartProjectRequest("3008", "Admin's own", Start: Mon));
        Assert.Null(project.ProjectManagerId); // admin@ has no employee record: nobody to make manager

        var mine = await Start(seeded, Owner).StartAsync(new StartProjectRequest("3009", "Pat's project", Start: Mon));
        Assert.Equal(fixture.PmId, mine.ProjectManagerId);
        var item = await seeded.As(Owner).Host.Get<IItemService>().CreateAsync(mine.SheetId!.Value, new Workbench.Contracts.Sheets.CreateItemRequest("First task"));
        Assert.Equal("3009.01", item.CostpointWbsId);
    }
}
