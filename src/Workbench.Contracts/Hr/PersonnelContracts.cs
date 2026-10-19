using Workbench.Contracts.Payroll;

namespace Workbench.Contracts.Hr;

// HR personnel records. Enums mirror Workbench.Domain.Hr by name and value.
//
// Who sees what (enforced on the server):
// - HR and administrators: everything.
// - The employee: their own record (job, personal, emergency contacts, credentials, documents marked visible, their own
//   EEO self-identification); they change personal fields and emergency contacts by request (approved by HR).
// - A supervisor (direct or up the chain): the job fields and credentials of their reports, nothing personal.
// - Finance: the job fields of everyone (for positions, labor and compensation), nothing personal or EEO.
// - Everyone: the directory (name, title, org unit, supervisor) and the org chart.

public enum EmploymentStatus { PendingStart, Active, OnLeave, Suspended, Terminated }

public enum TerminationReason { Voluntary, Involuntary, Retirement, EndOfContract, Other }

public enum EeoGender { NotDisclosed, Male, Female }

public enum EeoRace { NotDisclosed, HispanicOrLatino, White, BlackOrAfricanAmerican, NativeHawaiianOrPacificIslander, Asian, AmericanIndianOrAlaskaNative, TwoOrMoreRaces }

public enum EeoJobCategory
{
    ExecutiveSeniorOfficials, FirstMidOfficials, Professionals, Technicians, SalesWorkers, AdministrativeSupport, CraftWorkers, Operatives,
    LaborersHelpers, ServiceWorkers,
}

public enum DisabilityStatus { NotDisclosed, Yes, No }

public enum CredentialKind { Education, Certification, License, SecurityClearance }

public enum ClearanceLevel { PublicTrust, Confidential, Secret, TopSecret, TopSecretSci }

public enum HrRequestStatus { Pending, Approved, Rejected, Cancelled }

/// <summary>How much of a record the caller may see.</summary>
public enum HrRecordAccess
{
    /// <summary>Directory fields only.</summary>
    Directory,

    /// <summary>Job fields and credentials (supervisors, Finance).</summary>
    Job,

    /// <summary>The employee's own record.</summary>
    Self,

    /// <summary>HR and administrators.</summary>
    Full,
}

public static class HrLabels
{
    public static string Label(EmploymentStatus s) => s switch
    {
        EmploymentStatus.PendingStart => "Starting soon",
        EmploymentStatus.OnLeave => "On leave",
        _ => s.ToString(),
    };

    public static string Label(EmploymentTypeKind t) => t switch
    {
        EmploymentTypeKind.FullTime => "Full-time",
        EmploymentTypeKind.PartTime => "Part-time",
        _ => t.ToString(),
    };

    public static string Label(ClearanceLevel c) => c switch
    {
        ClearanceLevel.PublicTrust => "Public Trust",
        ClearanceLevel.TopSecret => "Top Secret",
        ClearanceLevel.TopSecretSci => "TS/SCI",
        _ => c.ToString(),
    };

    public static string Label(CredentialKind k) => k == CredentialKind.SecurityClearance ? "Security clearance" : k.ToString();

    public static string Label(EeoJobCategory c) => c switch
    {
        EeoJobCategory.ExecutiveSeniorOfficials => "Executive/senior level officials and managers",
        EeoJobCategory.FirstMidOfficials => "First/mid-level officials and managers",
        EeoJobCategory.AdministrativeSupport => "Administrative support workers",
        EeoJobCategory.CraftWorkers => "Craft workers",
        EeoJobCategory.LaborersHelpers => "Laborers and helpers",
        EeoJobCategory.SalesWorkers => "Sales workers",
        EeoJobCategory.ServiceWorkers => "Service workers",
        _ => c.ToString(),
    };

    public static string Label(EeoRace r) => r switch
    {
        EeoRace.HispanicOrLatino => "Hispanic or Latino",
        EeoRace.BlackOrAfricanAmerican => "Black or African American",
        EeoRace.NativeHawaiianOrPacificIslander => "Native Hawaiian or Other Pacific Islander",
        EeoRace.AmericanIndianOrAlaskaNative => "American Indian or Alaska Native",
        EeoRace.TwoOrMoreRaces => "Two or more races",
        EeoRace.NotDisclosed => "I don't wish to answer",
        _ => r.ToString(),
    };
}

