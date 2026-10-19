namespace Workbench.Contracts.ProjectAccounting;

// Enums mirror Workbench.Domain.ProjectAccounting by name and value.

public enum ContractKind { FirmFixedPrice, TimeAndMaterials, LaborHour, CostPlusFixedFee, CostPlusAwardFee, CostPlusIncentiveFee }

public enum ContractState { Pending, Active, Closed }

public enum ProjectState { Planning, Active, OnHold, Closed }

public enum ChargeCodeKind { Direct, Indirect, Leave }

public enum FiscalPeriodState { Open, Closed }

// ---- People ----

public sealed record OrgUnitDto(Guid Id, string Code, string Name, Guid? ParentId);

public sealed record CreateOrgUnitRequest(string Code, string Name, Guid? ParentId = null);

public sealed record LaborCategoryDto(Guid Id, string Code, string Name, bool IsActive);

public sealed record SaveLaborCategoryRequest(string Code, string Name, bool IsActive = true);

public sealed record EmployeeDto(
    Guid Id,
    string EmployeeNumber,
    string Email,
    string DisplayName,
    Guid? OrgUnitId,
    Guid? SupervisorId,
    Guid? DefaultLaborCategoryId,
    DateOnly HireDate,
    DateOnly? TerminationDate,
    decimal StandardHoursPerWeek,
    bool IsExempt);

public sealed record SaveEmployeeRequest(
    string EmployeeNumber,
    string Email,
    string DisplayName,
    Guid? OrgUnitId,
    Guid? SupervisorId,
    Guid? DefaultLaborCategoryId,
    DateOnly HireDate,
    DateOnly? TerminationDate = null,
    decimal StandardHoursPerWeek = 40m,
    bool IsExempt = true);

public sealed record CostRateDto(Guid Id, Guid EmployeeId, DateOnly EffectiveFrom, decimal HourlyCost);

public sealed record AddCostRateRequest(DateOnly EffectiveFrom, decimal HourlyCost, string Reason);

/// <summary>Employees, org units, labor categories and cost rates.</summary>
public interface IPeopleService
{
    Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>The employee record for the signed-in user, or null when they aren't an employee.</summary>
    Task<EmployeeDto?> GetCurrentEmployeeAsync(CancellationToken cancellationToken = default);

    Task<EmployeeDto> CreateEmployeeAsync(SaveEmployeeRequest request, CancellationToken cancellationToken = default);

    Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrgUnitDto>> ListOrgUnitsAsync(CancellationToken cancellationToken = default);

    Task<OrgUnitDto> CreateOrgUnitAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LaborCategoryDto>> ListLaborCategoriesAsync(CancellationToken cancellationToken = default);

    Task<LaborCategoryDto> CreateLaborCategoryAsync(SaveLaborCategoryRequest request, CancellationToken cancellationToken = default);

    Task<LaborCategoryDto> UpdateLaborCategoryAsync(Guid laborCategoryId, SaveLaborCategoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cost-rate history, newest first. Sensitive: restrict to finance roles once auth lands.</summary>
    Task<IReadOnlyList<CostRateDto>> ListCostRatesAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Adds an effective-dated cost rate. Rates are never edited; a correction is a new rate plus an audit reason.</summary>
    Task<CostRateDto> AddCostRateAsync(Guid employeeId, AddCostRateRequest request, CancellationToken cancellationToken = default);
}

// ---- Contracts and customers ----

public sealed record CustomerDto(Guid Id, string Code, string Name, string? Category);

public sealed record SaveCustomerRequest(string Code, string Name, string? Category = null);

public sealed record ContractDto(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    string ContractNumber,
    string Name,
    ContractKind Type,
    ContractState Status,
    bool IsPrime,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal CeilingValue,
    decimal FundedValue);

public sealed record SaveContractRequest(
    Guid CustomerId,
    string ContractNumber,
    string Name,
    ContractKind Type,
    ContractState Status,
    bool IsPrime,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal CeilingValue,
    decimal FundedValue);

public sealed record BillRateDto(Guid Id, Guid ContractId, Guid LaborCategoryId, DateOnly EffectiveFrom, decimal HourlyBillRate);

public sealed record AddBillRateRequest(Guid LaborCategoryId, DateOnly EffectiveFrom, decimal HourlyBillRate);

public interface IContractService
{
    Task<IReadOnlyList<CustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default);

    Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContractDto>> ListContractsAsync(CancellationToken cancellationToken = default);

    Task<ContractDto?> GetContractAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ContractDto> CreateContractAsync(SaveContractRequest request, CancellationToken cancellationToken = default);

