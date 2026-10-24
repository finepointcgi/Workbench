using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Identity;
using Workbench.Ui.Setup.Clins;

namespace Workbench.Tests.ContractDetail;

/// <summary>The CLIN list says when a CLIN has no money on it, and its Fund button funds it in one step.</summary>
public sealed class FundClinComponentTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly Guid ContractId = Guid.NewGuid();

    private static ClinDto Clin(string number, decimal ceiling, decimal funded) =>
        new(Guid.NewGuid(), ContractId, number, null, $"CLIN {number}", ClinPricingKind.FirmFixedPrice, false, false, Start, Start.AddYears(1),
            ceiling, funded, null, 0, false);

    private static ClinFundingStatusDto Status(ClinDto clin, FundingLevel level) =>
        new(clin.Id, clin.Number, clin.Description, false, false, clin.PeriodStart, clin.PeriodEnd, clin.CeilingValue, clin.FundedValue,
            0m, 0m, clin.FundedValue, null, null, 0m, null, level);

    private static BunitContext NewContext(IContractModificationService? mods = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Fake<IClinService>.Create());
        ctx.Services.AddSingleton(mods ?? Fake<IContractModificationService>.Create());
        return ctx;
    }

    [Fact]
    public async Task A_clin_with_no_money_says_not_funded_and_offers_to_fund_it()
    {
        await using var ctx = NewContext();
        var unfunded = Clin("0001", 850_000m, 0m);
        var full = Clin("0002", 100_000m, 100_000m);
        var contract = new ContractDto(ContractId, Guid.NewGuid(), "Army", "1599", "DLB Abrams", ContractKind.FirmFixedPrice, ContractState.Active, true,
            Start, Start.AddYears(1), 950_000m, 100_000m);
        var status = new ContractFundingStatusDto(ContractId, "1599", Start, false, 0.75m, 0.9m,
            [Status(unfunded, FundingLevel.Unfunded), Status(full, FundingLevel.Ok)], []);

        var tab = ctx.Render<ClinsTab>(p => p.Add(t => t.Contract, contract).Add(t => t.Clins, [unfunded, full]).Add(t => t.Status, status));

        var rows = tab.FindAll("[data-testid=clin-row]");
        Assert.Contains("Not funded", rows[0].TextContent);
        Assert.DoesNotContain("Not funded", rows[1].TextContent);
        Assert.Single(rows[0].QuerySelectorAll("[data-testid=clin-fund]"));
        Assert.Empty(rows[1].QuerySelectorAll("[data-testid=clin-fund]")); // nothing left to fund under its value
    }

    [Fact]
    public async Task The_fund_dialog_writes_and_executes_one_funding_modification()
    {
        SaveModificationRequest? saved = null;
        var executed = false;
        var draftId = Guid.NewGuid();
        ContractModificationDto Mod(ModificationState state) => new(draftId, ContractId, saved!.Number, saved.EffectiveDate, saved.Description, state,
            null, null, saved.Changes, saved.Changes.Sum(c => c.Amount ?? 0));
        var mods = Fake<IContractModificationService>.Create(
            ("ListAsync", _ => (IReadOnlyList<ContractModificationDto>)[new(Guid.NewGuid(), ContractId, "P00001", Start, "Award", ModificationState.Executed, null, null, [], 0m)]),
            ("CreateDraftAsync", a => { saved = (SaveModificationRequest)a![1]!; return Mod(ModificationState.Draft); }),
            ("ExecuteAsync", _ => { executed = true; return Mod(ModificationState.Executed); }));
        await using var ctx = NewContext(mods);
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var clin = Clin("0001", 850_000m, 50_000m);

        var reference = await provider.InvokeAsync(() => dialogs.ShowAsync<FundClinDialog>("Fund CLIN 0001",
            new DialogParameters<FundClinDialog> { { d => d.ContractId, ContractId }, { d => d.Clin, clin } }));

        // The next modification number and what's left under the CLIN's value are filled in.
        provider.WaitForAssertion(() => Assert.Equal("P00002", provider.Find("[data-testid=fund-mod-number] input, input[data-testid=fund-mod-number]").GetAttribute("value")));
        provider.Find("[data-testid=fund-amount] input, input[data-testid=fund-amount]").Input("200000");
        await provider.Find("[data-testid=fund-save]").ClickAsync(new());

        var result = await reference.Result;
        Assert.False(result!.Canceled);
        Assert.True(executed);
        var change = Assert.Single(saved!.Changes);
        Assert.Equal((ModificationChangeKind.Fund, "0001", 200_000m), (change.Kind, change.ClinNumber, change.Amount));
        Assert.Equal(("P00002", "Fund CLIN 0001"), (saved.Number, saved.Description));
    }
}