/// <summary>A row of the employee list. <see cref="JobTitle"/> and the rest are directory-level (everyone may see them).</summary>
public sealed record EmployeeSummaryDto(
    Guid EmployeeId,
    string EmployeeNumber,
    string DisplayName,
    string Email,
    string? JobTitle,
    string? OrgUnitName,
    Guid? SupervisorId,
    string? SupervisorName,
    EmploymentStatus Status,
    DateOnly HireDate,
    DateOnly? TerminationDate);

public sealed record JobInfoDto(
    string? JobTitle,
    Guid? PositionId,
    string? PositionCode,
    string? PositionTitle,
    Guid? OrgUnitId,
    string? OrgUnitName,
    Guid? SupervisorId,
    string? SupervisorName,
    EmploymentTypeKind EmploymentType,
    EmploymentStatus Status,
    bool IsExempt,
    string? WorkLocation,
    string? WorkState,
    DateOnly HireDate,
    DateOnly? TerminationDate,
    TerminationReason? TerminationReason);

public sealed record PersonalInfoDto(
    string? PreferredName,
    string? PersonalEmail,
    string? PersonalPhone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    DateOnly? BirthDate);

public sealed record EmergencyContactDto(Guid Id, string Name, string? Relationship, string Phone, string? Email, bool IsPrimary);

public sealed record CredentialDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    CredentialKind Kind,
    string Name,
    string? Issuer,
    string? Identifier,
    ClearanceLevel? ClearanceLevel,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Notes,
    int? DaysToExpiry);

public sealed record EmployeeDocumentDto(Guid Id, string Title, string? Category, string FileName, long SizeBytes, bool EmployeeVisible, string UploadedBy, DateTime UploadedAtUtc);

public sealed record EeoRecordDto(
    EeoGender Gender,
    EeoRace Race,
    EeoJobCategory? JobCategory,
    DisabilityStatus Disability,
    bool IsVeteran,
    bool DisabledVeteran,
    bool RecentlySeparatedVeteran,
    bool ActiveDutyWartimeVeteran,
    bool ArmedForcesServiceMedalVeteran,
    DateOnly? DischargeDate,
    DateTime? UpdatedAtUtc)
{
    public bool IsProtectedVeteran => DisabledVeteran || RecentlySeparatedVeteran || ActiveDutyWartimeVeteran || ArmedForcesServiceMedalVeteran;

    public static EeoRecordDto Empty { get; } = new(EeoGender.NotDisclosed, EeoRace.NotDisclosed, null, DisabilityStatus.NotDisclosed, false, false, false, false, false, null, null);
}

/// <summary>
/// One employee's record, shaped by <see cref="Access"/>: sections the caller may not see are null (never empty), so
/// the UI can tell "hidden" from "nothing on file". EEO data is never part of it (<see cref="IHrPersonnelService.GetEeoAsync"/>).
/// </summary>
public sealed record EmployeeRecordDto(
    EmployeeSummaryDto Summary,
    HrRecordAccess Access,
    JobInfoDto? Job,
    PersonalInfoDto? Personal,
    IReadOnlyList<EmergencyContactDto>? EmergencyContacts,
    IReadOnlyList<CredentialDto>? Credentials,
    IReadOnlyList<EmployeeDocumentDto>? Documents,
    IReadOnlyList<HrChangeRequestDto>? PendingRequests);

public sealed record EmployeeQuery(string? Search = null, bool IncludeTerminated = false, Guid? OrgUnitId = null);

public sealed record SaveJobInfoRequest(
    string? JobTitle,
    Guid? PositionId,
    Guid? OrgUnitId,
    Guid? SupervisorId,
    EmploymentTypeKind EmploymentType,
    EmploymentStatus Status,
    bool IsExempt,
    string? WorkLocation,
    string? WorkState);

public sealed record SavePersonalInfoRequest(
    string? PreferredName,
    string? PersonalEmail,
    string? PersonalPhone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    DateOnly? BirthDate);

public sealed record SaveEmergencyContact(string Name, string? Relationship, string Phone, string? Email, bool IsPrimary);

public sealed record SaveCredentialRequest(
    CredentialKind Kind,
    string Name,
    string? Issuer,
    string? Identifier,
    ClearanceLevel? ClearanceLevel,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Notes);

