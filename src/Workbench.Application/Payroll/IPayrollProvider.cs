namespace Workbench.Application.Payroll;

/// <summary>
/// One line of the hours export: an employee's hours under one earning code. <see cref="Category"/> is the Workbench
/// category (<c>REG</c>, <c>OT</c> or a leave charge code), for formats that put regular and overtime hours in their own columns.
/// </summary>
public sealed record PayrollHoursLine(string ProviderEmployeeId, string EmployeeName, string EarningCode, decimal Hours, string? CostCenter, string? Category = null);

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

/// <summary>Which payroll results to fetch from the provider's API.</summary>
public sealed record PayrollRegisterQuery(string CompanyCode, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly CheckDate, IReadOnlyList<string> ProviderEmployeeIds);

/// <summary>What sending hours through the provider's API did. The provider's payroll still has to be reviewed and submitted there.</summary>
public sealed record PayrollHoursSubmission(int Employees, int Lines, string Message);

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

    /// <summary>The provider's own earning codes for <c>REG</c>, <c>OT</c> and the usual leave codes, until Finance maps them.</summary>
    IReadOnlyList<Contracts.Payroll.EarningCodeMapping> DefaultEarningCodes => EarningCodes.Defaults;

    /// <summary>
    /// What the connect screen asks for (the customer's own API credentials, stored encrypted). Null when the adapter
    /// has no customer connection (Paylocity's API credentials come from server configuration).
    /// </summary>
    PayrollConnectionSpec? ConnectionSpec => null;

    /// <summary>True when the API can be called: credentials saved (or configured on the server).</summary>
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(ApiConfigured);

    /// <summary>Signs in with the saved credentials and reads something harmless. Returns what it found.</summary>
    Task<string> TestConnectionAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Name} has no connection to test here.");

    /// <summary>True when hours can be sent through the API (as well as the import file).</summary>
    bool SupportsHoursApi => false;

    /// <summary>Puts the hours on the provider's open payroll for the period. The customer reviews and submits it there.</summary>
    Task<PayrollHoursSubmission> SendHoursAsync(PayrollHoursExport export, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Name} takes hours as an import file only. Download the file and import it in {Name}.");

    /// <summary>True when processed payroll results can be read through the API (as well as the register file).</summary>
    bool SupportsRegisterApi => false;

    /// <summary>The processed payroll's checks for the period, as register rows (gross, taxes, deductions, net).</summary>
    Task<ParsedPayrollRegister> FetchRegisterAsync(PayrollRegisterQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Name} payroll results come from the register file. Export it from {Name} and import it here.");
}
