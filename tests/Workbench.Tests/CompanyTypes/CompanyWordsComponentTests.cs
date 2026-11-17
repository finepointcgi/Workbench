using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Company;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Ui.Projects;

namespace Workbench.Tests.CompanyTypes;

/// <summary>A key page in both modes: the project's setup step speaks of the contract and CLINs, or of the agreement and billing lines.</summary>
public sealed class CompanyWordsComponentTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static ProjectHubDto Hub() => new(
        new ProjectHubRowDto(ProjectId, "5101", "Harbor", null, null, Guid.NewGuid(), "C-1", ProjectState.Planning, null, null, null, false, false, null, null),
        null, null, new ProjectHubTabsDto(true, true, true, true, true, true, true, true, true), null, [], new DateOnly(2026, 10, 26));

    private static IRenderedComponent<ProjectSetupSteps> Render(BunitContext ctx, ITerms? terms)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx.Render<ProjectSetupSteps>(p =>
        {
            p.Add(s => s.Hub, Hub());
            if (terms is not null)
            {
                p.AddCascadingValue(terms);
            }
        });
    }

    [Fact]
    public void Without_a_company_type_the_page_reads_as_it_always_did()
    {
        using var ctx = new BunitContext();
        var page = Render(ctx, null);

        Assert.Contains("Set up the plan from the contract", page.Find("[data-testid='finish-setup']").TextContent);
        Assert.Contains("CLINs", page.Markup);
    }

    [Fact]
    public void A_government_contractor_keeps_the_federal_words()
    {
        using var ctx = new BunitContext();
        var page = Render(ctx, CompanyTerms.For(CompanyType.GovernmentContractor));

        Assert.Equal("Set up the plan from the contract", page.Find("[data-testid='finish-setup']").TextContent.Trim());
    }

    [Fact]
    public void A_professional_services_firm_reads_agreement_and_billing_lines()
    {
        using var ctx = new BunitContext();
        var page = Render(ctx, CompanyTerms.For(CompanyType.ProfessionalServices));

        Assert.Equal("Set up the plan from the agreement", page.Find("[data-testid='finish-setup']").TextContent.Trim());
        Assert.Contains("billing lines", page.Markup);
        Assert.DoesNotContain("CLIN", page.Markup);
    }
}
