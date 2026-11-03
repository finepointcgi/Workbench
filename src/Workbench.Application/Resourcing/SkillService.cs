using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>
/// Skills on people. A skill is added to the company list by typing it on a person, so nothing needs setting up first.
/// Everyone keeps their own skills; resource managers, HR and administrators keep anyone's and tidy the list.
/// </summary>
public sealed class SkillService(IWorkbenchDbContextFactory dbFactory, ResourcingAccess rules, AuditRecorder audit, TimeProvider clock) : ISkillService
{
    private const string SkillAudit = "Skill";
    private const string EmployeeSkillAudit = "EmployeeSkill";

    public const int MaxNameLength = 80;
    public const decimal MaxYears = 60m;

    public async Task<IReadOnlyList<SkillDto>> ListSkillsAsync(CancellationToken cancellationToken = default)
    {
        rules.Access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var skills = await db.Set<D.Skill>().AsNoTracking().ToListAsync(cancellationToken);
        var counts = await ActiveCountsAsync(db, cancellationToken);
        return skills
            .Select(s => new SkillDto(s.Id, s.Name, s.Category, counts.GetValueOrDefault(s.Id)))
            .OrderByDescending(s => s.People).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<EmployeeSkillDto>> GetEmployeeSkillsAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await RequireSeeAsync(employeeId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<D.EmployeeSkill>().AsNoTracking().Where(s => s.EmployeeId == employeeId).ToListAsync(cancellationToken);
        var skills = await db.Set<D.Skill>().AsNoTracking().ToDictionaryAsync(s => s.Id, cancellationToken);
        return rows.Where(r => skills.ContainsKey(r.SkillId))
            .Select(r => ToDto(r, skills[r.SkillId]))
            .OrderByDescending(s => s.Level).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<EmployeeSkillDto> SetEmployeeSkillAsync(Guid employeeId, SetEmployeeSkillRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireEditAsync(employeeId, cancellationToken);
        var name = CleanName(request.SkillName);
        if (!Enum.IsDefined(request.Level))
        {
            throw new ArgumentException("Pick Learning, Working, Advanced or Expert.");
        }

        if (request.Years is < 0 or > MaxYears)
        {
            throw new ArgumentException($"Years of experience must be between 0 and {MaxYears:0}.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Employee>().AnyAsync(e => e.Id == employeeId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Employee", employeeId);
        }

        var skill = await FindByNameAsync(db, name, cancellationToken);
        if (skill is null)
        {
            skill = new D.Skill { Name = name, Category = CleanCategory(request.Category) };
            db.Set<D.Skill>().Add(skill);
            audit.Record(db, SkillAudit, skill.Id, "Added", null, new { skill.Name, skill.Category });
        }

        var row = await db.Set<D.EmployeeSkill>().FirstOrDefaultAsync(s => s.EmployeeId == employeeId && s.SkillId == skill.Id, cancellationToken);
        var before = row is null ? null : new { Level = row.Level.ToString(), row.Years };
        if (row is null)
        {
            row = new D.EmployeeSkill { EmployeeId = employeeId, SkillId = skill.Id, UpdatedBy = audit.Actor };
            db.Set<D.EmployeeSkill>().Add(row);
        }

        row.Level = (D.SkillLevel)(int)request.Level;
        row.Years = request.Years;
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.UpdatedBy = audit.Actor;
        audit.Record(db, EmployeeSkillAudit, row.Id, before is null ? "Added" : "Updated", before, new { employeeId, skill = skill.Name, Level = row.Level.ToString(), row.Years });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row, skill);
    }

    public async Task RemoveEmployeeSkillAsync(Guid employeeId, Guid skillId, CancellationToken cancellationToken = default)
    {
        await RequireEditAsync(employeeId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<D.EmployeeSkill>().FirstOrDefaultAsync(s => s.EmployeeId == employeeId && s.SkillId == skillId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Skill", skillId);
        db.Set<D.EmployeeSkill>().Remove(row);
        audit.Record(db, EmployeeSkillAudit, row.Id, "Removed", new { employeeId, skillId, Level = row.Level.ToString() });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SkillDto> RenameSkillAsync(Guid skillId, RenameSkillRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireManageListAsync(cancellationToken);
        var name = CleanName(request.Name);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var skill = await db.Set<D.Skill>().FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Skill", skillId);
        var clash = await FindByNameAsync(db, name, cancellationToken);
        if (clash is not null && clash.Id != skillId)
        {
            throw new InvalidOperationException($"There is already a skill called \"{clash.Name}\". Delete one of them to combine them, after giving its people the other.");
        }

        var before = new { skill.Name, skill.Category };
        skill.Name = name;
        skill.Category = CleanCategory(request.Category);
        audit.Record(db, SkillAudit, skill.Id, "Updated", before, new { skill.Name, skill.Category });
        await db.SaveChangesAsync(cancellationToken);
        var counts = await ActiveCountsAsync(db, cancellationToken);
        return new SkillDto(skill.Id, skill.Name, skill.Category, counts.GetValueOrDefault(skill.Id));
    }

    public async Task DeleteSkillAsync(Guid skillId, CancellationToken cancellationToken = default)
    {
        await RequireManageListAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var skill = await db.Set<D.Skill>().FirstOrDefaultAsync(s => s.Id == skillId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Skill", skillId);
        var holders = await db.Set<D.EmployeeSkill>().CountAsync(s => s.SkillId == skillId, cancellationToken);
        db.Set<D.Skill>().Remove(skill);
        audit.Record(db, SkillAudit, skill.Id, "Removed", new { skill.Name, People = holders });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static EmployeeSkillDto ToDto(D.EmployeeSkill row, D.Skill skill) =>
        new(skill.Id, skill.Name, skill.Category, (SkillLevel)(int)row.Level, row.Years);

    /// <summary>Case-insensitive on every provider (SQLite and PostgreSQL compare text differently).</summary>
    private static async Task<D.Skill?> FindByNameAsync(Workbench.Application.IWorkbenchDbContext db, string name, CancellationToken ct)
    {
        var all = await db.Set<D.Skill>().ToListAsync(ct);
        return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How many current employees hold each skill.</summary>
    private static async Task<Dictionary<Guid, int>> ActiveCountsAsync(Workbench.Application.IWorkbenchDbContext db, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var active = await db.Set<Employee>().AsNoTracking().Where(e => e.HireDate <= today && (e.TerminationDate == null || e.TerminationDate >= today)).Select(e => e.Id).ToListAsync(ct);
        var rows = await db.Set<D.EmployeeSkill>().AsNoTracking().Select(s => new { s.EmployeeId, s.SkillId }).ToListAsync(ct);
        var activeSet = active.ToHashSet();
        return rows.Where(r => activeSet.Contains(r.EmployeeId)).GroupBy(r => r.SkillId).ToDictionary(g => g.Key, g => g.Count());
    }

    private static string CleanName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
        {
            throw new ArgumentException("Type the skill's name.");
        }

        if (clean.Length > MaxNameLength)
        {
            throw new ArgumentException($"A skill's name can be at most {MaxNameLength} characters.");
        }

        return clean;
    }

    private static string? CleanCategory(string? category)
    {
        var clean = category?.Trim();
        return string.IsNullOrEmpty(clean) ? null : clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
    }

    /// <summary>Resource managers, HR and administrators.</summary>
    private async Task<bool> ManagesAsync(CancellationToken ct) =>
        (await rules.RolesAsync(ct)).HasAny(Permission.ResourcingManage, Permission.HrRecordsManage);

    private async Task RequireManageListAsync(CancellationToken ct)
    {
        rules.Access.RequireSignedIn();
        if (!await ManagesAsync(ct))
        {
            throw new WorkbenchForbiddenException("Only resource managers, HR and administrators can rename or remove skills.");
        }
    }

    private async Task RequireEditAsync(Guid employeeId, CancellationToken ct)
    {
        rules.Access.RequireSignedIn();
        if (await rules.MeAsync(ct) != employeeId && !await ManagesAsync(ct))
        {
            throw new WorkbenchForbiddenException("You can change your own skills. Resource managers and HR can change anyone's.");
        }
    }

    /// <summary>Yourself, staffing roles (who see everyone in "find people"), HR and administrators.</summary>
    private async Task RequireSeeAsync(Guid employeeId, CancellationToken ct)
    {
        rules.Access.RequireSignedIn();
        if (await rules.MeAsync(ct) != employeeId && !await rules.SeesEveryoneAsync(ct) && !await ManagesAsync(ct))
        {
            throw new WorkbenchForbiddenException("You can see your own skills. Project and resource managers can see everyone's.");
        }
    }
}
