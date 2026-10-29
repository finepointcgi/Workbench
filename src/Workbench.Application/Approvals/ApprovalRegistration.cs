using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Approvals;
using Microsoft.AspNetCore.DataProtection;

namespace Workbench.Application.Approvals;

public static class ApprovalRegistration
{
    /// <summary>
    /// The approval workflow engine (injected into the timesheet, ODC, purchasing, billing and journal services), the
    /// setup, inbox and delegation services, and one inbox handler per document type. Above AddWorkbenchIdentity.
    /// </summary>
    public static IServiceCollection AddWorkbenchApprovals(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection(); // Email link tokens (the web host configures its keys; this is a no-op there).
        services.AddSingleton<ApprovalEngine>();
        services.AddScoped<IApprovalWorkflowService, ApprovalWorkflowService>();
        services.AddScoped<IApprovalInboxService, ApprovalInboxService>();
        services.AddScoped<IApprovalDelegationService, ApprovalDelegationService>();
        services.AddSingleton<ApprovalEmailTokens>();
        services.AddSingleton<Notifications.INotificationChatLinks, ApprovalChatLinks>(); // Teams messages about approvals link to the document.
        services.AddScoped<IApprovalEmailLinkService, ApprovalEmailLinkService>();
        services.AddSingleton<Notifications.INotificationEmailExtras, ApprovalEmailActions>();
        services.AddScoped<ApprovalReminderCheck>();
        services.AddSingleton<ApprovalBackfillGate>();
        services.AddScoped<ApprovalBackfill>();
        services.AddScoped<IApprovalDocumentHandler, TimesheetApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, ExpenseReportApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, VendorOdcApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, RequisitionApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, ApBillApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, InvoiceApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, JournalEntryApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, LeaveRequestApprovalHandler>();
        return services;
    }

    /// <summary>For the server host: routing documents pending from before workflows (once per tenant at start) and hourly escalation reminders.</summary>
    public static IServiceCollection AddWorkbenchApprovalsHosting(this IServiceCollection services)
    {
        services.AddHostedService<ApprovalBackfillBackgroundService>();
        services.AddHostedService<ApprovalReminderBackgroundService>();
        return services;
    }
}
