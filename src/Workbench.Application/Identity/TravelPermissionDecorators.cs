using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Travel;
using Workbench.Domain.Travel;

namespace Workbench.Application.Identity;

/// <summary>
/// Travel and corporate cards. A trip request is seen by its traveler, by anyone it was routed to for approval, and by
/// Finance and administrators (others get "not found"); only the traveler changes it. The trip comparison follows the
/// expense report's own access (the employee and its approvers) plus Finance. Card programs, cards, imports and
/// reconciliation are Finance/Admin; the cardholder service only ever returns the signed-in user's own charges.
/// </summary>
public static class TravelPermissionRegistration
{
    public static IServiceCollection AddWorkbenchTravelPermissions(this IServiceCollection services)
    {
        services.Decorate<ITravelAuthorizationService, PermissionCheckingTravelAuthorizationService>();
        services.Decorate<ICardProgramService, PermissionCheckingCardProgramService>();
        services.Decorate<ICardChargeService, PermissionCheckingCardChargeService>();
        return services;
    }
}

public sealed class PermissionCheckingTravelAuthorizationService(
    ITravelAuthorizationService inner,
    AccessControl access,
    IWorkbenchDbContextFactory dbFactory,
    ApprovalEngine approvals,
    IExpenseReportService reports) : ITravelAuthorizationService
{
    public Task<IReadOnlyList<TravelAuthorizationDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListMineAsync(cancellationToken);
    }

    public Task<IReadOnlyList<TravelAuthorizationDto>> ListAwaitingMeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListAwaitingMeAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TravelAuthorizationDto>> ListAllAsync(TravelAuthorizationQuery? query = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.TravelManage, "see everyone's trips", cancellationToken);
        return await inner.ListAllAsync(query, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> GetAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await RequireViewAsync(authorizationId, cancellationToken);
        return await inner.GetAsync(authorizationId, cancellationToken);
    }

    public Task<TravelEstimateDto> EstimateAsync(EstimateTravelRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.EstimateAsync(request, cancellationToken);
    }

    public Task<TravelAuthorizationDetailDto> CreateAsync(SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.CreateAsync(request, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> UpdateAsync(Guid authorizationId, SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        await RequireTravelerAsync(authorizationId, cancellationToken);
        return await inner.UpdateAsync(authorizationId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await RequireTravelerAsync(authorizationId, cancellationToken);
        await inner.DeleteAsync(authorizationId, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> SubmitAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await RequireTravelerAsync(authorizationId, cancellationToken);
        return await inner.SubmitAsync(authorizationId, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> ApproveAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireViewAsync(authorizationId, cancellationToken);
        return await inner.ApproveAsync(authorizationId, request, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> RejectAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireViewAsync(authorizationId, cancellationToken);
        return await inner.RejectAsync(authorizationId, request, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> CancelAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default)
    {
        await RequireTravelerAsync(authorizationId, cancellationToken);
        return await inner.CancelAsync(authorizationId, request, cancellationToken);
    }

    public async Task<ExpenseReportDto> StartExpenseReportAsync(Guid authorizationId, CancellationToken cancellationToken = default)
    {
        await RequireTravelerAsync(authorizationId, cancellationToken);
        return await inner.StartExpenseReportAsync(authorizationId, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> LinkExpenseReportAsync(Guid authorizationId, LinkExpenseReportRequest request, CancellationToken cancellationToken = default)
    {
        await RequireTravelerAsync(authorizationId, cancellationToken);
        return await inner.LinkExpenseReportAsync(authorizationId, request, cancellationToken);
    }

    public async Task<TravelComparisonDto?> GetComparisonForReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        await RequireReportAsync(expenseReportId, cancellationToken);
        return await inner.GetComparisonForReportAsync(expenseReportId, cancellationToken);
    }

    public async Task<TravelComparisonDto> SetLineCategoryAsync(Guid expenseReportId, Guid lineId, SetTravelLineCategoryRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn(); // The service allows only the report's employee.
        return await inner.SetLineCategoryAsync(expenseReportId, lineId, request, cancellationToken);
    }

    public async Task<TravelAuthorizationDetailDto> RecordAdvanceAsync(Guid authorizationId, RecordTravelAdvanceRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.TravelManage, "record travel advances", cancellationToken);
        return await inner.RecordAdvanceAsync(authorizationId, request, cancellationToken);
    }

    public Task<TravelPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetPolicyAsync(cancellationToken);
    }

    private async Task<TravelAuthorization> RequireViewAsync(Guid id, CancellationToken ct)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(ct);
        var ta = await db.Set<TravelAuthorization>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ta is not null
            && (await access.HasAsync(Permission.TravelManage, ct)
                || ta.EmployeeId == await access.GetEmployeeIdAsync(ct)
                || await approvals.IsParticipantAsync(db, ApprovalDocumentTypes.TravelAuthorization, id, access.Email, ct)
                || await approvals.CanDecideAsync(db, ApprovalDocumentTypes.TravelAuthorization, id, access.Email, null, ct)))
        {
            return ta;
        }

        throw WorkbenchNotFoundException.For("Trip request", id);
    }

    private async Task RequireTravelerAsync(Guid id, CancellationToken ct)
    {
        var ta = await RequireViewAsync(id, ct);
        if (ta.EmployeeId != await access.GetEmployeeIdAsync(ct))
        {
            throw await access.DenyAsync("Only the traveler can change their trip request.", ct);
        }
    }

    private async Task RequireReportAsync(Guid reportId, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (!await access.HasAsync(Permission.TravelManage, ct))
        {
            await reports.GetReportAsync(reportId, ct); // Throws unless the user is the employee or one of the report's approvers.
        }
    }
}

public sealed class PermissionCheckingCardProgramService(ICardProgramService inner, AccessControl access) : ICardProgramService
{
    private const string What = "manage corporate cards";

    public async Task<IReadOnlyList<CardProgramDto>> ListProgramsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, What, cancellationToken);
        return await inner.ListProgramsAsync(cancellationToken);
    }

    public async Task<CardProgramDto> CreateProgramAsync(SaveCardProgramRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, What, cancellationToken);
        return await inner.CreateProgramAsync(request, cancellationToken);
    }

    public async Task<CardProgramDto> UpdateProgramAsync(Guid programId, SaveCardProgramRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, What, cancellationToken);
        return await inner.UpdateProgramAsync(programId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<CorporateCardDto>> ListCardsAsync(Guid? programId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, What, cancellationToken);
        return await inner.ListCardsAsync(programId, cancellationToken);
    }

    public async Task<CorporateCardDto> CreateCardAsync(SaveCorporateCardRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, What, cancellationToken);
        return await inner.CreateCardAsync(request, cancellationToken);
    }

    public async Task<CorporateCardDto> UpdateCardAsync(Guid cardId, SaveCorporateCardRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, What, cancellationToken);
        return await inner.UpdateCardAsync(cardId, request, cancellationToken);
    }

    public async Task<CardImportResultDto> ImportStatementAsync(Guid programId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, "import card statements", cancellationToken);
        return await inner.ImportStatementAsync(programId, fileName, content, cancellationToken);
    }

    public async Task<IReadOnlyList<CardStatementImportDto>> ListImportsAsync(Guid? programId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, What, cancellationToken);
        return await inner.ListImportsAsync(programId, cancellationToken);
    }

    public async Task<IReadOnlyList<CardTransactionDto>> ListTransactionsAsync(CardTransactionQuery? query = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, "see everyone's card charges", cancellationToken);
        return await inner.ListTransactionsAsync(query, cancellationToken);
    }

    public async Task<CardAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, "see everyone's card charges", cancellationToken);
        return await inner.GetAgingAsync(asOf, cancellationToken);
    }

    public async Task<IReadOnlyList<CardReconciliationDto>> GetReconciliationAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, "reconcile corporate cards", cancellationToken);
        return await inner.GetReconciliationAsync(cancellationToken);
    }

    public async Task<CardTransactionDto> ExcludeTransactionAsync(Guid transactionId, ExcludeCardTransactionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, "reconcile corporate cards", cancellationToken);
        return await inner.ExcludeTransactionAsync(transactionId, request, cancellationToken);
    }

    public async Task<CardTransactionDto> RestoreTransactionAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, "reconcile corporate cards", cancellationToken);
        return await inner.RestoreTransactionAsync(transactionId, cancellationToken);
    }

    public async Task<CardTransactionDto> AssignCardAsync(Guid transactionId, AssignCardRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CardsManage, "reconcile corporate cards", cancellationToken);
        return await inner.AssignCardAsync(transactionId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingCardChargeService(ICardChargeService inner, AccessControl access) : ICardChargeService
{
    public Task<IReadOnlyList<CorporateCardDto>> ListMyCardsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListMyCardsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<CardTransactionDto>> ListMyChargesAsync(bool includeAssigned = false, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListMyChargesAsync(includeAssigned, cancellationToken);
    }

    public Task<CardChargeSummaryDto> GetMySummaryAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetMySummaryAsync(cancellationToken);
    }

    public Task<OdcEntryDto> AttachToReportAsync(Guid transactionId, AttachCardChargeRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.AttachToReportAsync(transactionId, request, cancellationToken);
    }

    public Task DetachAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.DetachAsync(transactionId, cancellationToken);
    }

    public Task<IReadOnlyList<CardLineDto>> ListReportCardLinesAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListReportCardLinesAsync(expenseReportId, cancellationToken);
    }
}
