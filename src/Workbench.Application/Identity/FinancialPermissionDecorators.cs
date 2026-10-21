using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Time;

namespace Workbench.Application.Identity;

// Permission checks in front of timekeeping reports, project financials, budgets, financial exports and ODC listings.
// Labor cost (hours x cost rate) is Finance's; a project's financials and budgets belong to Finance and its manager.

/// <summary>
/// Hours and daily-entry reports: Timekeeper, Finance and Admin see everyone, a supervisor sees their direct reports,
/// everyone else only themselves. The labor distribution carries hourly costs, so it's Finance and Admin only.
/// </summary>
public sealed class PermissionCheckingTimeReportService(ITimeReportService inner, ProjectAccess projects) : ITimeReportService
{
    public async Task<IReadOnlyList<HoursByChargeCodeRow>> GetHoursByChargeCodeAsync(DateOnly from, DateOnly to, bool approvedOnly = true, CancellationToken cancellationToken = default)
    {
        var visible = await projects.VisibleTimeEmployeeIdsAsync(cancellationToken);
        var rows = await inner.GetHoursByChargeCodeAsync(from, to, approvedOnly, cancellationToken);
        return visible is null ? rows : rows.Where(r => visible.Contains(r.EmployeeId)).ToList();
    }

    public async Task<IReadOnlyList<DailyEntryComplianceRow>> GetDailyEntryComplianceAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var visible = await projects.VisibleTimeEmployeeIdsAsync(cancellationToken);
        var rows = await inner.GetDailyEntryComplianceAsync(from, to, cancellationToken);
        return visible is null ? rows : rows.Where(r => visible.Contains(r.EmployeeId)).ToList();
    }

    public async Task<string> ExportLaborDistributionCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireAsync(Permission.LaborCostView, "export the labor distribution", cancellationToken);
        return await inner.ExportLaborDistributionCsvAsync(from, to, cancellationToken);
    }
}

public sealed class PermissionCheckingProjectFinancialsService(IProjectFinancialsService inner, ProjectAccess projects) : IProjectFinancialsService
{
    public async Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's financials", cancellationToken);
        return await inner.GetProjectSummaryAsync(projectId, budgetVersionId, forecastVersionId, cancellationToken);
    }

    public async Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's financials", cancellationToken);
        return await inner.GetWbsFinancialsAsync(projectId, budgetVersionId, forecastVersionId, cancellationToken);
    }

    public async Task<TimePhasedBurnDto> GetTimePhasedBurnAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's financials", cancellationToken);
        return await inner.GetTimePhasedBurnAsync(projectId, budgetVersionId, cancellationToken);
    }

    public async Task<ContractFundingDto> GetContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.RequireContractFundingAsync(contractId, cancellationToken);
        return await inner.GetContractFundingAsync(contractId, cancellationToken);
    }

    /// <summary>Finance and Admin get every contract; a project manager gets the contracts their projects are on.</summary>
    public async Task<IReadOnlyList<ContractFundingDto>> ListContractFundingAsync(CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        var managed = await projects.ManagedContractIdsAsync(cancellationToken);
        if (managed is { Count: 0 })
        {
            return [];
        }

        var all = await inner.ListContractFundingAsync(cancellationToken);
        return managed is null ? all : all.Where(c => managed.Contains(c.ContractId)).ToList();
    }
}

public sealed class PermissionCheckingFinancialExportService(IFinancialExportService inner, ProjectAccess projects) : IFinancialExportService
{
    public async Task<FinancialExportFile> ExportWbsCsvAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "export this project's financials", cancellationToken);
        return await inner.ExportWbsCsvAsync(projectId, budgetVersionId, forecastVersionId, cancellationToken);
    }

    public async Task<FinancialExportFile> ExportTimePhasedCsvAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "export this project's financials", cancellationToken);
        return await inner.ExportTimePhasedCsvAsync(projectId, budgetVersionId, cancellationToken);
    }
}

/// <summary>Reading and changing budgets (including locking a baseline and promoting a forecast): Finance, Admin or the project's manager.</summary>
public sealed class PermissionCheckingBudgetService(IBudgetService inner, ProjectAccess projects) : IBudgetService
{
    public async Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's budgets", cancellationToken);
        return await inner.ListVersionsAsync(projectId, cancellationToken);
    }

    public async Task<BudgetVersionDetailDto?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        if (await projects.ProjectOfBudgetVersionAsync(versionId, cancellationToken) is not { } projectId)
        {
            return null;
        }

        await projects.RequireProjectFinancialsAsync(projectId, "see this project's budgets", cancellationToken);
        return await inner.GetVersionAsync(versionId, cancellationToken);
    }

    public async Task<BudgetVersionDetailDto> CreateVersionAsync(Guid projectId, CreateBudgetVersionRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "create budget versions", cancellationToken);
        return await inner.CreateVersionAsync(projectId, request, cancellationToken);
    }

    public async Task<BudgetVersionDetailDto> SaveLinesAsync(Guid versionId, IReadOnlyList<SaveBudgetLineRequest> lines, CancellationToken cancellationToken = default)
    {
        await RequireVersionAsync(versionId, "change budgets", cancellationToken);
        return await inner.SaveLinesAsync(versionId, lines, cancellationToken);
    }

    public async Task<BudgetVersionDto> LockBaselineAsync(Guid versionId, string? reason = null, CancellationToken cancellationToken = default)
    {
        await RequireVersionAsync(versionId, "lock a baseline", cancellationToken);
        return await inner.LockBaselineAsync(versionId, reason, cancellationToken);
    }

    public async Task<BudgetVersionDto> PromoteToCurrentAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await RequireVersionAsync(versionId, "promote a forecast", cancellationToken);
        return await inner.PromoteToCurrentAsync(versionId, cancellationToken);
    }

    public async Task<BudgetVersionDetailDto> RepriceAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await RequireVersionAsync(versionId, "reprice budgets", cancellationToken);
        return await inner.RepriceAsync(versionId, cancellationToken);
    }

    private async Task RequireVersionAsync(Guid versionId, string what, CancellationToken cancellationToken)
    {
        projects.Access.RequireSignedIn();
        var projectId = await projects.ProjectOfBudgetVersionAsync(versionId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Budget version", versionId);
        await projects.RequireProjectFinancialsAsync(projectId, what, cancellationToken);
    }
}

