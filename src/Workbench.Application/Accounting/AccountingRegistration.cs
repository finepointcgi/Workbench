using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Time;

namespace Workbench.Application.Accounting;

public static class AccountingRegistration
{
    /// <summary>
    /// The general ledger, and automatic posting from timesheet and ODC approvals.
    /// <para>
    /// Ordering: call after AddWorkbenchTimekeeping and AddWorkbenchOdcs (the posting decorators wrap their approval
    /// services) and before AddWorkbenchIdentity, whose permission decorators must stay outermost so a refused approval
    /// never posts. The Finance-only checks for these services live in AddWorkbenchIdentity
    /// (<c>AddWorkbenchAccountingPermissions</c>).
    /// </para>
    /// </summary>
    public static IServiceCollection AddWorkbenchAccounting(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddScoped<GeneralLedger>();
        services.AddScoped<PostingEngine>();
        services.AddScoped<IChartOfAccountsService, ChartOfAccountsService>();
        services.AddScoped<IJournalService, JournalService>();
        services.AddScoped<IPostingService, PostingService>();
        services.AddScoped<IPeriodCloseService, PeriodCloseService>();
        services.AddScoped<IAccountingReportService, AccountingReportService>();

        services.Decorate<ITimesheetApprovalService, PostingTimesheetApprovalService>();
        services.Decorate<ITimesheetService, PostingTimesheetService>();
        services.Decorate<IOdcApprovalService, PostingOdcApprovalService>();
        return services;
    }
}