public sealed record SaveEeoRequest(
    EeoGender Gender,
    EeoRace Race,
    DisabilityStatus Disability,
    bool IsVeteran,
    bool DisabledVeteran,
    bool RecentlySeparatedVeteran,
    bool ActiveDutyWartimeVeteran,
    bool ArmedForcesServiceMedalVeteran,
    DateOnly? DischargeDate,

    /// <summary>HR only; ignored when the employee self-identifies.</summary>
    EeoJobCategory? JobCategory = null);

/// <summary>
/// An employee asks to change their own personal details and/or emergency contacts. Null sections stay as they are;
/// <see cref="EmergencyContacts"/> replaces the whole list.
/// </summary>
public sealed record SubmitHrChangeRequest(SavePersonalInfoRequest? Personal, IReadOnlyList<SaveEmergencyContact>? EmergencyContacts, string? Comment);

public sealed record HrChangeRequestDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    string RequestedBy,
    DateTime CreatedAtUtc,
    string Summary,
    string? Comment,
    HrRequestStatus Status,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionComment,

    /// <summary>The proposed values: HR and the employee only.</summary>
    SubmitHrChangeRequest? Changes);

public sealed record HrDecisionRequest(string? Comment);

public sealed record DocumentContent(string FileName, string ContentType, Stream Content);

public sealed record OrgChartNodeDto(Guid EmployeeId, string DisplayName, string? JobTitle, string? OrgUnitName, Guid? SupervisorId, int DirectReports);

public interface IHrPersonnelService
{
    /// <summary>The directory: active employees (and terminated ones for HR when asked), directory fields only.</summary>
    Task<IReadOnlyList<EmployeeSummaryDto>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken = default);

    /// <summary>One record, shaped by what the caller may see.</summary>
    Task<EmployeeRecordDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>The signed-in person's own record. Not found without an employee record.</summary>
    Task<EmployeeRecordDto> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>HR: job fields. Writes the supervisor, org unit and FLSA status to the employee and the employment type to the payroll profile.</summary>
    Task<EmployeeRecordDto> SaveJobAsync(Guid employeeId, SaveJobInfoRequest request, CancellationToken cancellationToken = default);

    Task<EmployeeRecordDto> SavePersonalAsync(Guid employeeId, SavePersonalInfoRequest request, CancellationToken cancellationToken = default);

    Task<EmployeeRecordDto> SaveEmergencyContactsAsync(Guid employeeId, IReadOnlyList<SaveEmergencyContact> contacts, CancellationToken cancellationToken = default);

    /// <summary>HR, administrators or the employee. Reading someone else's is a security event (<c>hr.eeo_viewed</c>).</summary>
    Task<EeoRecordDto> GetEeoAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>HR (any employee) or the employee (self-identification). Audited without values.</summary>
    Task<EeoRecordDto> SaveEeoAsync(Guid employeeId, SaveEeoRequest request, CancellationToken cancellationToken = default);

    Task<CredentialDto> SaveCredentialAsync(Guid employeeId, Guid? credentialId, SaveCredentialRequest request, CancellationToken cancellationToken = default);

    Task DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default);

    /// <summary>Credentials expiring (or already expired) within <paramref name="withinDays"/>: HR all, supervisors their reports, others their own.</summary>
    Task<IReadOnlyList<CredentialDto>> ListExpiringCredentialsAsync(int withinDays, CancellationToken cancellationToken = default);

    Task<EmployeeDocumentDto> UploadDocumentAsync(Guid employeeId, string title, string? category, bool employeeVisible, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>HR, or the employee when the document is marked visible to them.</summary>
    Task<DocumentContent> OpenDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>The employee asks HR to change their own personal details or emergency contacts.</summary>
    Task<HrChangeRequestDto> RequestChangeAsync(SubmitHrChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>HR: every request (pending only by default). Others: their own.</summary>
    Task<IReadOnlyList<HrChangeRequestDto>> ListChangeRequestsAsync(bool pendingOnly = true, CancellationToken cancellationToken = default);

    Task<HrChangeRequestDto> ApproveChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default);

    Task<HrChangeRequestDto> RejectChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default);

    Task<HrChangeRequestDto> CancelChangeAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>Everyone: the reporting tree (supervisor links) of active employees with their titles.</summary>
    Task<IReadOnlyList<OrgChartNodeDto>> GetOrgChartAsync(CancellationToken cancellationToken = default);
}
