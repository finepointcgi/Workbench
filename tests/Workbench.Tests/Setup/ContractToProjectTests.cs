using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Identity;
using Workbench.Ui.Projects;
using Workbench.Ui.Setup;
using Workbench.Ui.Setup.Clins;

namespace Workbench.Tests.Setup;

/// <summary>
/// Friction found walking a contract from award to project (docs/ux/e2e-walkthrough.md): the type pickers showed code
/// names ("FirmFixedPrice"), a T&amp;M contract's new CLIN defaulted to CPFF with no number, and New › Project from a
/// contract's page started blank with no name.
/// </summary>
public sealed class ContractToProjectTests
{
    [Fact]
    public void Contract_types_and_clin_pricing_are_spelled_out_not_code_names()
    {
        foreach (var kind in Enum.GetValues<ContractKind>())
        {
            Assert.DoesNotContain(kind.ToString(), SetupFormat.Name(kind), StringComparison.Ordinal);
            Assert.Contains(' ', SetupFormat.Name(kind));
        }

        foreach (var pricing in Enum.GetValues<ClinPricingKind>())
        {
            Assert.DoesNotContain(pricing.ToString(), ClinFormat.Name(pricing), StringComparison.Ordinal);
        }

        Assert.Equal("Time and materials", SetupFormat.Name(ContractKind.TimeAndMaterials));
        Assert.Equal("Cost, no fee", ClinFormat.Name(ClinPricingKind.CostNoFee));
    }

    [Fact]
    public void A_new_clin_follows_the_contracts_type_and_takes_the_next_number()
    {
        Assert.Equal(ClinPricingKind.TimeAndMaterials, ClinFormat.PricingFor(ContractKind.TimeAndMaterials));
        Assert.Equal(ClinPricingKind.FirmFixedPrice, ClinFormat.PricingFor(ContractKind.FirmFixedPrice));
        Assert.Equal(ClinPricingKind.CostPlusFixedFee, ClinFormat.PricingFor(ContractKind.CostPlusFixedFee));
        foreach (var kind in Enum.GetValues<ContractKind>())
        {
            Assert.Equal(kind.ToString(), ClinFormat.PricingFor(kind).ToString());
        }

        Assert.Equal("0001", ClinFormat.NextNumber([]));
        Assert.Equal("0003", ClinFormat.NextNumber(["0001", "0002"]));
        Assert.Equal("1003", ClinFormat.NextNumber(["0001", "1002", "X001", "0001AA"]));
        Assert.Equal("", ClinFormat.NextNumber(["9999"]));
    }

    [Fact]
    public async Task New_project_from_a_contract_starts_from_it_with_its_name()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var contract = new ContractDto(Guid.NewGuid(), Guid.NewGuid(), "U.S. Army PEO EIS", "W91QUZ-27-C-0042", "Logistics data modernization",
            ContractKind.TimeAndMaterials, ContractState.Active, true, new DateOnly(2026, 10, 24), new DateOnly(2027, 10, 31), 1_000_000m, 400_000m);
        var other = contract with { Id = Guid.NewGuid(), ContractNumber = "N00019-26-C-0117", Name = "Avionics test set" };
        ctx.Services.AddSingleton(Fake<IProjectStartService>.Create(("SuggestCodeAsync", _ => "4202")));
        ctx.Services.AddSingleton(Fake<IProjectService>.Create());
        ctx.Services.AddSingleton(Fake<IContractService>.Create(("ListContractsAsync", _ => (IReadOnlyList<ContractDto>)[other, contract])));

        var form = ctx.Render<NewProjectForm>(p => p.Add(f => f.ContractId, contract.Id));

        form.WaitForAssertion(() =>
        {
            Assert.Equal("Logistics data modernization", form.Find("input[data-testid=start-name], [data-testid=start-name] input").GetAttribute("value"));
            Assert.Contains("W91QUZ-27-C-0042 · Logistics data modernization", form.Markup, StringComparison.Ordinal); // the picked contract
        });

        // Without a contract to start from, the form is the blank start as before.
        var blank = ctx.Render<NewProjectForm>();
        blank.WaitForAssertion(() => Assert.Contains("4202", blank.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Logistics data modernization", blank.Markup, StringComparison.Ordinal);
        Assert.Empty(blank.FindAll("[data-testid=start-contract-pick]"));
    }
}
