namespace Workbench.Domain.Crm;

/// <summary>What an account is to us: the government buyer (agency and its offices) or industry (primes, partners, competitors).</summary>
public enum CrmAccountKind
{
    Agency,
    Office,
    Prime,
    Partner,
    Competitor,
    Other,
}

/// <summary>
/// An organization business development tracks. Agencies and offices form a hierarchy (Department → Agency → Office) through
/// <see cref="ParentId"/>. Once it buys from us it's linked to a <see cref="ProjectAccounting.Customer"/>.
/// </summary>
public sealed class CrmAccount : Entity
{
    public required string Name { get; set; }
    public string? Acronym { get; set; }
    public CrmAccountKind Kind { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>SAM.gov Unique Entity ID and CAGE code (industry accounts).</summary>
    public string? Uei { get; set; }
    public string? CageCode { get; set; }

    public string? Website { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }

    /// <summary>The billing customer this account became.</summary>
    public Guid? CustomerId { get; set; }

    public bool IsActive { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>The part a contact plays in a buy (or at a partner).</summary>
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

public sealed class CrmContact : Entity
{
    public Guid AccountId { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Title { get; set; }
    public ContactRole Role { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public enum CrmActivityKind
{
    Call,
    Meeting,
    Email,
    Note,
    Task,
}

/// <summary>
/// Something that happened (a call, meeting, email or note, logged by hand) or needs doing (a task with a due date and an
/// assignee, listed on their home page). Attached to an account, a contact and/or an opportunity.
/// </summary>
public sealed class CrmActivity : Entity
{
    public Guid? AccountId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? OpportunityId { get; set; }
    public CrmActivityKind Kind { get; set; }
    public required string Subject { get; set; }
    public string? Body { get; set; }

    /// <summary>When it happened (calls, meetings, emails, notes).</summary>
    public DateOnly? OccurredOn { get; set; }

    /// <summary>Tasks: when it's due and who does it (lower-case email).</summary>
    public DateOnly? DueOn { get; set; }
    public string? AssignedTo { get; set; }
    public bool IsDone { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
