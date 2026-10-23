using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Formulas;
using Workbench.Tests.Identity;

namespace Workbench.Tests.SheetExtras;

public sealed class SheetTemplateTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    [Fact]
    public async Task The_gallery_has_the_five_built_in_templates()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Viewer);

        var templates = await host.Templates().ListAsync();

        Assert.Equal(
            ["Project schedule", "Issue tracker", "RAID log", "Contract deliverables", "Onboarding checklist"],
            templates.Where(t => t.IsBuiltIn).Select(t => t.Name));
        Assert.All(templates, t => Assert.NotEmpty(t.ColumnNames));
    }

    [Theory]
    [InlineData("builtin:project-schedule")]
    [InlineData("builtin:issue-tracker")]
    [InlineData("builtin:raid-log")]
    [InlineData("builtin:contract-deliverables")]
    [InlineData("builtin:onboarding-checklist")]
    public async Task Every_built_in_template_makes_a_working_sheet(string templateId)
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);

        var created = await host.Templates().CreateSheetAsync(new CreateSheetFromTemplateRequest(templateId, "From template"));

        var sheet = await host.Host.Sheets.GetAsync(created.Id);
        Assert.Equal("Program Office", created.Workspace);
        var items = await host.Host.Items.ListAsync(created.Id);
        Assert.NotEmpty(items);
        var formulas = sheet!.Columns.Where(c => c.Formula is not null).Select(c => c.Id.ToString()).ToHashSet();
        Assert.All(items.SelectMany(i => i.Values.Where(v => formulas.Contains(v.Key))), v => Assert.False(FormulaErrors.IsError(v.Value), $"{templateId}: {v.Value}"));
    }

    [Fact]
    public async Task Built_in_dates_start_today_and_formulas_compute()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);

        var created = await host.Templates().CreateSheetAsync(new CreateSheetFromTemplateRequest("builtin:issue-tracker", "Issues"));

        var sheet = (await host.Host.Sheets.GetAsync(created.Id))!;
        var reported = sheet.Columns.Single(c => c.Name == "Reported on");
        var age = sheet.Columns.Single(c => c.Name == "Days open");
        var first = (await host.Host.Items.ListAsync(created.Id)).OrderBy(i => i.Order).First();
        Assert.Equal(Today.ToString("yyyy-MM-dd"), first.Cell(reported));
        Assert.Equal("0", first.Cell(age));

        var plan = await host.Templates().CreateSheetAsync(new CreateSheetFromTemplateRequest("builtin:project-schedule", "Plan"));
        var plannedItems = await host.Host.Items.ListAsync(plan.Id);
        Assert.Equal(Today, plannedItems.Single(i => i.Title == "Kickoff meeting").Start);
        Assert.True(plannedItems.Single(i => i.Title == "Kickoff meeting").IsMilestone);
        Assert.Equal(2, plannedItems.Count(i => i.ParentId is null && i.Title is "Planning" or "Execution"));
    }

    [Fact]
    public async Task Without_rows_only_the_columns_are_made()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);

        var created = await host.Templates().CreateSheetAsync(new CreateSheetFromTemplateRequest("builtin:raid-log", "Risks", IncludeRows: false));

        Assert.Empty(await host.Host.Items.ListAsync(created.Id));
        Assert.Equal(8, (await host.Host.Sheets.GetAsync(created.Id))!.Columns.Count);
    }

    [Fact]
    public async Task Save_as_template_shares_it_with_the_workspace_and_rebases_dates()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var status = await host.AddColumnAsync(host.SheetId, "Status", ColumnKind.Dropdown, "Open", "Done");
        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);
        await host.Host.Items.UpdateAsync(host.SheetId, kickoff.Id, new UpdateItemRequest(kickoff.Title, new DateOnly(2025, 3, 3), new DateOnly(2025, 3, 7), null,
            new Dictionary<string, string?> { [status.Id.ToString()] = "Open" }));

        var saved = await host.Templates().SaveAsTemplateAsync(host.SheetId, new SaveSheetTemplateRequest("Our schedule", IncludeRows: true));

        host.As(SeededHost.Viewer);
        Assert.Contains(await host.Templates().ListAsync(), t => t.Id == saved.Id && t.Workspace == "Program Office" && !t.CanDelete);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Templates().DeleteAsync(Guid.Parse(saved.Id)));
        host.As(SeededHost.Outsider);
        Assert.DoesNotContain(await host.Templates().ListAsync(), t => t.Id == saved.Id);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Templates().CreateSheetAsync(new CreateSheetFromTemplateRequest(saved.Id, "Mine")));

        host.As(SeededHost.Editor);
        var created = await host.Templates().CreateSheetAsync(new CreateSheetFromTemplateRequest(saved.Id, "Next project"));
        var item = Assert.Single(await host.Host.Items.ListAsync(created.Id));
        Assert.Equal(Today, item.Start);
        Assert.Equal(Today.AddDays(4), item.Finish);
        var newStatus = (await host.Host.Sheets.GetAsync(created.Id))!.Columns.Single(c => c.Name == "Status");
        Assert.Equal("Open", item.Cell(newStatus));
        Assert.NotEqual(status.Id, newStatus.Id);

        host.As(SeededHost.Owner);
        await host.Templates().DeleteAsync(Guid.Parse(saved.Id));
        Assert.DoesNotContain(await host.Templates().ListAsync(), t => t.Id == saved.Id);
    }

    [Fact]
    public async Task Viewers_cannot_save_templates()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Viewer);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Templates().SaveAsTemplateAsync(host.SheetId, new SaveSheetTemplateRequest("Nope")));
    }

    [Fact]
    public async Task Copy_sheet_keeps_columns_rows_hierarchy_formulas_and_dependencies()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var hours = await host.AddColumnAsync(host.SheetId, "Hours", ColumnKind.Number);
        var doubled = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Doubled", ColumnKind.Number, Formula: "[Hours] * 2"));
        var child = await host.Host.Items.CreateAsync(host.SheetId, new CreateItemRequest("Agenda", host.ItemId, Start: new DateOnly(2026, 11, 2), Finish: new DateOnly(2026, 11, 3),
            Values: new Dictionary<string, string?> { [hours.Id.ToString()] = "3" }));
        var next = await host.Host.Items.CreateAsync(host.SheetId, new CreateItemRequest("Follow up", Start: new DateOnly(2026, 11, 4), Finish: new DateOnly(2026, 11, 4)));
        await host.Host.Get<IDependencyService>().AddAsync(host.SheetId, new CreateDependencyRequest(child.Id, next.Id, DependencyKind.FinishToStart, 0));

        var copy = await host.Templates().CopyAsync(host.SheetId, new CopySheetRequest("Schedule (copy)"));

        Assert.Equal("Program Office", copy.Workspace);
        var items = await host.Host.Items.ListAsync(copy.Id);
        Assert.Equal(3, items.Count);
        var copiedChild = items.Single(i => i.Title == "Agenda");
        Assert.Equal(items.Single(i => i.Title == "Kickoff").Id, copiedChild.ParentId);
        Assert.Equal(new DateOnly(2026, 11, 2), copiedChild.Start);
        var columns = (await host.Host.Sheets.GetAsync(copy.Id))!.Columns;
        Assert.Equal("6", copiedChild.Cell(columns.Single(c => c.Name == "Doubled")));
        Assert.DoesNotContain(items, i => i.Id == child.Id);
        var dependency = Assert.Single(await host.Host.Get<IDependencyService>().ListAsync(copy.Id));
        Assert.Equal(copiedChild.Id, dependency.PredecessorId);
        Assert.Equal(doubled.Formula, columns.Single(c => c.Name == "Doubled").Formula);

        var empty = await host.Templates().CopyAsync(host.SheetId, new CopySheetRequest("Columns only", IncludeRows: false));
        Assert.Empty(await host.Host.Items.ListAsync(empty.Id));
        Assert.Equal(2, (await host.Host.Sheets.GetAsync(empty.Id))!.Columns.Count);
    }

    [Fact]
    public async Task A_copy_leaves_out_what_the_copier_cannot_see()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var budget = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Budget", ColumnKind.Currency, IsRestricted: true));
        await host.SetAsync(host.SheetId, await host.ItemAsync(host.SheetId, host.ItemId), budget, "5000");

        host.As(SeededHost.Viewer); // can copy (Viewer), into a workspace they can edit: none here
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Templates().CopyAsync(host.SheetId, new CopySheetRequest("Mine")));

        host.As(SeededHost.Editor);
        var copy = await host.Templates().CopyAsync(host.SheetId, new CopySheetRequest("Editor copy"));
        host.As(SeededHost.Owner);
        Assert.DoesNotContain((await host.Host.Sheets.GetAsync(copy.Id))!.Columns, c => c.Name == "Budget");
        Assert.DoesNotContain("5000", (await host.Host.Items.ListAsync(copy.Id)).SelectMany(i => i.Values.Values));
    }

    [Fact]
    public async Task Outsiders_cannot_copy_a_sheet_they_cannot_see()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Templates().CopyAsync(host.FinanceSheetId, new CopySheetRequest("Budget copy")));
    }
}
