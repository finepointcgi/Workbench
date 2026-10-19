namespace Workbench.Application.Payroll;

/// <summary>One line of the hours export: an employee's hours under one earning code.</summary>
public sealed record PayrollHoursLine(string ProviderEmployeeId, string EmployeeName, string EarningCode, decimal Hours, string? CostCenter);

/// <summary>Approved hours for a pay period, ready for a provider to format.</summary>
public sealed record PayrollHoursExport(string CompanyCode, string PayGroupCode, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly CheckDate, IReadOnlyList<PayrollHoursLine> Lines);

public sealed record PayrollExportFile(string FileName, string ContentType, string Content);

/// <summary>One check from the provider's payroll register.</summary>
public sealed record PayrollRegisterRow(
    string ProviderEmployeeId,
    string EmployeeName,
    DateOnly CheckDate,
    decimal GrossWages,
    decimal EmployeeTaxes,
    decimal EmployerTaxes,
    decimal Deductions,
    decimal EmployerBenefits,
    decimal NetPay);

public sealed record ParsedPayrollRegister(IReadOnlyList<PayrollRegisterRow> Rows, IReadOnlyList<string> Warnings);

/// <summary>An employee as Workbench knows them, for pushing to the provider.</summary>
public sealed record PayrollEmployeeRecord(
    Guid EmployeeId,
    string? ProviderEmployeeId,
    string EmployeeNumber,
    string FirstName,
    string LastName,
    string Email,
    DateOnly HireDate,
    DateOnly? TerminationDate,
    bool IsExempt,
    string PayType,
    decimal? AnnualSalary,
    decimal? HourlyRate,
    string? CostCenter);

public enum PayrollSyncAction { Create, Update, Terminate }

/// <summary>
/// One benefit deduction for a pay period: a flat <see cref="Amount"/> per period, or a <see cref="Percent"/> of pay
/// (401(k)), with the employer's contribution when there is one.
/// </summary>
public sealed record PayrollDeductionLine(string ProviderEmployeeId, string EmployeeName, string DeductionCode, decimal? Amount, decimal? Percent, decimal? EmployerAmount, bool IsPreTax);

/// <summary>Benefit deductions for a pay period, ready for a provider to format (from HR's benefit elections).</summary>
public sealed record PayrollDeductionExport(string CompanyCode, string PayGroupCode, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly CheckDate, IReadOnlyList<PayrollDeductionLine> Lines);

public sealed record PayrollSyncOutcome(Guid EmployeeId, PayrollSyncAction Action, bool Succeeded, string? ProviderEmployeeId, string Message);

/// <summary>
/// A payroll provider adapter. Workbench never calculates payroll taxes: the provider does. An adapter turns approved
/// hours into the provider's import file, reads the provider's payroll register back, and (optionally) keeps the
/// provider's employee records in step.
/// </summary>
public interface IPayrollProvider
{
    /// <summary>Stable key stored on pay groups, e.g. <c>paylocity</c>.</summary>
    string Key { get; }

    string Name { get; }

    /// <summary>False for adapters that aren't built yet: the provider picker shows them as "coming soon".</summary>
    bool IsSupported { get; }

    bool SupportsEmployeeSync { get; }

    /// <summary>True when the provider's API credentials are configured (employee sync needs them).</summary>
    bool ApiConfigured { get; }

    /// <summary>Formats the hours as the provider's payroll import file.</summary>
    PayrollExportFile ExportHours(PayrollHoursExport export);

    /// <summary>Reads the provider's payroll register export.</summary>
    ParsedPayrollRegister ParseRegister(string content);

    /// <summary>Formats benefit deductions as the provider's deduction import file. Adapters without one say so.</summary>
    PayrollExportFile ExportDeductions(PayrollDeductionExport export) =>
        throw new NotSupportedException($"{Name} doesn't take a benefit deduction import yet. Enter the deductions in {Name} by hand.");

    /// <summary>Creates, updates or terminates employees in the provider. Live API calls; never used in tests.</summary>
    Task<IReadOnlyList<PayrollSyncOutcome>> SyncEmployeesAsync(string companyCode, IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes, CancellationToken cancellationToken);
}

/// <summary>Adapters that are listed but not built yet. Every operation says so clearly.</summary>
public abstract class ComingSoonPayrollProvider : IPayrollProvider
{
    public abstract string Key { get; }

    public abstract string Name { get; }

    public bool IsSupported => false;

    public bool SupportsEmployeeSync => false;

    public bool ApiConfigured => false;

    public PayrollExportFile ExportHours(PayrollHoursExport export) => throw NotYet("hours export");

    public ParsedPayrollRegister ParseRegister(string content) => throw NotYet("payroll register import");

    public PayrollExportFile ExportDeductions(PayrollDeductionExport export) => throw NotYet("benefit deduction export");

    public Task<IReadOnlyList<PayrollSyncOutcome>> SyncEmployeesAsync(string companyCode, IReadOnlyList<(PayrollEmployeeRecord Employee, PayrollSyncAction Action)> changes, CancellationToken cancellationToken) =>
        throw NotYet("employee sync");

    private NotSupportedException NotYet(string what) =>
        new($"{Name} {what} isn't supported yet (coming soon). Workbench integrates with Paylocity today; use a Paylocity pay group or export hours manually.");
}

public sealed class AdpPayrollProvider : ComingSoonPayrollProvider
{
    public override string Key => "adp";

    public override string Name => "ADP Workforce Now";
}

public sealed class PaychexPayrollProvider : ComingSoonPayrollProvider
{
    public override string Key => "paychex";

    public override string Name => "Paychex Flex";
}

public sealed class GustoPayrollProvider : ComingSoonPayrollProvider
{
    public override string Key => "gusto";

    public override string Name => "Gusto";
}
