using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Contracts;

namespace Workbench.Application.ContractDetail;

public static class ContractDetailRegistration
{
    /// <summary>CLINs, modifications and funding. Hosts may configure thresholds under <c>FundingNotices</c>.</summary>
    public static IServiceCollection AddWorkbenchContractDetail(this IServiceCollection services)
    {
        services.AddOptions<FundingNoticeOptions>().BindConfiguration(FundingNoticeOptions.Section);
        services.AddScoped<ClinFundingCalculator>();
        services.AddScoped<FundingNoticeChecker>();
        services.AddScoped<IClinService, ClinService>();
        services.AddScoped<IContractModificationService, ContractModificationService>();
        services.AddScoped<IFundingService, FundingService>();
        return services;
    }

    /// <summary>For the server host: the hourly Limitation of Funds check.</summary>
    public static IServiceCollection AddWorkbenchContractDetailHosting(this IServiceCollection services)
    {
        services.AddHostedService<FundingNoticeBackgroundService>();
        return services;
    }
}
