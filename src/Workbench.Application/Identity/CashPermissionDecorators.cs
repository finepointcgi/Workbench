using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

// Finance-or-Admin checks in front of every cash management service (bank accounts, statements, reconciliation, forecast).

public static class CashPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchCashPermissions(this IServiceCollection services)
    {
        services.Decorate<IBankAccountService, PermissionCheckingBankAccountService>();
        services.Decorate<IBankStatementService, PermissionCheckingBankStatementService>();
        services.Decorate<IBankReconciliationService, PermissionCheckingBankReconciliationService>();
        services.Decorate<ICashForecastService, PermissionCheckingCashForecastService>();
        return services;
    }
}

public sealed class PermissionCheckingBankAccountService(IBankAccountService inner, AccessControl access) : IBankAccountService
{
    private const string What = "use bank accounts";

    public async Task<IReadOnlyList<BankAccountDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ListAsync(includeInactive, cancellationToken);
    }

    public async Task<BankAccountDto> GetAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.GetAsync(bankAccountId, cancellationToken);
    }

    public async Task<BankAccountDto> CreateAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<BankAccountDto> UpdateAsync(Guid bankAccountId, SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.UpdateAsync(bankAccountId, request, cancellationToken);
    }

    public async Task<BankAccountNumberDto> RevealAccountNumberAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.RevealAccountNumberAsync(bankAccountId, cancellationToken);
    }

    public async Task<IReadOnlyList<CashGlAccountDto>> ListCashGlAccountsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ListCashGlAccountsAsync(cancellationToken);
    }
}

public sealed class PermissionCheckingBankStatementService(IBankStatementService inner, AccessControl access) : IBankStatementService
{
    private const string What = "import bank statements";

    public async Task<StatementImportResultDto> ImportAsync(ImportStatementRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ImportAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<StatementImportDto>> ListImportsAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ListImportsAsync(bankAccountId, cancellationToken);
    }

    public async Task<IReadOnlyList<BankItemDto>> ListItemsAsync(Guid bankAccountId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ListItemsAsync(bankAccountId, from, to, cancellationToken);
    }

    public async Task<BankAccountDto> SaveCsvMappingAsync(Guid bankAccountId, CsvColumnMapping mapping, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.SaveCsvMappingAsync(bankAccountId, mapping, cancellationToken);
    }
}

public sealed class PermissionCheckingBankReconciliationService(IBankReconciliationService inner, AccessControl access) : IBankReconciliationService
{
    private const string What = "reconcile bank accounts";

    public async Task<IReadOnlyList<BankReconciliationDto>> ListAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ListAsync(bankAccountId, cancellationToken);
    }

    public async Task<ReconciliationDefaultsDto> GetDefaultsAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.GetDefaultsAsync(bankAccountId, cancellationToken);
    }

    public async Task<BankReconciliationDto> StartAsync(StartReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.StartAsync(request, cancellationToken);
    }

    public async Task<BankReconciliationDto> UpdateAsync(Guid reconciliationId, UpdateReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.UpdateAsync(reconciliationId, request, cancellationToken);
    }

    public async Task<ReconcileWorkspaceDto> GetWorkspaceAsync(Guid reconciliationId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.GetWorkspaceAsync(reconciliationId, cancellationToken);
    }

    public async Task<AutoMatchResultDto> AutoMatchAsync(AutoMatchRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.AutoMatchAsync(request, cancellationToken);
    }

    public async Task<BankMatchDto> MatchAsync(MatchItemsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.MatchAsync(request, cancellationToken);
    }

    public async Task UnmatchAsync(Guid matchId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        await inner.UnmatchAsync(matchId, cancellationToken);
    }

    public async Task<BookingAccountsDto> GetBookingAccountsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.GetBookingAccountsAsync(cancellationToken);
    }

    public async Task<BankMatchDto> BookBankItemAsync(BookBankItemRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.BookBankItemAsync(request, cancellationToken);
    }

    public async Task<BankReconciliationDto> CompleteAsync(Guid reconciliationId, CompleteReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.CompleteAsync(reconciliationId, request, cancellationToken);
    }

    public async Task<BankReconciliationDto> ReopenAsync(Guid reconciliationId, ReconciliationReasonRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ReopenAsync(reconciliationId, request, cancellationToken);
    }

    public async Task<ReconciliationReportDto> GetReportAsync(Guid reconciliationId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.GetReportAsync(reconciliationId, cancellationToken);
    }

    public async Task<IReadOnlyList<BankCloseWaiverDto>> ListCloseWaiversAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.ListCloseWaiversAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<BankCloseWaiverDto> WaiveCloseCheckAsync(WaiveBankCloseCheckRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashManage, What, cancellationToken);
        return await inner.WaiveCloseCheckAsync(request, cancellationToken);
    }
}

public sealed class PermissionCheckingCashForecastService(ICashForecastService inner, AccessControl access) : ICashForecastService
{
    private const string What = "see the cash position and forecast";

    public async Task<CashPositionDto> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashForecastView, What, cancellationToken);
        return await inner.GetPositionAsync(cancellationToken);
    }

    public async Task<CashForecastDto> GetForecastAsync(int weeks = 13, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashForecastView, What, cancellationToken);
        return await inner.GetForecastAsync(weeks, cancellationToken);
    }

    public async Task<IReadOnlyList<ForecastItemDto>> ListItemsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashForecastView, What, cancellationToken);
        return await inner.ListItemsAsync(cancellationToken);
    }

    public async Task<ForecastItemDto> CreateItemAsync(SaveForecastItemRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashForecastView, What, cancellationToken);
        return await inner.CreateItemAsync(request, cancellationToken);
    }

    public async Task<ForecastItemDto> UpdateItemAsync(Guid itemId, SaveForecastItemRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashForecastView, What, cancellationToken);
        return await inner.UpdateItemAsync(itemId, request, cancellationToken);
    }

    public async Task<CashSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashForecastView, What, cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<CashSettingsDto> SaveSettingsAsync(CashSettingsDto settings, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.CashForecastView, What, cancellationToken);
        return await inner.SaveSettingsAsync(settings, cancellationToken);
    }
}
