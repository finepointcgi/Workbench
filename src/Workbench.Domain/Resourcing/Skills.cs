namespace Workbench.Domain.Resourcing;

/// <summary>How well someone does a skill. Searching "at least Working" also finds Advanced and Expert people.</summary>
public enum SkillLevel
{
    /// <summary>Has started on it; needs guidance.</summary>
    Learning = 1,

    /// <summary>Does it on their own.</summary>
    Working = 2,

    /// <summary>Does the hard parts and helps others.</summary>
    Advanced = 3,

    /// <summary>The person to ask.</summary>
    Expert = 4,
}

/// <summary>A skill the company staffs by ("C#", "Cost accounting", "DCAA audit support"). Added by typing it on a person.</summary>
public sealed class Skill : Entity
{
    public required string Name { get; set; }

    /// <summary>Optional grouping for the picker ("Engineering", "Finance").</summary>
    public string? Category { get; set; }
}

/// <summary>One person's level in one skill. A person has each skill at most once.</summary>
public sealed class EmployeeSkill : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid SkillId { get; set; }
    public SkillLevel Level { get; set; } = SkillLevel.Working;
    public decimal? Years { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public required string UpdatedBy { get; set; }
}
