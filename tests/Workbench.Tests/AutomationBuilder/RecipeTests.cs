using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Automations;
using Workbench.Tests.Identity;
using static Workbench.Tests.AutomationBuilder.AutomationKit;

namespace Workbench.Tests.AutomationBuilder;

/// <summary>The recipe gallery, request-approval on sheet rows, and who may see and change sheet automations.</summary>
public sealed class RecipeTests
{
    /// <summary>A sheet with Status (dropdown), Owner (person) and Due (date), and a second sheet to send rows to.</summary>
    private static async Task<(Guid Sheet, Guid Target, ColumnDto Status, ColumnDto Owner)> SheetsAsync(AutomationKit kit)
    {
        var sheets = kit.Host.Get<ISheetService>();
        var columns = kit.Host.Get<IColumnService>();
        var sheet = (await sheets.CreateAsync(new CreateSheetRequest("Tracker", null, false))).Id;
        var status = await columns.CreateAsync(sheet, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Not started", "In progress", "Blocked", "Ready for review", "Approved", "Rejected", "Done"]));
        var owner = await columns.CreateAsync(sheet, new CreateColumnRequest("Owner", ColumnKind.Person));
        await columns.CreateAsync(sheet, new CreateColumnRequest("Due", ColumnKind.Date));
        var target = (await sheets.CreateAsync(new CreateSheetRequest("Archive", null, false))).Id;
        return (sheet, target, status, owner);
    }

    [Fact]
    public async Task Every_recipe_with_its_blanks_filled_in_creates_a_valid_automation()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var (sheet, target, status, owner) = await SheetsAsync(kit);
        var workflow = await kit.Host.Get<IApprovalWorkflowService>().CreateRecordWorkflowAsync(new CreateRecordApprovalWorkflowRequest("Row review"));
        var connector = await kit.WithDbAsync(async db =>
        {
            var c = new Workbench.Domain.Integrations.IntegrationConnector { Kind = "teams", Name = "Billing", SettingsJson = "{}", CreatedBy = Admin };
            db.Set<Workbench.Domain.Integrations.IntegrationConnector>().Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });

        Assert.Contains(AutomationTemplates.All, t => t.Scope == AutomationRecipeScope.Sheet);
        Assert.Contains(AutomationTemplates.All, t => t.Category == "Time");
        foreach (var recipe in AutomationTemplates.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(recipe.Sentence));
            foreach (var blank in recipe.Blanks!)
            {
                Assert.Contains("{" + blank.Key + "}", recipe.Sentence); // Every blank is in the sentence...
            }

            // ...and the blanks people must fill in, filled in.
            var blanks = recipe.Blanks.Where(b => b.Default is null).ToDictionary(b => b.Key, b => (string?)(b.Kind switch
            {
                AutomationRecipeBlankKind.Person => "lead@example.com",
                AutomationRecipeBlankKind.Sheet => target.ToString(),
                AutomationRecipeBlankKind.ApprovalWorkflow => workflow.DocumentType,
                _ => throw new InvalidOperationException($"{recipe.Key}: no sample for {b.Kind}"),
            }));
            var request = await kit.Automations.BuildRecipeAsync(recipe.Key, recipe.Scope == AutomationRecipeScope.Sheet ? sheet : null, blanks);
            if (recipe.Key == "invoice-to-teams")
            {
                request = request with { Definition = request.Definition with { Actions = [request.Definition.Actions[0] with { ConnectorId = connector }] } };
            }

            var created = await kit.Automations.CreateAsync(request);
            Assert.Equal(recipe.Key, created.TemplateKey);
            Assert.Equal(recipe.Scope == AutomationRecipeScope.Sheet ? sheet : (Guid?)null, created.Definition.Trigger.SheetId);
        }

        // Column blanks pick the sheet's columns by name: "When Status changes to Done, notify Owner".
        var notify = await kit.Automations.BuildRecipeAsync("sheet-status-notify", sheet, new Dictionary<string, string?>());
        Assert.Equal(status.Id.ToString(), notify.Definition.Trigger.FieldKey);
        Assert.Equal("Done", notify.Definition.Trigger.ToValue);
        Assert.Equal(owner.Id.ToString(), Assert.Single(notify.Definition.Actions[0].Recipients!).Value);
        Assert.Equal("When Status is Done, notify Owner", notify.Name);

