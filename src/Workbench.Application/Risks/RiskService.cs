using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Risks;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Risks;

namespace Workbench.Application.Risks;

public sealed class RiskService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IRiskService
{
    /// <summary>A live risk at or above this probability (percent) counts as "high" in the summary.</summary>
    public const int HighProbability = 50;

    public async Task<RiskRegisterDto> GetRegisterAsync(Guid projectId, bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw new WorkbenchNotFoundException("Project not found.");
        }

        var rows = await db.Set<ProjectRisk>().AsNoTracking().Where(r => r.ProjectId == projectId).ToListAsync(cancellationToken);
        var today = Today();
        var all = rows.Select(r => ToDto(r, today)).ToList();
        var reserve = await db.Set<EvmBaseline>().AsNoTracking().Where(b => b.ProjectId == projectId).Select(b => (decimal?)b.ManagementReserve).FirstOrDefaultAsync(cancellationToken);

        var live = all.Where(r => r.IsLive).ToList();
        var expected = live.Sum(r => r.ExpectedCost);
        var summary = new RiskSummaryDto(
            live.Count,
            live.Count(r => r.ProbabilityPercent >= HighProbability),
            live.Count(r => r.ReviewOverdue),
            expected,
            live.Sum(r => r.CostImpact),
            live.Sum(r => r.ExpectedDays),
            reserve,
            reserve is { } mr ? mr - expected : null);

        var shown = (includeClosed ? all : all.Where(r => r.Status != RiskStatusDto.Closed))
            .OrderBy(r => r.IsLive ? 0 : 1).ThenByDescending(r => r.ExpectedCost).ThenBy(r => r.Number).ToList();
        return new RiskRegisterDto(projectId, summary, shown);
    }

    public async Task<RiskDto> GetAsync(Guid riskId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return ToDto(await LoadAsync(db, riskId, cancellationToken), Today());
    }

    public async Task<RiskDto> CreateAsync(Guid projectId, SaveRiskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw new WorkbenchNotFoundException("Project not found.");
        }

        var next = (await db.Set<ProjectRisk>().Where(r => r.ProjectId == projectId).MaxAsync(r => (int?)r.Number, cancellationToken) ?? 0) + 1;
        var risk = new ProjectRisk { ProjectId = projectId, Number = next, CreatedBy = audit.Actor, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        Apply(risk, request);
        db.Set<ProjectRisk>().Add(risk);
        audit.Record(db, nameof(ProjectRisk), risk.Id, "Created", null, new { risk.Number, risk.Title, risk.ProbabilityPercent, risk.CostImpact });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(risk, Today());
    }

    public async Task<RiskDto> UpdateAsync(Guid riskId, SaveRiskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var risk = await LoadAsync(db, riskId, cancellationToken);
        var before = new { risk.Title, risk.Status, risk.ProbabilityPercent, risk.CostImpact, risk.ScheduleImpactDays };
        Apply(risk, request);
        risk.UpdatedBy = audit.Actor;
        risk.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(ProjectRisk), risk.Id, "Updated", before, new { risk.Title, risk.Status, risk.ProbabilityPercent, risk.CostImpact, risk.ScheduleImpactDays });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(risk, Today());
    }

    public async Task DeleteAsync(Guid riskId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var risk = await LoadAsync(db, riskId, cancellationToken);
        audit.Record(db, nameof(ProjectRisk), risk.Id, "Deleted", new { risk.Number, risk.Title }, null);
        db.Set<ProjectRisk>().Remove(risk);
        await db.SaveChangesAsync(cancellationToken);
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static async Task<ProjectRisk> LoadAsync(IWorkbenchDbContext db, Guid riskId, CancellationToken ct) =>
        await db.Set<ProjectRisk>().FirstOrDefaultAsync(r => r.Id == riskId, ct) ?? throw new WorkbenchNotFoundException("Risk not found.");

    private static void Apply(ProjectRisk risk, SaveRiskRequest r)
    {
        var title = (r.Title ?? "").Trim();
        if (title.Length == 0)
        {
            throw new ArgumentException("Give the risk a title.");
        }

        if (title.Length > 200)
        {
            throw new ArgumentException("The title is too long (200 characters at most).");
        }

        if (r.ProbabilityPercent is < 0 or > 100)
        {
            throw new ArgumentException("Probability is a percentage from 0 to 100.");
        }

        if (r.CostImpact < 0)
        {
            throw new ArgumentException("Cost impact can't be negative.");
        }

        if (r.ScheduleImpactDays < 0)
        {
            throw new ArgumentException("Schedule impact can't be negative.");
        }

        risk.Title = title;
        risk.Description = Clean(r.Description, 2000);
        risk.Category = (RiskCategory)(int)r.Category;
        risk.ProbabilityPercent = r.ProbabilityPercent;
        risk.CostImpact = decimal.Round(r.CostImpact, 2);
        risk.ScheduleImpactDays = r.ScheduleImpactDays;
        risk.Status = (RiskStatus)(int)r.Status;
        risk.Response = (RiskResponse)(int)r.Response;
        risk.ResponsePlan = Clean(r.ResponsePlan, 2000);
        risk.OwnerEmail = Clean(r.OwnerEmail, 320)?.ToLowerInvariant();
        risk.ReviewOn = r.ReviewOn;
        risk.PartRefs = Clean(r.PartRefs, 500);
        risk.RequirementRefs = Clean(r.RequirementRefs, 500);
    }

    private static string? Clean(string? value, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        return v.Length <= max ? v : v[..max];
    }

    private static RiskDto ToDto(ProjectRisk r, DateOnly today)
    {
        var live = r.Status is RiskStatus.Open or RiskStatus.Mitigating or RiskStatus.Accepted;
        return new RiskDto(
            r.Id, r.ProjectId, r.Number, r.Title, r.Description, (RiskCategoryDto)(int)r.Category, r.ProbabilityPercent, r.CostImpact, r.ScheduleImpactDays,
            decimal.Round(r.CostImpact * r.ProbabilityPercent / 100m, 2), decimal.Round(r.ScheduleImpactDays * r.ProbabilityPercent / 100m, 1),
            (RiskStatusDto)(int)r.Status, (RiskResponseDto)(int)r.Response, r.ResponsePlan, r.OwnerEmail, r.ReviewOn,
            live && r.ReviewOn is { } due && due < today, r.PartRefs, r.RequirementRefs, r.CreatedAtUtc, r.UpdatedAtUtc);
    }
}
