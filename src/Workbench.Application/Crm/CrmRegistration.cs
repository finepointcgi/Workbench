using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Crm;

namespace Workbench.Application.Crm;

public static class CrmRegistration
{
    /// <summary>
    /// Business development (CRM and capture): accounts, opportunities, gates (the "Opportunity gate" approval handler),
    /// SAM.gov leads (the real API with <c>SamGov:ApiKey</c>, else sample data), pipeline views and home to-dos. Above
    /// AddWorkbenchIdentity, which adds the permission decorators.
    /// </summary>
    public static IServiceCollection AddWorkbenchCrm(this IServiceCollection services)
    {
        services.AddHttpClient(SamGovClients.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<ISamGovClient>(SamGovClients.Create);
        services.AddScoped<CrmAccess>();
        services.AddScoped<IOpportunityService, OpportunityService>();
        services.AddScoped<ICrmAccountService, CrmAccountService>();
        services.AddScoped<IOpportunityGateService, OpportunityGateService>();
        services.AddScoped<LeadFitScorer>();
        services.AddScoped<OpportunityLeadService>();
        services.AddScoped<IOpportunityLeadService>(sp => sp.GetRequiredService<OpportunityLeadService>());
        services.AddScoped<IPipelineService, PipelineService>();
        services.AddScoped<ICapturePlanService, CapturePlanService>();
        services.AddScoped<IProposalService, ProposalService>();
        services.AddScoped<IRequirementService, RequirementService>();
        services.AddScoped<IApprovalDocumentHandler, OpportunityGateApprovalHandler>();
        services.AddScoped<IMyTodoProvider, CrmTodoProvider>();
        services.AddScoped<Contracts.Pricing.IPricingOpportunityLookup, CrmOpportunityLookup>(); // The proposal dialog's picker (wins over pricing's TryAdd default).
        return services;
    }

    /// <summary>For the server host: runs active saved searches against SAM.gov once a day (only when an API key is configured).</summary>
    public static IServiceCollection AddWorkbenchCrmHosting(this IServiceCollection services)
    {
        services.AddHostedService<SavedLeadSearchBackgroundService>();
        return services;
    }
}

/// <summary>
/// Daily: every active saved search, per tenant, as Workbench System (through the service, not its permission decorator).
/// Skipped with the sample client: there's nothing new to find.
/// </summary>
public sealed class SavedLeadSearchBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<SavedLeadSearchBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = SystemExecution.CreateScope(scopes);
                    if (scope.ServiceProvider.GetRequiredService<ISamGovClient>().IsSample)
                    {
                        return;
                    }

                    var service = scope.ServiceProvider.GetRequiredService<OpportunityLeadService>();
                    var created = 0;
                    foreach (var search in (await service.ListSearchesAsync(ct)).Where(s => s.IsActive))
                    {
                        created += (await service.RunSearchAsync(search.Id, ct)).Created;
                    }

                    if (created > 0)
                    {
                        logger.LogInformation("Saved SAM.gov searches found {Count} new leads", created);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Saved SAM.gov searches failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
