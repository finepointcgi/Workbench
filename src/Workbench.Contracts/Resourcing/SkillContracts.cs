namespace Workbench.Contracts.Resourcing;

// Skills on people, so staffing can find who can do the work: "Advanced in C# and Azure, free in March".

public enum SkillLevel
{
    Learning = 1,
    Working = 2,
    Advanced = 3,
    Expert = 4,
}

public static class SkillLabels
{
    public static string Label(SkillLevel level) => level.ToString();

    public static string Describe(SkillLevel level) => level switch
    {
        SkillLevel.Learning => "Learning: has started, needs guidance",
        SkillLevel.Working => "Working: does it on their own",
        SkillLevel.Advanced => "Advanced: does the hard parts and helps others",
        _ => "Expert: the person to ask",
    };
}

/// <summary>A skill in the company list, with how many active people have it.</summary>
public sealed record SkillDto(Guid Id, string Name, string? Category, int People);

/// <summary>One skill on one person.</summary>
public sealed record EmployeeSkillDto(Guid SkillId, string Name, string? Category, SkillLevel Level, decimal? Years);

/// <summary>
/// Gives a person a skill, or changes their level in it. The skill is found by name (any capitalization) and added to the
/// company list when it's new, so there is nothing to set up first.
/// </summary>
public sealed record SetEmployeeSkillRequest(string SkillName, SkillLevel Level, decimal? Years = null, string? Category = null);

/// <summary>One person's skills, best first.</summary>
public sealed record PersonSkillsDto(Guid EmployeeId, IReadOnlyList<EmployeeSkillDto> Skills);

public sealed record RenameSkillRequest(string Name, string? Category = null);

/// <summary>Skills on people: the company list, each person's skills, and who has what.</summary>
public interface ISkillService
{
    /// <summary>The company's skills, most-held first. Anyone signed in.</summary>
    Task<IReadOnlyList<SkillDto>> ListSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>Everyone's skills in one call, for screens that show them beside names. Staffing roles see everyone; others only themselves.</summary>
    Task<IReadOnlyList<PersonSkillsDto>> ListPeopleSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>A person's skills: yourself, your reports, or anyone for staffing roles.</summary>
    Task<IReadOnlyList<EmployeeSkillDto>> GetEmployeeSkillsAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Yourself, the people you supervise, or anyone for resource managers, HR and administrators.</summary>
    Task<EmployeeSkillDto> SetEmployeeSkillAsync(Guid employeeId, SetEmployeeSkillRequest request, CancellationToken cancellationToken = default);

    Task RemoveEmployeeSkillAsync(Guid employeeId, Guid skillId, CancellationToken cancellationToken = default);

    /// <summary>Rename a skill or change its category everywhere. Resource managers, HR and administrators.</summary>
    Task<SkillDto> RenameSkillAsync(Guid skillId, RenameSkillRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes the skill off everyone and off the list.</summary>
    Task DeleteSkillAsync(Guid skillId, CancellationToken cancellationToken = default);
}
