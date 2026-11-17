namespace Workbench.Contracts.Payroll;

// The file-based Paylocity exchange (no live API needed): mapping, the export history and the roster import.
// Payroll services are Finance/Admin; the roster import is HR (it creates and changes personnel records).

// ---- Mapping ----

/// <summary>A pay group and the Paylocity company code its files are filed under.</summary>
public sealed record PaylocityCompanyRow(Guid PayGroupId, string PayGroupCode, string PayGroupName, string CompanyCode, int Employees, bool IsActive);

/// <summary>
/// An org unit and what the hours export sends as its Paylocity cost center: <see cref="MappedCode"/> when set, else the
/// org unit's own code (<see cref="SentAs"/>).
/// </summary>
public sealed record PaylocityCostCenterRow(Guid OrgUnitId, string OrgUnitCode, string OrgUnitName, string? MappedCode, string SentAs, int Employees);

public sealed record SaveCostCenterMapping(Guid OrgUnitId, string? Code);

/// <summary>
/// One employee's Paylocity id and whether their hours can be exported. <see cref="Status"/> is <c>Ready</c>, <c>NoId</c>,
/// <c>DuplicateId</c> or <c>NoPayGroup</c>.
/// </summary>
public sealed record PaylocityEmployeeRow(Guid EmployeeId, string EmployeeNumber, string Name, string? PayGroupCode, string? PaylocityId, string? CostCenter, string Status, string? Note);

public sealed record PaylocityMappingDto(
    IReadOnlyList<PaylocityCompanyRow> Companies,
    IReadOnlyList<PaylocityCostCenterRow> CostCenters,
    IReadOnlyList<PaylocityEmployeeRow> Employees,
    IReadOnlyList<string> Problems);

// ---- Export history ----

/// <summary>
/// One hours export of a pay period. <see cref="Sha256"/> is the checksum of the file as exported: compare it with the
/// file in Paylocity's import log. <see cref="IsReExport"/> exports repeat the period on purpose and carry the reason.
/// </summary>
public sealed record PayrollExportRecordDto(
    Guid Id,
    Guid RunId,
    Guid PayGroupId,
    string PayGroupCode,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int Sequence,
    string Method,
    string FileName,
    string Sha256,
    int LineCount,
    int EmployeeCount,
    decimal TotalHours,
    int FlaggedCount,
    bool IsReExport,
    string? Note,
    string ExportedBy,
    DateTime ExportedAtUtc);

public sealed record PayrollExportFileDto(string FileName, string ContentType, string Content, string Sha256);

// ---- Roster import ----

/// <summary>What a roster row does to Workbench.</summary>
public enum RosterAction { Create, Update, NoChange, Skip, Error }

/// <summary>Employee fields a roster import can set. Fields Workbench owns are never changed on an existing employee.</summary>
public static class PaylocityRosterFields
{
    public const string Name = "name";
    public const string Email = "email";
    public const string HireDate = "hiredate";
    public const string OrgUnit = "orgunit";
    public const string JobTitle = "jobtitle";
    public const string Exempt = "exempt";
    public const string EmploymentType = "employmenttype";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Name, "Name"), (Email, "Email"), (HireDate, "Hire date"), (OrgUnit, "Department / org unit"), (JobTitle, "Job title"),
        (Exempt, "Exempt (FLSA)"), (EmploymentType, "Full-time / part-time"),
    ];
}

public sealed record PaylocityRosterSettingsDto(IReadOnlyList<string> OwnedFields, DateTime? UpdatedAtUtc, string? UpdatedBy);

public sealed record SavePaylocityRosterSettingsRequest(IReadOnlyList<string> OwnedFields);

/// <summary>
/// A Paylocity employee export (CSV text). <see cref="DryRun"/> only previews. Terminations and rehires found in the
/// file change nothing unless <see cref="ApplyTerminations"/> / <see cref="ApplyRehires"/> are set. New employees are
/// linked to <see cref="PayGroupId"/> (a payroll profile without a pay rate) when one is given.
/// </summary>
public sealed record PaylocityRosterRequest(
    string FileName,
    string Content,
    bool DryRun = true,
    Guid? PayGroupId = null,
    bool ApplyTerminations = false,
    bool ApplyRehires = false);

public sealed record RosterFieldChange(string Field, string? From, string? To, bool Applied, string? Reason);

public sealed record PaylocityRosterRowDto(
    int Line,
    string PaylocityId,
    string Name,
    string? Email,
    RosterAction Action,
    Guid? EmployeeId,
    string MatchedBy,
    IReadOnlyList<RosterFieldChange> Changes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notices);

/// <summary>An employee who has a Paylocity id in Workbench but isn't in the file (never changed).</summary>
public sealed record RosterMissingDto(Guid EmployeeId, string Name, string PaylocityId, bool IsActive);

public sealed record PaylocityRosterResultDto(
    bool DryRun,
    string FileName,
    string Sha256,
    IReadOnlyList<PaylocityRosterRowDto> Rows,
    IReadOnlyList<RosterMissingDto> MissingFromFile,
    IReadOnlyList<string> Warnings)
{
    public int Count(RosterAction action) => Rows.Count(r => r.Action == action);
}

public interface IPaylocityService
{
    /// <summary>Company codes, cost centers and employee ids as the next export will send them, with what is missing.</summary>
    Task<PaylocityMappingDto> GetMappingAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the Paylocity cost center for org units (a blank code goes back to the org unit's own code). Audited.</summary>
    Task<PaylocityMappingDto> SaveCostCentersAsync(IReadOnlyList<SaveCostCenterMapping> mappings, CancellationToken cancellationToken = default);

    /// <summary>Every hours export, newest first.</summary>
    Task<IReadOnlyList<PayrollExportRecordDto>> ListExportsAsync(Guid? payGroupId = null, Guid? payPeriodId = null, CancellationToken cancellationToken = default);

    /// <summary>The file as it was exported (checked against its checksum).</summary>
    Task<PayrollExportFileDto> GetExportFileAsync(Guid exportId, CancellationToken cancellationToken = default);

    Task<PaylocityRosterSettingsDto> GetRosterSettingsAsync(CancellationToken cancellationToken = default);

    Task<PaylocityRosterSettingsDto> SaveRosterSettingsAsync(SavePaylocityRosterSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Previews (dry run) or applies a Paylocity employee export to the personnel records.</summary>
    Task<PaylocityRosterResultDto> ImportRosterAsync(PaylocityRosterRequest request, CancellationToken cancellationToken = default);
}
