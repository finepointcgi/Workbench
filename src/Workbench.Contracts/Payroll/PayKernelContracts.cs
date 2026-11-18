namespace Workbench.Contracts.Payroll;

// The pay calendar and pay profile kinds core keeps (leave eligibility and accruals read them whether or not the payroll module is installed).
// The payroll module's contracts (pay groups, runs, providers, Paylocity) are in the module repository.

// Enums mirror Workbench.Domain.Payroll by name and value.
public enum PayFrequencyKind { Weekly, Biweekly, SemiMonthly, Monthly }

public enum PayTypeKind { Hourly, Salaried }

public enum EmploymentTypeKind { FullTime, PartTime, Temporary, Seasonal, Intern }
