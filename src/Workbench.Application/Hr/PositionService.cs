using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

/// <summary>The approved position list. Salary ranges only for HR, Finance and administrators.</summary>
public sealed class PositionService(IWorkbenchDbContextFactory dbFactory, HrAccess hrAccess, AuditRecorder audit, TimeProvider clock) : IPositionService
{
    public const string AuditEntityType = "Position";

    public async Task<IReadOnlyList<PositionDto>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<D.Position>().AsNoTracking().Where(p => includeClosed || p.Status != D.PositionStatus.Closed).ToListAsync(cancellationToken);
        return await MapAsync(db, rows, viewer.IsHr || viewer.IsFinance, cancellationToken);
    }

    public async Task<PositionDto> GetAsync(Guid positionId, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await LoadAsync(db, positionId, viewer.IsHr || viewer.IsFinance, cancellationToken);
    }

    public Task<PositionDto> CreateAsync(SavePositionRequest request, CancellationToken cancellationToken = default) => SaveAsync(null, request, cancellationToken);

    public Task<PositionDto> UpdateAsync(Guid positionId, SavePositionRequest request, CancellationToken cancellationToken = default) => SaveAsync(positionId, request, cancellationToken);

    private async Task<PositionDto> SaveAsync(Guid? positionId, SavePositionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(ct);
        var code = HrText.Code(request.Code, "The position");
        var title = HrText.Clean(request.Title, 200, "The title") ?? throw new ArgumentException("Give the position a title.");
        if (request.Headcount is < 1 or > 1000)
        {
            throw new ArgumentException("Headcount must be between 1 and 1,000.");
        }

        if (request.SalaryMin is < 0 || request.SalaryMax is < 0 || request.SalaryMin > request.SalaryMax || request.SalaryMax > 10_000_000)
        {
            throw new ArgumentException("Enter a salary range from low to high, up to 10,000,000.");
        }

        if (await db.Set<D.Position>().AnyAsync(p => p.Code == code && p.Id != positionId, ct))
        {
            throw new ArgumentException($"Position code {code} is already used.");
        }

        if (request.LaborCategoryId is { } lc && !await db.Set<LaborCategory>().AnyAsync(c => c.Id == lc, ct))
        {
            throw new ArgumentException("That labor category doesn't exist.");
        }

        if (request.OrgUnitId is { } ou && !await db.Set<OrgUnit>().AnyAsync(u => u.Id == ou, ct))
        {
            throw new ArgumentException("That org unit doesn't exist.");
        }

        if (request.ReportsToPositionId is { } parent)
        {
            if (parent == positionId)
            {
                throw new ArgumentException("A position can't report to itself.");
            }

            if (!await db.Set<D.Position>().AnyAsync(p => p.Id == parent, ct))
            {
                throw new ArgumentException("The position it reports to doesn't exist.");
            }
        }

        var position = positionId is { } id
            ? await db.Set<D.Position>().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Position", id)
            : null;
        var before = position is null ? null : Snapshot(position);
        if (position is not null && request.Status == PositionStatus.Closed && position.Status != D.PositionStatus.Closed)
        {
            var filled = await FilledAsync(db, [position.Id], ct);
            if (filled.GetValueOrDefault(position.Id) > 0)
            {
                throw new ArgumentException("Someone still fills this position. Move them to another position before closing it.");
            }
        }

        position ??= db.Set<D.Position>().Add(new D.Position { Code = code, Title = title }).Entity;
        position.Code = code;
        position.Title = title;
        position.LaborCategoryId = request.LaborCategoryId;
        position.OrgUnitId = request.OrgUnitId;
        position.ReportsToPositionId = request.ReportsToPositionId;
        position.EeoJobCategory = request.EeoJobCategory is { } eeo ? (D.EeoJobCategory)(int)eeo : null;
        position.IsExempt = request.IsExempt;
        position.EmploymentType = (EmploymentType)(int)request.EmploymentType;
        position.SalaryMin = request.SalaryMin;
        position.SalaryMax = request.SalaryMax;
        position.Headcount = request.Headcount;
        position.Status = (D.PositionStatus)(int)request.Status;
        position.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        position.UpdatedBy = audit.Actor;
        audit.Record(db, AuditEntityType, position.Id, before is null ? "Created" : "Updated", before, Snapshot(position));
        await db.SaveChangesAsync(ct);
        return await LoadAsync(db, position.Id, true, ct);
    }

    private static object Snapshot(D.Position p) =>
        new { p.Code, p.Title, p.LaborCategoryId, p.OrgUnitId, p.ReportsToPositionId, p.Headcount, p.SalaryMin, p.SalaryMax, Status = p.Status.ToString() };

    private async Task<PositionDto> LoadAsync(IWorkbenchDbContext db, Guid positionId, bool showSalary, CancellationToken ct)
    {
        var row = await db.Set<D.Position>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == positionId, ct) ?? throw WorkbenchNotFoundException.For("Position", positionId);
        return (await MapAsync(db, [row], showSalary, ct))[0];
    }

    /// <summary>Active (not yet terminated) employees in each position.</summary>
    internal static async Task<Dictionary<Guid, int>> FilledAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> positionIds, CancellationToken ct, DateOnly? today = null) =>
        (await IncumbentsAsync(db, positionIds, today ?? DateOnly.FromDateTime(DateTime.UtcNow), ct)).ToDictionary(g => g.Key, g => g.Value.Count);

    private static async Task<Dictionary<Guid, List<string>>> IncumbentsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> positionIds, DateOnly today, CancellationToken ct)
    {
        var rows = await db.Set<D.EmployeeHrProfile>().AsNoTracking().Where(p => p.PositionId != null && positionIds.Contains(p.PositionId!.Value))
            .Join(db.Set<Employee>(), p => p.EmployeeId, e => e.Id, (p, e) => new { PositionId = p.PositionId!.Value, e.DisplayName, e.TerminationDate })
            .ToListAsync(ct);
        return rows.Where(r => r.TerminationDate is null || r.TerminationDate >= today)
            .GroupBy(r => r.PositionId).ToDictionary(g => g.Key, g => g.Select(r => r.DisplayName).OrderBy(n => n).ToList());
    }

    private async Task<List<PositionDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<D.Position> rows, bool showSalary, CancellationToken ct)
    {
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var units = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var codes = await db.Set<D.Position>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var incumbents = await IncumbentsAsync(db, rows.Select(r => r.Id).ToList(), DateOnly.FromDateTime(clock.GetLocalNow().DateTime), ct);
        return rows.OrderBy(r => r.Code, StringComparer.Ordinal).Select(p =>
        {
            var people = incumbents.GetValueOrDefault(p.Id) ?? [];
            return new PositionDto(p.Id, p.Code, p.Title, p.LaborCategoryId, p.LaborCategoryId is { } lc ? categories.GetValueOrDefault(lc) : null,
                p.OrgUnitId, p.OrgUnitId is { } ou ? units.GetValueOrDefault(ou) : null, p.ReportsToPositionId,
                p.ReportsToPositionId is { } parent ? codes.GetValueOrDefault(parent) : null,
                p.EeoJobCategory is { } eeo ? (EeoJobCategory)(int)eeo : null, p.IsExempt, (EmploymentTypeKind)(int)p.EmploymentType,
                showSalary ? p.SalaryMin : null, showSalary ? p.SalaryMax : null, p.Headcount, people.Count, (PositionStatus)(int)p.Status, people);
        }).ToList();
    }
}
