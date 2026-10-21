using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Crm;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Identity;

public static class CrmPermissionRegistration
{
    /// <summary>Business development: see <see cref="CrmAccess"/> for the rules.</summary>
    public static IServiceCollection AddWorkbenchCrmPermissions(this IServiceCollection services)
    {
        services.Decorate<IOpportunityService, PermissionCheckingOpportunityService>();
        services.Decorate<ICrmAccountService, PermissionCheckingCrmAccountService>();
        services.Decorate<IOpportunityGateService, PermissionCheckingOpportunityGateService>();
        services.Decorate<IOpportunityLeadService, PermissionCheckingOpportunityLeadService>();
        services.Decorate<IPipelineService, PermissionCheckingPipelineService>();
        return services;
    }
}

/// <summary>
/// Opportunities: the pipeline (BD, Finance, Admin) reads all; capture/proposal managers read and edit theirs; administrators
/// edit all; BD creates; Finance/Admin hand off won work. Lists are filtered by the service.
/// </summary>
public sealed class PermissionCheckingOpportunityService(IOpportunityService inner, CrmAccess access) : IOpportunityService
{
    public async Task<OpportunityDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(id, cancellationToken);
        return await inner.GetAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<OpportunityDto>> ListAsync(OpportunityFilter? filter = null, CancellationToken cancellationToken = default)
    {
        if (filter?.MineOnly == true)
        {
            // Only the opportunities the user manages: nothing to refuse (the menu asks this of everyone).
            access.RequireSignedInOrThrow();
            return await inner.ListAsync(filter, cancellationToken);
        }

        await access.RequirePipelineAsync("see the pipeline", cancellationToken, orManager: true);
        return await inner.ListAsync(filter, cancellationToken);
    }

    public async Task<OpportunityDto> CreateAsync(SaveOpportunityRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("add opportunities", cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<OpportunityDto> UpdateAsync(Guid id, SaveOpportunityRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(id, cancellationToken);
        return await inner.UpdateAsync(id, request, cancellationToken);
    }

    public async Task<OpportunityDto> ChangeStageAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(id, cancellationToken);
        return await inner.ChangeStageAsync(id, request, cancellationToken);
    }

    public async Task<OpportunityDto> RecordOutcomeAsync(Guid id, RecordOutcomeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(id, cancellationToken);
        return await inner.RecordOutcomeAsync(id, request, cancellationToken);
    }

    public async Task<OpportunityDto> ReopenAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(id, cancellationToken);
        return await inner.ReopenAsync(id, request, cancellationToken);
    }

    public async Task<IReadOnlyList<StageChangeDto>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(id, cancellationToken);
        return await inner.GetHistoryAsync(id, cancellationToken);
    }

    public async Task<HandoffResultDto> HandoffAsync(Guid id, HandoffRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(id, cancellationToken);
        await access.RequireAsync(Permission.SetupContracts, "create contracts from won opportunities", cancellationToken);
        return await inner.HandoffAsync(id, request, cancellationToken);
    }

    public async Task<OpportunityDto> EnsureBpChargeCodeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(id, cancellationToken);
        return await inner.EnsureBpChargeCodeAsync(id, cancellationToken);
    }
}

/// <summary>Accounts, contacts and activities: the pipeline reads (and managers, for their opportunities); BD, Finance and Admin change.</summary>
public sealed class PermissionCheckingCrmAccountService(ICrmAccountService inner, CrmAccess access, IWorkbenchDbContextFactory dbFactory) : ICrmAccountService
{
    public async Task<IReadOnlyList<CrmAccountDto>> ListAccountsAsync(CrmAccountKind? kind = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see accounts", cancellationToken, orManager: true);
        return await inner.ListAccountsAsync(kind, search, cancellationToken);
    }

