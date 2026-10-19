using Workbench.Domain.Payroll;

namespace Workbench.Domain.Hr;

// Personnel records. The employee itself stays Workbench.Domain.ProjectAccounting.Employee (name, number, email, org unit,
// supervisor, hire/termination dates, FLSA exemption in IsExempt); HR adds one profile row per employee plus the
// restricted EEO/veteran self-identification, emergency contacts, credentials and documents. Personal data is for HR
// (and administrators) and the employee; supervisors see only the job fields of their reports.

public enum EmploymentStatus
{
    /// <summary>Hired, start date still ahead (onboarding).</summary>
    PendingStart,
    Active,
    OnLeave,
    Suspended,
    Terminated,
}

/// <summary>An employee's HR profile: the job and the personal contact details. One per employee.</summary>
public sealed class EmployeeHrProfile : Entity
{
    public Guid EmployeeId { get; set; }

    public string? JobTitle { get; set; }

    /// <summary>The approved position this person fills (<see cref="Position"/>).</summary>
    public Guid? PositionId { get; set; }

    /// <summary>Kept in step with the payroll profile's employment type when there is one (leave eligibility reads that).</summary>
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;

    public EmploymentStatus Status { get; set; } = EmploymentStatus.Active;

    /// <summary>Office, site or "Remote".</summary>
    public string? WorkLocation { get; set; }

    /// <summary>Two-letter state of the work location (EEO-1 establishment, state taxes).</summary>
    public string? WorkState { get; set; }

    // Personal (HR and the employee only).
    public string? PreferredName { get; set; }
    public string? PersonalEmail { get; set; }
    public string? PersonalPhone { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public DateOnly? BirthDate { get; set; }

    /// <summary>Set when the employee is terminated (offboarding).</summary>
    public TerminationReason? TerminationReason { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public enum TerminationReason
{
    Voluntary,
    Involuntary,
    Retirement,
    EndOfContract,
    Other,
}

// EEO-1 Component 1 categories.
public enum EeoGender
{
    NotDisclosed,
    Male,
    Female,
}

public enum EeoRace
{
    NotDisclosed,
    HispanicOrLatino,
    White,
    BlackOrAfricanAmerican,
    NativeHawaiianOrPacificIslander,
    Asian,
    AmericanIndianOrAlaskaNative,
    TwoOrMoreRaces,
}

/// <summary>The ten EEO-1 job categories.</summary>
public enum EeoJobCategory
{
    ExecutiveSeniorOfficials,
    FirstMidOfficials,
    Professionals,
    Technicians,
    SalesWorkers,
    AdministrativeSupport,
    CraftWorkers,
    Operatives,
    LaborersHelpers,
    ServiceWorkers,
}

public enum DisabilityStatus
{
    NotDisclosed,
    Yes,
    No,
}

/// <summary>
/// EEO and veteran self-identification. Restricted: HR, administrators and the employee only, never managers or Finance;
/// every read by someone other than the employee is a security event and changes are audited without values.
/// </summary>
public sealed class EmployeeEeoRecord : Entity
{
    public Guid EmployeeId { get; set; }
    public EeoGender Gender { get; set; }
    public EeoRace Race { get; set; }

    /// <summary>Set by HR from the job (not self-identified); falls back to the position's category.</summary>
    public EeoJobCategory? JobCategory { get; set; }

    public DisabilityStatus Disability { get; set; }

    // VEVRAA protected veteran categories (VETS-4212).
    public bool IsVeteran { get; set; }
    public bool DisabledVeteran { get; set; }
    public bool RecentlySeparatedVeteran { get; set; }
    public bool ActiveDutyWartimeVeteran { get; set; }
    public bool ArmedForcesServiceMedalVeteran { get; set; }
    public DateOnly? DischargeDate { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public required string UpdatedBy { get; set; }

    public bool IsProtectedVeteran => DisabledVeteran || RecentlySeparatedVeteran || ActiveDutyWartimeVeteran || ArmedForcesServiceMedalVeteran;
}

public sealed class EmergencyContact : Entity
{
    public Guid EmployeeId { get; set; }
    public required string Name { get; set; }
    public string? Relationship { get; set; }
    public required string Phone { get; set; }
    public string? Email { get; set; }
    public bool IsPrimary { get; set; }
    public int Sequence { get; set; }
}

public enum CredentialKind
{
    Education,
    Certification,
    License,
    SecurityClearance,
}

public enum ClearanceLevel
{
    PublicTrust,
    Confidential,
    Secret,
    TopSecret,
    TopSecretSci,
}

/// <summary>A degree, certification, license or security clearance, with its expiry (alerts run from it).</summary>
public sealed class EmployeeCredential : Entity
{
    public Guid EmployeeId { get; set; }
    public CredentialKind Kind { get; set; }

    /// <summary>"B.S. Computer Science", "CISSP", "Secret".</summary>
    public required string Name { get; set; }

    public string? Issuer { get; set; }

    /// <summary>Certificate or license number; never a clearance case number.</summary>
    public string? Identifier { get; set; }

    public ClearanceLevel? ClearanceLevel { get; set; }
    public DateOnly? IssuedOn { get; set; }

    /// <summary>Expiry, or for a clearance the periodic reinvestigation due date.</summary>
    public DateOnly? ExpiresOn { get; set; }

    public string? Notes { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public required string UpdatedBy { get; set; }
}

/// <summary>A personnel document (offer letter, I-9, certificate). The bytes are a <c>StoredFile</c> (owner type <c>EmployeeDocument</c>).</summary>
public sealed class EmployeeDocument : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid FileId { get; set; }
    public required string Title { get; set; }
    public string? Category { get; set; }

    /// <summary>The employee may see and download it; otherwise HR only.</summary>
    public bool EmployeeVisible { get; set; }

    public required string UploadedBy { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}

public enum HrRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>
/// An employee's request to change their own record (contact details, address, emergency contacts). Routed through the
/// approval engine (document type <c>hr-change</c>, default: HR); approval applies the proposed values.
/// </summary>
public sealed class HrChangeRequest : Entity
{
    public Guid EmployeeId { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The proposed personal fields and emergency contacts (JSON of the request).</summary>
    public required string ChangesJson { get; set; }

    /// <summary>A plain-language list of the fields that change (no values: it is shown in the approvals inbox).</summary>
    public required string Summary { get; set; }

    public string? Comment { get; set; }
    public HrRequestStatus Status { get; set; } = HrRequestStatus.Pending;
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
}
