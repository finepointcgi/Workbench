using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

internal static class Mapping
{
    public static EmployeeDto ToDto(this Employee e) =>
        new(e.Id, e.EmployeeNumber, e.Email, e.DisplayName, e.OrgUnitId, e.SupervisorId, e.DefaultLaborCategoryId,
            e.HireDate, e.TerminationDate, e.StandardHoursPerWeek, e.IsExempt);

    public static OrgUnitDto ToDto(this OrgUnit o) => new(o.Id, o.Code, o.Name, o.ParentId);

    public static LaborCategoryDto ToDto(this LaborCategory l) => new(l.Id, l.Code, l.Name, l.IsActive);

    public static CostRateDto ToDto(this EmployeeCostRate r) => new(r.Id, r.EmployeeId, r.EffectiveFrom, r.HourlyCost);

    public static CustomerDto ToDto(this Customer c) => new(c.Id, c.Code, c.Name, c.Category);

    public static ContractDto ToDto(this Contract c, string customerName) =>
        new(c.Id, c.CustomerId, customerName, c.ContractNumber, c.Name, (ContractKind)(int)c.Type, (ContractState)(int)c.Status,
            c.IsPrime, c.PeriodStart, c.PeriodEnd, c.CeilingValue, c.FundedValue);

    public static BillRateDto ToDto(this ContractLaborRate r) => new(r.Id, r.ContractId, r.LaborCategoryId, r.EffectiveFrom, r.HourlyBillRate);

    public static ProjectDto ToDto(this Project p, string? contractNumber) =>
        new(p.Id, p.Code, p.Name, p.ContractId, contractNumber, p.ProjectManagerId, (ProjectState)(int)p.Status, p.Start, p.Finish, p.SheetId);

    public static WbsElementDto ToDto(this WbsElement w) =>
        new(w.Id, w.ProjectId, w.ParentId, w.Code, w.Name, (ChargeCodeKind)(int)w.Kind, w.IsChargeable, w.IsOpen, w.RequiresAssignment, w.ItemId, w.Order);

    public static ChargeAssignmentDto ToDto(this ChargeAssignment a) => new(a.Id, a.WbsElementId, a.EmployeeId, a.LaborCategoryId, a.From, a.To);

    public static FiscalPeriodDto ToDto(this FiscalPeriod p, int year) =>
        new(p.Id, year, p.Name, p.Number, p.Start, p.End, (FiscalPeriodState)(int)p.Status);

    public static string Required(this string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{field} is required.") : value.Trim();
}
