namespace Workbench.Contracts.Crm;

public enum CrmAccountKind { Agency, Office, Prime, Partner, Competitor, Other }

public enum ContactRole
{
    ContractingOfficer,
    ContractingSpecialist,
    ProgramManager,
    TechnicalLead,
    SmallBusinessSpecialist,
    Executive,
    BusinessDevelopment,
    Other,
}

public enum CrmActivityKind { Call, Meeting, Email, Note, Task }

public static class CrmLabels
{
    public static bool IsGovernment(this CrmAccountKind kind) => kind is CrmAccountKind.Agency or CrmAccountKind.Office;

    public static string Label(ContactRole role) => role switch
    {
        ContactRole.ContractingOfficer => "Contracting officer",
        ContactRole.ContractingSpecialist => "Contracting specialist",
        ContactRole.ProgramManager => "Program manager",
        ContactRole.TechnicalLead => "Technical lead",
        ContactRole.SmallBusinessSpecialist => "Small business specialist",
        ContactRole.BusinessDevelopment => "Business development",
        _ => role.ToString(),
    };
}

public sealed record CrmAccountDto(
    Guid Id,
    string Name,
    string? Acronym,
    CrmAccountKind Kind,
    Guid? ParentId,
    string? ParentName,
    string? Uei,
    string? CageCode,
    string? Website,
    string? Phone,
    string? Address,
    string? Notes,
    Guid? CustomerId,
    string? CustomerName,
    bool IsActive,
    int ContactCount,
    int OpenOpportunityCount);

public sealed record SaveCrmAccountRequest(
    string Name,
    CrmAccountKind Kind,
    string? Acronym = null,
    Guid? ParentId = null,
    string? Uei = null,
    string? CageCode = null,
    string? Website = null,
    string? Phone = null,
    string? Address = null,
    string? Notes = null,
    bool IsActive = true);

public sealed record CrmContactDto(
    Guid Id,
    Guid AccountId,
    string AccountName,
    string FirstName,
    string LastName,
    string? Title,
    ContactRole Role,
    string? Email,
    string? Phone,
    string? Notes,
    bool IsActive)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public sealed record SaveCrmContactRequest(
    Guid AccountId,
    string FirstName,
    string LastName,
    ContactRole Role = ContactRole.Other,
    string? Title = null,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    bool IsActive = true);

public sealed record CrmActivityDto(
    Guid Id,
    CrmActivityKind Kind,
    string Subject,
    string? Body,
    DateOnly? OccurredOn,
    DateOnly? DueOn,
    string? AssignedTo,
    bool IsDone,
    Guid? AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? OpportunityId,
    string? OpportunityName,
    string CreatedBy,
    DateTime CreatedAtUtc);

/// <summary>Logs a call, meeting, email or note (<see cref="OccurredOn"/>), or adds a task (<see cref="DueOn"/>, <see cref="AssignedTo"/>).</summary>
public sealed record SaveCrmActivityRequest(
    CrmActivityKind Kind,
    string Subject,
    string? Body = null,
    Guid? AccountId = null,
    Guid? ContactId = null,
    Guid? OpportunityId = null,
    DateOnly? OccurredOn = null,
    DateOnly? DueOn = null,
    string? AssignedTo = null);

public sealed record CrmActivityFilter(Guid? AccountId = null, Guid? ContactId = null, Guid? OpportunityId = null, bool OpenTasksOnly = false, bool MineOnly = false);

/// <summary>
/// Accounts (agencies and offices with their hierarchy, primes, partners, competitors), contacts and activities. Anyone who
/// sees the pipeline reads them (and capture/proposal managers, for their opportunities' accounts); business development,
/// Finance and administrators change them.
/// </summary>
public interface ICrmAccountService
{
    Task<IReadOnlyList<CrmAccountDto>> ListAccountsAsync(CrmAccountKind? kind = null, string? search = null, CancellationToken cancellationToken = default);

    Task<CrmAccountDto> GetAccountAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CrmAccountDto> CreateAccountAsync(SaveCrmAccountRequest request, CancellationToken cancellationToken = default);

    Task<CrmAccountDto> UpdateAccountAsync(Guid id, SaveCrmAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Links the account to the billing customer it became (null unlinks).</summary>
    Task<CrmAccountDto> LinkCustomerAsync(Guid id, Guid? customerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmContactDto>> ListContactsAsync(Guid? accountId = null, string? search = null, CancellationToken cancellationToken = default);

    Task<CrmContactDto> CreateContactAsync(SaveCrmContactRequest request, CancellationToken cancellationToken = default);

    Task<CrmContactDto> UpdateContactAsync(Guid id, SaveCrmContactRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmActivityDto>> ListActivitiesAsync(CrmActivityFilter filter, CancellationToken cancellationToken = default);

    Task<CrmActivityDto> LogActivityAsync(SaveCrmActivityRequest request, CancellationToken cancellationToken = default);

    Task<CrmActivityDto> SetTaskDoneAsync(Guid activityId, bool done, CancellationToken cancellationToken = default);
}