    public async Task<CrmAccountDto> GetAccountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see accounts", cancellationToken, orManager: true);
        return await inner.GetAccountAsync(id, cancellationToken);
    }

    public async Task<CrmAccountDto> CreateAccountAsync(SaveCrmAccountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireCrmEditorAsync("add accounts", cancellationToken);
        return await inner.CreateAccountAsync(request, cancellationToken);
    }

    public async Task<CrmAccountDto> UpdateAccountAsync(Guid id, SaveCrmAccountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireCrmEditorAsync("change accounts", cancellationToken);
        return await inner.UpdateAccountAsync(id, request, cancellationToken);
    }

    public async Task<CrmAccountDto> LinkCustomerAsync(Guid id, Guid? customerId, CancellationToken cancellationToken = default)
    {
        await access.RequireCrmEditorAsync("link accounts to customers", cancellationToken);
        return await inner.LinkCustomerAsync(id, customerId, cancellationToken);
    }

    public async Task<IReadOnlyList<CrmContactDto>> ListContactsAsync(Guid? accountId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see contacts", cancellationToken, orManager: true);
        return await inner.ListContactsAsync(accountId, search, cancellationToken);
    }

    public async Task<CrmContactDto> CreateContactAsync(SaveCrmContactRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireCrmEditorAsync("add contacts", cancellationToken);
        return await inner.CreateContactAsync(request, cancellationToken);
    }

    public async Task<CrmContactDto> UpdateContactAsync(Guid id, SaveCrmContactRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireCrmEditorAsync("change contacts", cancellationToken);
        return await inner.UpdateContactAsync(id, request, cancellationToken);
    }

    public async Task<IReadOnlyList<CrmActivityDto>> ListActivitiesAsync(CrmActivityFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.OpportunityId is { } opportunity && filter.AccountId is null && filter.ContactId is null)
        {
            await access.RequireReadAsync(opportunity, cancellationToken);
        }
        else
        {
            await access.RequirePipelineAsync("see activity", cancellationToken, orManager: filter.MineOnly);
        }

        return await inner.ListActivitiesAsync(filter, cancellationToken);
    }

    public async Task<CrmActivityDto> LogActivityAsync(SaveCrmActivityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OpportunityId is { } opportunity && !await access.SeesPipelineAsync(cancellationToken))
        {
            // A manager without a pipeline role logs activity on their own opportunity.
            await access.RequireEditAsync(opportunity, cancellationToken);
        }
        else
        {
            await access.RequireCrmEditorAsync("log activity", cancellationToken);
            if (request.OpportunityId is { } o)
            {
                await access.RequireReadAsync(o, cancellationToken);
            }
        }

        return await inner.LogActivityAsync(request, cancellationToken);
    }

    public async Task<CrmActivityDto> SetTaskDoneAsync(Guid activityId, bool done, CancellationToken cancellationToken = default)
    {
        access.RequireSignedInOrThrow();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var activity = await db.Set<D.CrmActivity>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == activityId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Activity", activityId);
            if (activity.AssignedTo != access.Email && activity.CreatedBy != access.Email)
            {
                await access.RequireCrmEditorAsync("complete other people's tasks", cancellationToken);
            }
        }

        return await inner.SetTaskDoneAsync(activityId, done, cancellationToken);
    }
}

/// <summary>
/// Gates: anyone who sees the pipeline (or manages an opportunity) reads them; BD and administrators configure them; an
/// opportunity's editors submit reviews; the approval workflow decides who approves (checked in the service).
/// </summary>
public sealed class PermissionCheckingOpportunityGateService(IOpportunityGateService inner, CrmAccess access, IWorkbenchDbContextFactory dbFactory) : IOpportunityGateService
{
    public async Task<IReadOnlyList<GateDefinitionDto>> ListGatesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see gates", cancellationToken, orManager: true);
        return await inner.ListGatesAsync(includeInactive, cancellationToken);
    }

    public async Task<GateDefinitionDto> CreateGateAsync(SaveGateDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("set up gates", cancellationToken);
        return await inner.CreateGateAsync(request, cancellationToken);
    }

    public async Task<GateDefinitionDto> UpdateGateAsync(Guid id, SaveGateDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("set up gates", cancellationToken);
        return await inner.UpdateGateAsync(id, request, cancellationToken);
    }

    public async Task<IReadOnlyList<GateReviewDto>> ListReviewsAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(opportunityId, cancellationToken);
        return await inner.ListReviewsAsync(opportunityId, cancellationToken);
    }

    public async Task<GateReviewDto> SubmitAsync(Guid opportunityId, SubmitGateReviewRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(opportunityId, cancellationToken);
        return await inner.SubmitAsync(opportunityId, request, cancellationToken);
    }

    public async Task<GateReviewDto> ApproveAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedInOrThrow();
        await ExistsAsync(reviewId, cancellationToken);
        return await inner.ApproveAsync(reviewId, request, cancellationToken);
    }

    public async Task<GateReviewDto> RejectAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedInOrThrow();
        await ExistsAsync(reviewId, cancellationToken);
        return await inner.RejectAsync(reviewId, request, cancellationToken);
    }

    public async Task<GateReviewDto> CancelAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedInOrThrow();
        await ExistsAsync(reviewId, cancellationToken);
        return await inner.CancelAsync(reviewId, cancellationToken);
    }

    public Task<IReadOnlyList<GateReviewDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedInOrThrow();
        return inner.ListWaitingOnMeAsync(cancellationToken);
    }

    private async Task ExistsAsync(Guid reviewId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        if (!await db.Set<D.OpportunityGateReview>().AnyAsync(r => r.Id == reviewId, ct))
        {
            throw WorkbenchNotFoundException.For("Gate review", reviewId);
        }
    }
}

