using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Customization;
using Workbench.Ui.AutomationBuilder;

namespace Workbench.Tests.AutomationBuilder;

public sealed class ConditionGroupEditorTests
{
    private static readonly AutomationCatalogDto Catalog = new([], [], ApprovalOrgRoleName.All, [], [], [], [], [], [], []);

    private static readonly IReadOnlyList<EntityFieldDto> Fields =
    [
        new("contract", "fundedValue", "Funded value", "money", false, true, null, AutomationPermissionKeys.FinanceFields),
        new("contract", "status", "Status", "choice", false, true, ["Pending", "Active", "Closed"], null),
    ];

    [Fact]
    public async Task Conditions_and_nested_groups_are_added_and_become_the_definition()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Render<MudBlazor.MudPopoverProvider>();
        var group = new GroupModel();
        var editor = ctx.Render<ConditionGroupEditor>(p => p.Add(e => e.Group, group).Add(e => e.Fields, Fields).Add(e => e.Catalog, Catalog));

        editor.Find("button[data-testid=add-condition]").Click();
        Assert.Single(group.Conditions);
        Assert.Equal("fundedValue", group.Conditions[0].FieldKey);
        editor.FindAll("button").First(b => b.TextContent.Contains("Group")).Click();
        Assert.Single(group.Groups);
        Assert.Equal(GroupLogic.Any, group.Groups[0].Logic);

        group.Conditions[0].Operator = ConditionOperator.LessThan;
        group.Conditions[0].Value = "1000";
        group.Groups[0].Conditions.Add(new ConditionModel { Subject = ConditionSubject.ActorRole, Operator = ConditionOperator.In, Values = ["Finance"] });
        var definition = group.ToGroup()!;
        Assert.Equal(GroupLogic.All, definition.Logic);
        Assert.Equal(new AutomationCondition(ConditionSubject.Field, ConditionOperator.LessThan, "fundedValue", "1000"), definition.Conditions![0]);
        Assert.Equal(["Finance"], definition.Groups![0].Conditions![0].Values);
        Assert.Equal(2, editor.FindAll("[data-testid=condition-group]").Count);
    }
}
