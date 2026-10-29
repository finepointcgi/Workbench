using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Accounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.IndirectRates;

public static class IndirectRatesRegistration
{
    /// <summary>
    /// Indirect rates, allocations and burdened cost (<see cref="IBurdenedCostService"/> for billing and financials).
    /// Call after AddWorkbenchAccounting (it decorates the accounting reports and adds a period-close check) and above
    /// AddWorkbenchIdentity, which adds the permission decorators.
    /// </summary>
    public static IServiceCollection AddWorkbenchIndirectRates(this IServiceCollection services)
    {
        services.AddScoped<AllocationEngine>();
        services.AddScoped<IIndirectRateService, IndirectRateService>();
        services.AddScoped<IBurdenedCostService, BurdenedCostService>();
        services.AddScoped<IBurdenedFinancialsService, BurdenedFinancialsService>();
        services.AddScoped<IPeriodCloseCheck, AllocationCloseCheck>();
        services.Decorate<IAccountingReportService, BurdenedReconciliationReportService>();
        return services;
    }
}

/// <summary>Period close: the allocation for the period is posted and matches the current approved cost.</summary>
public sealed class AllocationCloseCheck(AllocationEngine engine) : IPeriodCloseCheck
{
    public async Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken cancellationToken)
    {
        const string title = "Allocation posted";
        var preview = await engine.PreviewAsync(db, period.Id, cancellationToken);
        if (preview.Posted is null && preview.Total == 0)
        {
            return new CloseCheckDto(CloseCheckKind.AllocationPosted, title, true, 0, "No indirect burden to allocate", preview.Notes);
        }

        if (preview.IsCurrent)
        {
            return new CloseCheckDto(CloseCheckKind.AllocationPosted, title, true, 0,
                $"Allocation {preview.Posted!.EntryNumber} posted ({preview.Posted.Total:N2})", []);
        }

        return new CloseCheckDto(CloseCheckKind.AllocationPosted, title, false, 1,
            preview.Posted is null
                ? $"Indirect cost allocation not posted ({preview.Total:N2} to apply)"
                : $"Allocation {preview.Posted.EntryNumber} ({preview.Posted.Total:N2}) is out of date: {preview.Total:N2} now; re-run it",
            ["Run it under Accounting › Indirect rates › Allocation runs."]);
    }
}

/// <summary>Adds the expected burden (provisional rates on the financials actuals) to the GL reconciliation report.</summary>
public sealed class BurdenedReconciliationReportService(IAccountingReportService inner, IWorkbenchDbContextFactory dbFactory) : IAccountingReportService
{
    public Task<MonthReviewDto> GetMonthReviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.GetMonthReviewAsync(fiscalPeriodId, cancellationToken);

    public Task<TrialBalanceDto> GetTrialBalanceAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.GetTrialBalanceAsync(fiscalPeriodId, cancellationToken);

    public Task<LedgerDetailDto> GetLedgerDetailAsync(LedgerQuery query, CancellationToken cancellationToken = default) => inner.GetLedgerDetailAsync(query, cancellationToken);

    public Task<FinancialStatementDto> GetIncomeStatementAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.GetIncomeStatementAsync(fiscalPeriodId, cancellationToken);

    public Task<FinancialStatementDto> GetBalanceSheetAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.GetBalanceSheetAsync(fiscalPeriodId, cancellationToken);

    public Task<IReadOnlyList<ProjectCostRowDto>> GetProjectCostLedgerAsync(Guid? projectId = null, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        inner.GetProjectCostLedgerAsync(projectId, fiscalPeriodId, cancellationToken);

    public async Task<ReconciliationDto> GetReconciliationAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        var report = await inner.GetReconciliationAsync(projectId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var book = await RateBook.LoadAsync(db, cancellationToken);
        if (!book.HasRates)
        {
            return report;
        }

        var costs = await DirectCosts.LoadAsync(db, projectId, DateOnly.MinValue, DateOnly.MaxValue, [], cancellationToken);
        var expected = (await BurdenedCostService.BurdenAsync(db, book, costs, IndirectRateBasis.Provisional, cancellationToken)).Lines
            .ToDictionary(l => l.WbsElementId, l => l.Fringe + l.Overhead + l.GeneralAndAdministrative);
        var rows = report.Rows.Select(r => r with { ExpectedBurden = expected.GetValueOrDefault(r.WbsElementId) }).ToList();
        var notes = report.Notes.ToList();
        if (rows.Any(r => r.BurdenDifference != 0))
        {
            notes.Add("Applied burden differs from provisional burden on some elements: allocation runs not posted (or out of date) for some periods, or a posted true-up.");
        }

        return new ReconciliationDto(rows, notes);
    }

    public Task<AccountingExportFile> ExportCsvAsync(AccountingExportRequest request, CancellationToken cancellationToken = default) => inner.ExportCsvAsync(request, cancellationToken);
}
