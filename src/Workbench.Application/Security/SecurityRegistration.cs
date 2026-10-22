using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Security;
using Workbench.Contracts.Time;

namespace Workbench.Application.Security;

public static class SecurityRegistration
{
    /// <summary>
    /// User lifecycle, invites, access reviews, the security event log and records retention. Register after the
    /// features whose calls it logs (time reports, financial exports, fiscal calendar) and before
    /// <c>AddWorkbenchIdentity</c>, whose permission decorators must stay outermost.
    /// </summary>
    public static IServiceCollection AddWorkbenchSecurity(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();
        services.TryAddSingleton(new SignInPolicyOptions());
        services.TryAddSingleton(new RetentionOptions());
        services.TryAddScoped<ISecurityEventContext, NoSecurityEventContext>();
        services.AddScoped<SecurityEventLog>();
        services.AddSingleton<UserStatusCache>();
        services.AddScoped<UserLifecycle>();
        services.AddScoped<SignInGate>();
        services.TryAddSingleton(new Identity.Passwords.PasswordOptions());
        services.TryAddSingleton<Identity.Passwords.AccountLinks>();
        services.AddScoped<Identity.Passwords.PasswordAccounts>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IInviteService, InviteService>();
        services.AddScoped<IAccessReviewService, AccessReviewService>();
        services.AddScoped<ISecurityEventService, SecurityEventService>();
        services.AddScoped<RetentionService>();
        services.AddScoped<IRetentionService>(sp => sp.GetRequiredService<RetentionService>());

        services.Decorate<ITimeReportService, SecurityLoggingTimeReportService>();
        services.Decorate<IFinancialExportService, SecurityLoggingFinancialExportService>();
        services.Decorate<IFiscalCalendarService, SecurityLoggingFiscalCalendarService>();
        return services;
    }
}
