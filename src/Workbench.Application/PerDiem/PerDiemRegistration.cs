using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;

namespace Workbench.Application.PerDiem;

public static class PerDiemRegistration
{
    /// <summary>
    /// Per diem rate tables and per diem on travel expense reports. Binds <see cref="PerDiemOptions"/> from config
    /// <c>PerDiem</c>. Register after <c>AddWorkbenchOdcs</c> (it decorates <see cref="IExpenseReportService"/>) and above
    /// <c>AddWorkbenchIdentity</c>.
    /// </summary>
    public static IServiceCollection AddWorkbenchPerDiem(this IServiceCollection services)
    {
        services.AddOptions<PerDiemOptions>().BindConfiguration(PerDiemOptions.Section);
        services.AddHttpClient<GsaPerDiemHttpClient>(client => client.Timeout = TimeSpan.FromSeconds(60));
        services.TryAddScoped<IGsaPerDiemClient>(sp => sp.GetRequiredService<GsaPerDiemHttpClient>());
        services.AddScoped<TravelPerDiemComputer>();
        services.AddScoped<IPerDiemRateService, PerDiemRateService>();
        services.AddScoped<ITravelPerDiemService, TravelPerDiemService>();
        services.Decorate<IExpenseReportService, PerDiemSubmitGuard>();
        return services;
    }
}