/// <summary>Leads and saved searches: BD and administrators run, import and review; Finance reads.</summary>
public sealed class PermissionCheckingOpportunityLeadService(IOpportunityLeadService inner, CrmAccess access) : IOpportunityLeadService
{
    public async Task<IReadOnlyList<SavedLeadSearchDto>> ListSearchesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see saved searches", cancellationToken);
        return await inner.ListSearchesAsync(cancellationToken);
    }

    public async Task<SavedLeadSearchDto> CreateSearchAsync(SaveLeadSearchRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("save searches", cancellationToken);
        return await inner.CreateSearchAsync(request, cancellationToken);
    }

    public async Task<SavedLeadSearchDto> UpdateSearchAsync(Guid id, SaveLeadSearchRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("change saved searches", cancellationToken);
        return await inner.UpdateSearchAsync(id, request, cancellationToken);
    }

    public async Task DeleteSearchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("delete saved searches", cancellationToken);
        await inner.DeleteSearchAsync(id, cancellationToken);
    }

    public async Task<LeadImportResultDto> RunSearchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("run searches", cancellationToken);
        return await inner.RunSearchAsync(id, cancellationToken);
    }

    public async Task<LeadImportResultDto> ImportCsvAsync(string csv, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("import leads", cancellationToken);
        return await inner.ImportCsvAsync(csv, cancellationToken);
    }

    public async Task<IReadOnlyList<OpportunityLeadDto>> ListLeadsAsync(LeadStatus? status = LeadStatus.New, CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see leads", cancellationToken);
        return await inner.ListLeadsAsync(status, cancellationToken);
    }

    public async Task<OpportunityDto> AcceptAsync(Guid leadId, AcceptLeadRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("accept leads", cancellationToken);
        return await inner.AcceptAsync(leadId, request, cancellationToken);
    }

    public async Task<OpportunityLeadDto> DismissAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        await access.RequireBusinessDevelopmentAsync("dismiss leads", cancellationToken);
        return await inner.DismissAsync(leadId, cancellationToken);
    }
}

/// <summary>Pipeline views: the pipeline (and managers, for their own work); the forecast and win/loss need the pipeline; B&amp;P setting Finance/Admin.</summary>
public sealed class PermissionCheckingPipelineService(IPipelineService inner, CrmAccess access) : IPipelineService
{
    public async Task<PipelineBoardDto> GetBoardAsync(bool includeOutcomes = false, CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see the pipeline", cancellationToken, orManager: true);
        return await inner.GetBoardAsync(includeOutcomes, cancellationToken);
    }

    public async Task<ForecastDto> GetForecastAsync(CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see the pipeline forecast", cancellationToken);
        return await inner.GetForecastAsync(cancellationToken);
    }

    public async Task<WinLossDto> GetWinLossAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see win/loss analysis", cancellationToken);
        return await inner.GetWinLossAsync(from, to, cancellationToken);
    }

    public async Task<IReadOnlyList<BpSpendDto>> GetBpSpendAsync(Guid? opportunityId = null, CancellationToken cancellationToken = default)
    {
        if (opportunityId is { } id)
        {
            await access.RequireReadAsync(id, cancellationToken);
        }
        else
        {
            await access.RequirePipelineAsync("see B&P spending", cancellationToken);
        }

        return await inner.GetBpSpendAsync(opportunityId, cancellationToken);
    }

    public async Task<CrmSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequirePipelineAsync("see business development settings", cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<CrmSettingsDto> SetBpParentAsync(Guid? wbsElementId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BpChargeCode, "choose the B&P charge code", cancellationToken);
        return await inner.SetBpParentAsync(wbsElementId, cancellationToken);
    }

    public async Task<IReadOnlyList<BpParentOptionDto>> ListBpParentOptionsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BpChargeCode, "choose the B&P charge code", cancellationToken);
        return await inner.ListBpParentOptionsAsync(cancellationToken);
    }
}
