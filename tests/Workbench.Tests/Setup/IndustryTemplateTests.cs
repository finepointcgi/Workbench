using Workbench.Application;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Setup;

/// <summary>The industry starters (consulting, agency retainer, software implementation, engineering design) in "Start a project".</summary>
public sealed class IndustryTemplateTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2);

    private static IProjectStartService Start(SeededHost seeded) => seeded.As(Admin).Host.Get<IProjectStartService>();

    [Fact]
    public async Task The_four_industry_starters_are_listed_with_their_outline_and_usual_roles()
    {
        await using var seeded = await SeededHost.CreateAsync();
        await ProjectAccountingFixture.SeedAsync(seeded);

        var templates = (await Start(seeded).ListTemplatesAsync()).ToDictionary(t => t.Key);

        Assert.True(new[] { "phases", "services", "product" }.All(templates.ContainsKey)); // The originals stay.
        Assert.Equal(["Engagement management", "Discovery", "Design", "Delivery", "Handover"], templates["consulting"].Outline.Where(l => !l.StartsWith(' ')));
        Assert.Equal(["Account management", "Monthly deliverables", "Reporting and review"], templates["retainer"].Outline.Where(l => !l.StartsWith(' ')));
        Assert.Contains("Hypercare", templates["implementation"].Outline);
        Assert.Equal(["Concept design", "Preliminary design", "Detailed design", "Construction support"], templates["engineering"].Outline.Where(l => !l.StartsWith(' ')));
        Assert.All(new[] { "consulting", "retainer", "implementation", "engineering" }, key => Assert.NotEmpty(templates[key].LaborCategories!));
        Assert.Contains("Engagement Manager", templates["consulting"].LaborCategories!);
    }

    [Theory]
    [InlineData("consulting", 5, 13)]
    [InlineData("retainer", 3, 7)]
    [InlineData("implementation", 6, 13)]
    [InlineData("engineering", 4, 9)]
    public async Task Each_starter_creates_a_numbered_wbs_and_a_plan_with_the_leaves_chargeable(string key, int phases, int leaves)
    {
        await using var seeded = await SeededHost.CreateAsync();
        await ProjectAccountingFixture.SeedAsync(seeded);

        var project = await Start(seeded).StartAsync(new StartProjectRequest("3101", "Industry start", From: ProjectStartFrom.Template, TemplateKey: key, Start: Mon));

        var wbs = await seeded.Host.Get<IProjectService>().ListWbsAsync(project.Id);
        var items = await seeded.Host.Get<Workbench.Contracts.Sheets.IItemService>().ListAsync(project.SheetId!.Value);
        Assert.Equal(phases, wbs.Count(w => w.ParentId is null));
        Assert.Equal(leaves, wbs.Count(w => w.IsChargeable));
        Assert.All(wbs, w => Assert.NotNull(w.ItemId));
        Assert.Equal(wbs.Count, items.Count);
        Assert.StartsWith("3101.01", wbs.OrderBy(w => w.Code, StringComparer.Ordinal).First().Code);
    }

    [Fact]
    public async Task A_template_on_a_contract_takes_the_usual_roles_that_exist_as_labor_categories()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        Guid manager;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var category = new LaborCategory { Code = "EM", Name = "Engagement Manager" };
            var inactive = new LaborCategory { Code = "OLD", Name = "Consultant", IsActive = false };
            db.Set<LaborCategory>().AddRange(category, inactive);
            await db.SaveChangesAsync();
            manager = category.Id;
        }

        var project = await Start(seeded).StartAsync(new StartProjectRequest("3102", "Consulting on a contract", fixture.Contract2, ProjectStartFrom.Template, "consulting", Start: Mon));

        var plan = await seeded.Host.Get<IProjectPlanService>().GetAsync(project.Id);
        Assert.Equal([manager], plan.Categories!.Where(c => c.Selected).Select(c => c.Id));
    }
}
