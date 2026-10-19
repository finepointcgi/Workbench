using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Companies;
using Workbench.Domain.Companies;
using Workbench.Tests.Accessibility;
using Workbench.Ui.Companies;

namespace Workbench.Tests.Companies;

public sealed class CompanySwitcherTests
{
    private static readonly CompanyDto Main = new(Company.DefaultId, "MAIN", "Workbench Demo Corp", null, 10, "USD", true, true);
    private static readonly CompanyDto Fed = new(Guid.NewGuid(), "FED", "Workbench Federal Solutions LLC", null, 10, "USD", true, false);

    private static BunitContext NewContext(CompanyContextDto context)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ICompanyService>(new FakeCompanies(context));
        return ctx;
    }

    [Fact]
    public async Task One_company_shows_no_switcher()
    {
        await using var ctx = NewContext(new CompanyContextDto([Main], null, false));
        var cut = ctx.Render<CompanySwitcher>();
        Assert.Empty(cut.FindAll("[data-testid=company-switcher]"));
    }

    [Fact]
    public async Task Two_companies_show_the_selected_one_with_a_name()
    {
        await using var ctx = NewContext(new CompanyContextDto([Fed, Main], Fed.Id, true));
        var cut = ctx.Render<CompanySwitcher>();
        Assert.Equal("FED", cut.Find("[data-testid=company-current]").TextContent);
        Assert.Contains("Company:", cut.Find("[data-testid=company-switcher]").TextContent);
        A11yAssert.Check(cut);
    }

    private sealed class FakeCompanies(CompanyContextDto context) : ICompanyService
    {
        public Task<CompanyContextDto> GetContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(context);

        public Task<CompanyContextDto> SelectAsync(SelectCompanyRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(context with { SelectedCompanyId = request.CompanyId });

        public Task<IReadOnlyList<CompanyDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default) => Task.FromResult(context.Companies);

        public Task<CompanyDto> CreateAsync(SaveCompanyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CompanyDto> UpdateAsync(Guid companyId, SaveCompanyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<UserCompanyAccessDto>> ListUserAccessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UserCompanyAccessDto> SetUserAccessAsync(Guid userId, SetUserCompanyAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompanyRecordDto>> ListRecordsAsync(CompanyRecordKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CompanyRecordDto> AssignAsync(AssignCompanyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompanyPeriodDto>> ListPeriodsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CompanyPeriodDto> SetPeriodClosedAsync(Guid companyId, Guid fiscalPeriodId, SetCompanyPeriodRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