        // A recipe for a sheet needs one; a blank left empty that has no default fails validation.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Automations.BuildRecipeAsync("sheet-status-notify", null, new Dictionary<string, string?>()));
        var unfilled = await kit.Automations.BuildRecipeAsync("sheet-status-move", sheet, new Dictionary<string, string?>());
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Automations.CreateAsync(unfilled));

        // Admin › Automation lists them all, sheet ones naming their sheet.
        var all = await kit.Automations.ListAsync();
        Assert.Equal(AutomationTemplates.All.Count, all.Count);
        Assert.All(all.Where(a => a.SheetId is not null), a => Assert.Equal("Tracker", a.SheetName));
    }

    [Fact]
    public async Task Request_approval_routes_a_row_and_writes_the_decision_back_so_other_automations_follow()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var (sheet, target, status, _) = await SheetsAsync(kit);
        var workflow = await kit.Host.Get<IApprovalWorkflowService>().CreateRecordWorkflowAsync(new CreateRecordApprovalWorkflowRequest("Row review"));
        var request = await kit.Automations.BuildRecipeAsync("sheet-status-approval", sheet, new Dictionary<string, string?> { ["workflow"] = workflow.DocumentType });
        var route = await kit.Automations.CreateAsync(request);
        // When the decision lands in Status, archive the row.
        await kit.Automations.CreateAsync(new SaveAutomationRequest("Archive approved", new AutomationDefinition(
            new(AutomationTriggerKind.FieldChanged, FieldKey: status.Id.ToString(), ToValue: "Approved", SheetId: sheet), null,
            [new(AutomationActionKind.MoveRow, TargetSheetId: target)])));
        var items = kit.Host.Get<IItemService>();
        var row = await items.CreateAsync(sheet, new CreateItemRequest("Spec v2"));
        await kit.WithDbAsync(async db =>
        {
            var workspaceId = await db.Sheets.Where(s => s.Id == sheet).Select(s => s.WorkspaceId).SingleAsync();
            db.Set<Workbench.Domain.Identity.WorkspaceMember>().Add(new() { WorkspaceId = workspaceId, UserEmail = Pm, Role = Workbench.Domain.Identity.WorkspaceRole.Editor });
            await db.SaveChangesAsync();
            return 0;
        });

        // Pat marks it ready (Pat submits it; the admin approves).
        kit.As(Pm);
        await items.UpdateAsync(sheet, row.Id, new UpdateItemRequest("Spec v2", null, null, null, new Dictionary<string, string?> { [status.Id.ToString()] = "Ready for review" }));
        var routed = Assert.Single(await kit.RunsAsync(route.Id));
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), routed.Status);

        kit.As(Admin);
        var inbox = kit.Host.Get<IApprovalInboxService>();
        var waiting = Assert.Single(await inbox.ListMineAsync(), i => i.DocumentType == workflow.DocumentType);
        Assert.Equal(row.Id, waiting.DocumentId);
        await inbox.DecideAsync(workflow.DocumentType, row.Id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));

        Assert.Empty(await items.ListAsync(sheet));
        var archived = Assert.Single(await items.ListAsync(target));
        Assert.Equal("Spec v2", archived.Title);
        await kit.WithDbAsync(async db =>
        {
            Assert.Equal("Approved", (await db.Set<AutomationApprovalRequest>().SingleAsync()).Outcome);
            return 0;
        });
    }

    [Fact]
    public async Task Sheet_editors_manage_their_sheets_automations_viewers_see_them_and_only_admins_see_the_whole_list()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var definition = new AutomationDefinition(new(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.Created], SheetId: seeded.SheetId), null,
            [new(AutomationActionKind.Notify, [new(RecipientKind.Person, SeededHost.Owner)])]);
        var editor = seeded.As(SeededHost.Editor).Host.Get<IAutomationService>();
        var created = await editor.CreateAsync(new SaveAutomationRequest("Ping", definition));
        Assert.Single(await editor.ListForSheetAsync(seeded.SheetId));
        Assert.True((await editor.GetSheetCatalogAsync(seeded.SheetId)).CanEdit);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => editor.ListAsync()); // Admin › Automation is for administrators.
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => editor.CreateAsync(new SaveAutomationRequest("Not a sheet",
            new AutomationDefinition(new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Created]), null, [Notify(Person(SeededHost.Owner))]))));

        var viewer = seeded.As(SeededHost.Viewer).Host.Get<IAutomationService>();
        Assert.Equal(created.Id, Assert.Single(await viewer.ListForSheetAsync(seeded.SheetId)).Id);
        Assert.False((await viewer.GetSheetCatalogAsync(seeded.SheetId)).CanEdit);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.CreateAsync(new SaveAutomationRequest("Nope", definition)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer.SetEnabledAsync(created.Id, false));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(SeededHost.Outsider).Host.Get<IAutomationService>().GetAsync(created.Id));

        // The rules API shows the same automation.
        Assert.Equal(created.Id, Assert.Single(await seeded.As(SeededHost.Viewer).Host.Get<IRuleService>().ListAsync(seeded.SheetId)).Id);

        var admin = seeded.As(SeededHost.Admin).Host.Get<IAutomationService>();
        var listed = Assert.Single(await admin.ListAsync(), a => a.Id == created.Id);
        Assert.Equal(seeded.SheetId, listed.SheetId);
        Assert.Equal("When an item is added", listed.TriggerSummary);
        Assert.False((await admin.SetEnabledAsync(created.Id, false)).IsEnabled);
    }
}
