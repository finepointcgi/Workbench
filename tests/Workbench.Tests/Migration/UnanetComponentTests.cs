using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Imports;
using Workbench.Ui.Setup.Import;
using Workbench.Ui.Setup.Migration;

namespace Workbench.Tests.Migration;

public sealed class UnanetComponentTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public async Task The_wizard_offers_unanet_and_explains_how_unanet_words_map_before_anything_is_imported()
    {
        await using var ctx = NewContext();
        IReadOnlyList<ImportKindDto> kinds =
        [
            new(ImportSource.Unanet, ImportKind.Employees, "People", "Unanet people become employees.", [new("EmployeeNumber", "Employee ID", true, "", ["EmployeeNumber", "Employee ID"])], "samples/import/unanet/people.csv"),
            new(ImportSource.Unanet, ImportKind.Projects, "Projects and tasks", "Projects and WBS.", [], null),
        ];
        ctx.Services.AddSingleton(Workbench.Tests.Identity.Fake<IImportService>.Create(("ListKindsAsync", _ => kinds)));

        var page = ctx.Render<ImportPage>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=source-Unanet]")));
        page.Find("[data-testid=source-Unanet]").Click();

        // Step 2 of the wizard: the plain-words mapping table is on screen with the order to import in.
        var guide = page.Find("[data-testid=unanet-guide]");
        Assert.Contains("Project (customer agreement part)", guide.TextContent);
        Assert.Contains("Contract: type, period of performance, value, funding", guide.TextContent);
        Assert.Contains("a project without tasks gets one general charge code", guide.TextContent.ToLowerInvariant());
        Assert.Contains("Approved timesheet", guide.TextContent);

        page.Find("[data-testid=kind-Employees]").Click();
        page.WaitForAssertion(() => Assert.Contains("Upload People", page.Markup));
        Assert.Contains("Unanet report or list", page.Markup);
        Assert.NotEmpty(page.FindAll("[data-testid=import-file]"));
        Assert.Contains("font-weight:600", page.Find("[data-testid=unanet-map-Employees]").GetAttribute("style"));
    }

    [Fact]
    public async Task The_unanet_checklist_lists_every_step_in_order_with_its_import_link()
    {
        await using var ctx = NewContext();
        ctx.Services.AddSingleton(Workbench.Tests.Identity.Fake<IImportService>.Create(("ListJobsAsync", _ => (IReadOnlyList<ImportJobDto>)[])));

        var page = ctx.Render<UnanetMigrationPage>();

        var steps = page.FindAll("[data-testid=unanet-steps] [data-step]");
        Assert.Equal(15, steps.Count);
        Assert.Equal("Timesheets", steps[^1].GetAttribute("data-step") is "Timesheets" ? "Timesheets" : "");
        Assert.Contains("source=Unanet&amp;kind=Timesheets", steps[^1].InnerHtml);
        Assert.NotEmpty(page.FindAll("[data-testid=open-unanet-reconcile]"));
        Assert.NotEmpty(page.FindAll("[data-testid=unanet-guide]"));
    }

    [Fact]
    public void The_dry_run_is_described_in_plain_words_by_what_it_adds_changes_and_leaves()
    {
        var plan = new List<ImportPlanRowDto>
        {
            new("p.csv", 2, ImportRowAction.Create, "Employee", "H1001", ""),
            new("p.csv", 3, ImportRowAction.Create, "Employee", "H1002", ""),
            new("p.csv", 2, ImportRowAction.Create, "Cost rate", "H1001 2026-01-01", ""),
            new("p.csv", 4, ImportRowAction.Create, "Labor category", "PM", ""),
            new("p.csv", 5, ImportRowAction.Update, "Employee", "H1003", ""),
            new("p.csv", 6, ImportRowAction.Skip, "Employee", "H1004", ""),
        };
        var counts = new ImportCountsDto(5, 4, 1, 1, 2);

        Assert.Equal("This import will add 2 employees, 1 cost rate and 1 labor category and change 1 employee. Leave 1 as they are. 2 rows have problems and will be left out.",
            ImportFormat.PlainWords(ImportKind.Employees, counts, imported: false, plan));
        Assert.StartsWith("Added 2 employees, 1 cost rate and 1 labor category; changed 1 employee.", ImportFormat.PlainWords(ImportKind.Employees, counts, imported: true, plan));
        Assert.Equal("This import will add nothing and change nothing.", ImportFormat.PlainWords(ImportKind.Timesheets, new ImportCountsDto(0, 0, 0, 0, 0), false, []));
    }
}