    Task<ContractDto> UpdateContractAsync(Guid contractId, SaveContractRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillRateDto>> ListBillRatesAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<BillRateDto> AddBillRateAsync(Guid contractId, AddBillRateRequest request, CancellationToken cancellationToken = default);
}

// ---- Projects, WBS, charge assignments ----

public sealed record ProjectDto(
    Guid Id,
    string Code,
    string Name,
    Guid? ContractId,
    string? ContractNumber,
    Guid? ProjectManagerId,
    ProjectState Status,
    DateOnly? Start,
    DateOnly? Finish,
    Guid? SheetId);

public sealed record SaveProjectRequest(
    string Code,
    string Name,
    Guid? ContractId,
    Guid? ProjectManagerId,
    ProjectState Status,
    DateOnly? Start,
    DateOnly? Finish);

public sealed record WbsElementDto(
    Guid Id,
    Guid ProjectId,
    Guid? ParentId,
    string Code,
    string Name,
    ChargeCodeKind Kind,
    bool IsChargeable,
    bool IsOpen,
    bool RequiresAssignment,
    Guid? ItemId,
    int Order);

public sealed record SaveWbsElementRequest(
    Guid? ParentId,
    string Code,
    string Name,
    ChargeCodeKind Kind = ChargeCodeKind.Direct,
    bool IsChargeable = true,
    bool IsOpen = true,
    bool RequiresAssignment = true);

public sealed record ChargeAssignmentDto(Guid Id, Guid WbsElementId, Guid EmployeeId, Guid? LaborCategoryId, DateOnly? From, DateOnly? To);

public sealed record AddChargeAssignmentRequest(Guid EmployeeId, Guid? LaborCategoryId = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>A charge code an employee may record time or ODCs against on a given date.</summary>
public sealed record ChargeCodeDto(
    Guid WbsElementId,
    string Code,
    string Name,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    ChargeCodeKind Kind,
    Guid LaborCategoryId);

public interface IProjectService
{
    Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task<ProjectDto?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>The project whose plan sheet is <paramref name="sheetId"/>, if any.</summary>
    Task<ProjectDto?> GetProjectForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<ProjectDto> CreateProjectAsync(SaveProjectRequest request, CancellationToken cancellationToken = default);

    Task<ProjectDto> UpdateProjectAsync(Guid projectId, SaveProjectRequest request, CancellationToken cancellationToken = default);

    /// <summary>All WBS elements of a project, ordered by parent then order.</summary>
    Task<IReadOnlyList<WbsElementDto>> ListWbsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<WbsElementDto> CreateWbsElementAsync(Guid projectId, SaveWbsElementRequest request, CancellationToken cancellationToken = default);

    Task<WbsElementDto> UpdateWbsElementAsync(Guid wbsElementId, SaveWbsElementRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChargeAssignmentDto>> ListAssignmentsAsync(Guid wbsElementId, CancellationToken cancellationToken = default);

    Task<ChargeAssignmentDto> AddAssignmentAsync(Guid wbsElementId, AddChargeAssignmentRequest request, CancellationToken cancellationToken = default);

    Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Charge codes <paramref name="employeeId"/> may use on <paramref name="date"/>: open, chargeable leaf
    /// elements of active projects that either don't require assignment or have a matching assignment.
    /// </summary>
    Task<IReadOnlyList<ChargeCodeDto>> ListChargeCodesAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="ListChargeCodesAsync"/> for several dates (a timesheet's week) at once, keyed by date. The same rules;
    /// the server answers in one pass, other implementations ask date by date.
    /// </summary>
    async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<ChargeCodeDto>>> ListChargeCodesForDatesAsync(
        Guid employeeId, IReadOnlyCollection<DateOnly> dates, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<DateOnly, IReadOnlyList<ChargeCodeDto>>();
        foreach (var date in dates.Distinct())
        {
            result[date] = await ListChargeCodesAsync(employeeId, date, cancellationToken);
        }

        return result;
    }
}

// ---- Fiscal calendar ----

public sealed record FiscalPeriodDto(Guid Id, int FiscalYear, string Name, int Number, DateOnly Start, DateOnly End, FiscalPeriodState Status);

public interface IFiscalCalendarService
{
    Task<IReadOnlyList<FiscalPeriodDto>> ListPeriodsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default);

    /// <summary>The period containing <paramref name="date"/>, or null when the calendar doesn't cover it.</summary>
    Task<FiscalPeriodDto?> GetPeriodForDateAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>Creates a fiscal year of 12 monthly periods starting on <paramref name="start"/> (day 1 of a month).</summary>
    Task<IReadOnlyList<FiscalPeriodDto>> CreateFiscalYearAsync(int fiscalYear, DateOnly start, CancellationToken cancellationToken = default);

    Task<FiscalPeriodDto> SetPeriodStatusAsync(Guid periodId, FiscalPeriodState status, string reason, CancellationToken cancellationToken = default);
}

// ---- Audit ----

public sealed record AuditEventDto(Guid Id, string EntityType, Guid EntityId, string Action, string? BeforeJson, string? AfterJson, string Actor, DateTime OccurredAtUtc, string? Reason);

public interface IAuditLogService
{
    Task<IReadOnlyList<AuditEventDto>> ListAsync(string? entityType = null, Guid? entityId = null, int limit = 200, CancellationToken cancellationToken = default);
}
