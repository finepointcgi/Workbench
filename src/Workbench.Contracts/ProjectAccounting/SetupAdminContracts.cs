namespace Workbench.Contracts.ProjectAccounting;

/// <summary>Filters for the audit log viewer. Dates are inclusive UTC calendar days.</summary>
public sealed record AuditQuery(string? EntityType = null, Guid? EntityId = null, DateOnly? From = null, DateOnly? To = null, int Limit = 500);

/// <summary>
/// What the first-run checklist on /setup ticks off. The later flags were added for the whole-system checklist (users,
/// chart of accounts, banking, payroll, approvals, indirect rates and billing); they default to false.
/// </summary>
public sealed record SetupChecklistDto(
    bool HasOpenFiscalPeriods,
    bool HasLaborCategories,
    bool HasEmployees,
    bool HasCostRates,
    bool HasCustomerAndContract,
    bool HasProjectWithWbs,
    bool HasChargeAssignments,
    bool HasInvitedUsers = false,
    bool HasChartOfAccounts = false,
    bool HasBankAccount = false,
    bool HasPayrollProvider = false,
    bool HasApprovalWorkflows = false,
    bool HasIndirectRates = false,
    bool HasBillingDetails = false);

/// <summary>Setup operations the admin screens need beyond the core services.</summary>
public interface ISetupAdminService
{
    Task<OrgUnitDto> UpdateOrgUnitAsync(Guid orgUnitId, CreateOrgUnitRequest request, CancellationToken cancellationToken = default);

    Task<CustomerDto> UpdateCustomerAsync(Guid customerId, SaveCustomerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Audit events matching <paramref name="query"/>, newest first.</summary>
    Task<IReadOnlyList<AuditEventDto>> SearchAuditAsync(AuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>Distinct audit entity types, for the viewer's filter.</summary>
    Task<IReadOnlyList<string>> ListAuditEntityTypesAsync(CancellationToken cancellationToken = default);

    Task<SetupChecklistDto> GetChecklistAsync(CancellationToken cancellationToken = default);
}
