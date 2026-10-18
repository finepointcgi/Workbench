namespace Workbench.Application.Financials;

/// <summary>An employee's charge assignment to a WBS element, with the labor category it resolves to.</summary>
public sealed record PlanningAssignment(Guid WbsElementId, Guid EmployeeId, Guid? LaborCategoryId, DateOnly? From, DateOnly? To);

public sealed record PlanningEmployee(Guid EmployeeId, Guid? DefaultLaborCategoryId, DateOnly HireDate, DateOnly? TerminationDate);

/// <summary>
/// Planning rate for a labor budget line: the average cost rate of the employees assigned to the WBS element with that
/// labor category, falling back to the average cost rate of everyone whose default labor category it is. A line with
/// no labor category uses everyone assigned to the element, then everyone. No rates at all gives 0.
/// </summary>
public sealed class PlanningRateCalculator(
    IEnumerable<PlanningAssignment> assignments,
    IEnumerable<PlanningEmployee> employees,
    EffectiveRateTable<Guid> costRates)
{
    private readonly List<PlanningAssignment> _assignments = assignments.ToList();
    private readonly Dictionary<Guid, PlanningEmployee> _employees = employees.ToDictionary(e => e.EmployeeId);

    public decimal RateFor(Guid wbsElementId, Guid? laborCategoryId, DateOnly asOf)
    {
        var assigned = _assignments
            .Where(a => a.WbsElementId == wbsElementId
                && (a.From is null || a.From <= asOf) && (a.To is null || a.To >= asOf)
                && _employees.ContainsKey(a.EmployeeId)
                && (laborCategoryId is null || (a.LaborCategoryId ?? _employees[a.EmployeeId].DefaultLaborCategoryId) == laborCategoryId))
            .Select(a => a.EmployeeId);
        if (Average(assigned, asOf) is { } rate)
        {
            return rate;
        }

        var byCategory = _employees.Values
            .Where(e => e.HireDate <= asOf && (e.TerminationDate is null || e.TerminationDate >= asOf))
            .Where(e => laborCategoryId is null || e.DefaultLaborCategoryId == laborCategoryId)
            .Select(e => e.EmployeeId);
        return Average(byCategory, asOf) ?? 0m;
    }

    private decimal? Average(IEnumerable<Guid> employeeIds, DateOnly asOf)
    {
        var rates = employeeIds.Distinct().Select(id => costRates.Find(id, asOf)).OfType<decimal>().ToList();
        return rates.Count == 0 ? null : Money.RoundRate(rates.Average());
    }
}