/// <summary>
/// A project's ODC list is for Finance, Admin and the project's manager; anyone else gets only their own entries.
/// A single entry (and its receipt) is visible to those, its submitter or employee, and their supervisor (the approver).
/// Changing a vendor ODC is for its submitter, Finance, Admin and the project's manager. Entering one stays open.
/// </summary>
public sealed class PermissionCheckingOdcService(IOdcService inner, ProjectAccess projects) : IOdcService
{
    public async Task<OdcListDto> ListProjectOdcsAsync(Guid projectId, OdcListQuery? query = null, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        var list = await inner.ListProjectOdcsAsync(projectId, query, cancellationToken);
        if (await projects.CanReadProjectFinancialsAsync(projectId, cancellationToken))
        {
            return list;
        }

        var me = await projects.Access.GetEmployeeIdAsync(cancellationToken);
        return OdcTotals.Summarize(list.Entries.Where(e => IsMine(e, me)).ToList());
    }

    public async Task<OdcEntryDto?> GetAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        var entry = await inner.GetAsync(odcId, cancellationToken);
        return entry is null || await CanSeeAsync(entry, cancellationToken) ? entry : throw Hidden(odcId);
    }

    public Task<OdcEntryDto> CreateVendorOdcAsync(Guid projectId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        return inner.CreateVendorOdcAsync(projectId, request, cancellationToken);
    }

    public async Task<OdcEntryDto> UpdateVendorOdcAsync(Guid odcId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(odcId, cancellationToken);
        return await inner.UpdateVendorOdcAsync(odcId, request, cancellationToken);
    }

    public async Task DeleteVendorOdcAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(odcId, cancellationToken);
        await inner.DeleteVendorOdcAsync(odcId, cancellationToken);
    }

    /// <summary>Expense lines check their report's owner inside the service; vendor ODCs are checked here.</summary>
    public async Task<OdcEntryDto> AttachReceiptAsync(Guid odcId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        if (await inner.GetAsync(odcId, cancellationToken) is { ExpenseReportId: null })
        {
            await RequireChangeAsync(odcId, cancellationToken);
        }

        return await inner.AttachReceiptAsync(odcId, fileName, content, cancellationToken);
    }

    public async Task<ReceiptContent> OpenReceiptAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        var entry = await inner.GetAsync(odcId, cancellationToken) ?? throw WorkbenchNotFoundException.For("ODC", odcId);
        if (!await CanSeeAsync(entry, cancellationToken))
        {
            throw Hidden(odcId);
        }

        return await inner.OpenReceiptAsync(odcId, cancellationToken);
    }

    public string GetReceiptUrl(Guid odcId) => inner.GetReceiptUrl(odcId);

    public async Task<OdcEntryDto> SubmitAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(odcId, cancellationToken);
        return await inner.SubmitAsync(odcId, cancellationToken);
    }

    public Task<ExpensePolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        return inner.GetPolicyAsync(cancellationToken);
    }

    private bool IsMine(OdcEntryDto entry, Guid? me) =>
        (me is not null && entry.EmployeeId == me) || string.Equals(entry.SubmittedBy, projects.Access.Email, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> CanSeeAsync(OdcEntryDto entry, CancellationToken cancellationToken) =>
        IsMine(entry, await projects.Access.GetEmployeeIdAsync(cancellationToken))
        || await projects.CanReadProjectFinancialsAsync(entry.ProjectId, cancellationToken)
        || await projects.SupervisesAsync(entry.EmployeeId, entry.SubmittedBy, cancellationToken);

    private async Task RequireChangeAsync(Guid odcId, CancellationToken cancellationToken)
    {
        projects.Access.RequireSignedIn();
        var entry = await inner.GetAsync(odcId, cancellationToken) ?? throw WorkbenchNotFoundException.For("ODC", odcId);
        if (entry.ExpenseReportId is not null)
        {
            // Not a vendor ODC: the service refuses it with its own message.
            return;
        }

        if (!IsMine(entry, await projects.Access.GetEmployeeIdAsync(cancellationToken))
            && !await projects.CanReadProjectFinancialsAsync(entry.ProjectId, cancellationToken))
        {
            throw new WorkbenchForbiddenException("Only the person who entered this ODC, the project's manager and Finance can change it.");
        }
    }

    private static WorkbenchForbiddenException Hidden(Guid odcId) =>
        new($"You can't see ODC {odcId}: it isn't yours, and you don't manage, approve or do Finance for its project.");
}
