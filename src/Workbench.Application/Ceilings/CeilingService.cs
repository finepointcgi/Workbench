using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Ceilings;
using Workbench.Domain.Ceilings;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Ceilings;

public sealed class CeilingService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    AccessControl access,
    ChargeGuard guard,
    TimeProvider clock) : ICeilingService
{
    internal const string CeilingEntity = "Ceiling";
    internal const string OverrideEntity = "CeilingOverride";
    internal const string RateCeilingEntity = "RateCeiling";

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<ContractCeilingStatusDto> GetContractStatusAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        var states = await CeilingEngine.ForContractAsync(db, contractId, includeInactive: true, UtcNow, cancellationToken);
        var rates = await RateCeilingsAsync(db, contractId, cancellationToken);
        var finance = await IsFinanceAsync(cancellationToken);
        return new ContractCeilingStatusDto(contract.Id, contract.ContractNumber, await StatusesAsync(db, states, finance, cancellationToken), rates, finance);
    }

    public async Task<ProjectCeilingStatusDto> GetProjectStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var states = await CeilingEngine.ForProjectAsync(db, project, UtcNow, cancellationToken);
        var finance = await IsFinanceAsync(cancellationToken);
        return new ProjectCeilingStatusDto(project.Id, project.Code, project.ContractId, await StatusesAsync(db, states, finance, cancellationToken), finance);
    }

    public async Task<CeilingStatusDto> GetAsync(Guid ceilingId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var states = await CeilingEngine.ForIdsAsync(db, [ceilingId], UtcNow, cancellationToken);
        if (states.Count == 0)
        {
            throw WorkbenchNotFoundException.For("Ceiling", ceilingId);
        }

        return (await StatusesAsync(db, states, await IsFinanceAsync(cancellationToken), cancellationToken))[0];
    }

    public async Task<CeilingDto> CreateAsync(SaveCeilingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ceiling = new Ceiling { CreatedBy = access.Email, CreatedAtUtc = UtcNow, Level = (CeilingLevel)(int)request.Scope };
        await SetTargetAsync(db, ceiling, request, cancellationToken);
        Apply(ceiling, request);
        db.Set<Ceiling>().Add(ceiling);
        audit.Record(db, CeilingEntity, ceiling.Id, "Created", after: Snapshot(ceiling));
        await db.SaveChangesAsync(cancellationToken);
        await AlertAsync(ceiling, cancellationToken);
        return await DtoAsync(db, ceiling.Id, cancellationToken);
    }

    public async Task<CeilingDto> UpdateAsync(Guid ceilingId, SaveCeilingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ceiling = await db.Set<Ceiling>().FirstOrDefaultAsync(c => c.Id == ceilingId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Ceiling", ceilingId);
        if (!ceiling.IsActive)
        {
            throw new ArgumentException("This ceiling was deactivated; add a new one instead.");
        }

        var before = Snapshot(ceiling);
        Apply(ceiling, request);
        ceiling.UpdatedBy = access.Email;
        ceiling.UpdatedAtUtc = UtcNow;
        audit.Record(db, CeilingEntity, ceiling.Id, "Updated", before, Snapshot(ceiling));
        await db.SaveChangesAsync(cancellationToken);
        await AlertAsync(ceiling, cancellationToken);
        return await DtoAsync(db, ceiling.Id, cancellationToken);
    }

    public async Task<CeilingDto> DeactivateAsync(Guid ceilingId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ceiling = await db.Set<Ceiling>().FirstOrDefaultAsync(c => c.Id == ceilingId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Ceiling", ceilingId);
        if (ceiling.IsActive)
        {
            ceiling.IsActive = false;
            ceiling.UpdatedBy = access.Email;
            ceiling.UpdatedAtUtc = UtcNow;
            audit.Record(db, CeilingEntity, ceiling.Id, "Deactivated", before: Snapshot(ceiling));
            await db.SaveChangesAsync(cancellationToken);
        }

        return await DtoAsync(db, ceiling.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<CeilingOverrideDto>> ListOverridesAsync(Guid ceilingId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Ceiling>().AnyAsync(c => c.Id == ceilingId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Ceiling", ceilingId);
        }

        var rows = await db.Set<CeilingOverride>().AsNoTracking().Where(o => o.CeilingId == ceilingId).ToListAsync(cancellationToken);
        return rows.OrderByDescending(o => o.ApprovedAtUtc).Select(ToDto).ToList();
    }

    public async Task<CeilingOverrideDto> GrantOverrideAsync(Guid ceilingId, GrantCeilingOverrideRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Required(request.Reason, "A reason for the override", 1000);
        var today = DateOnly.FromDateTime(UtcNow);
        if (request.ExpiresOn < today)
        {
            throw new ArgumentException("The override has to expire today or later.");
        }

        if (request.ExpiresOn > today.AddYears(2))
        {
            throw new ArgumentException("An override is temporary: it can last at most two years. Raise the ceiling instead.");
        }

        if (new[] { request.ExtraHours, request.ExtraCost, request.ExtraBilling }.Any(v => v is < 0))
        {
            throw new ArgumentException("Override amounts can't be negative.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ceiling = await db.Set<Ceiling>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == ceilingId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Ceiling", ceilingId);
        if (!ceiling.IsActive)
        {
            throw new ArgumentException("This ceiling was deactivated.");
        }

        var row = new CeilingOverride
        {
            CeilingId = ceilingId,
            ExtraHours = request.ExtraHours,
            ExtraCost = request.ExtraCost,
            ExtraBilling = request.ExtraBilling,
            Reason = reason,
            ExpiresAtUtc = request.ExpiresOn.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ApprovedBy = access.Email,
            ApprovedAtUtc = UtcNow,
        };
        db.Set<CeilingOverride>().Add(row);
        audit.Record(db, OverrideEntity, row.Id, "Granted",
            after: new { Ceiling = ceilingId, row.ExtraHours, row.ExtraCost, row.ExtraBilling, Expires = request.ExpiresOn }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task<CeilingOverrideDto> RevokeOverrideAsync(Guid overrideId, CeilingReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = Required(request?.Reason, "A reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<CeilingOverride>().FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Ceiling override", overrideId);
        if (row.RevokedAtUtc is not null)
        {
            throw new ArgumentException("This override was already revoked.");
        }

        row.RevokedBy = access.Email;
        row.RevokedAtUtc = UtcNow;
        row.RevokeReason = reason;
        audit.Record(db, OverrideEntity, row.Id, "Revoked", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task<IReadOnlyList<RateCeilingDto>> ListRateCeilingsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        return await RateCeilingsAsync(db, contractId, cancellationToken);
    }

    public async Task<RateCeilingDto> CreateRateCeilingAsync(Guid contractId, SaveRateCeilingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        var kind = (RateCeilingKind)(int)request.Kind;
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("Unknown rate ceiling kind.");
        }

        var isFee = kind is RateCeilingKind.FeeRate or RateCeilingKind.FeeAmount;
        if (request.ClinId is { } clinId)
        {
            if (!isFee)
            {
                throw new ArgumentException("Indirect rate caps apply to the whole contract; only fee caps can be per CLIN.");
            }

            if (!await db.Set<Clin>().AnyAsync(c => c.Id == clinId && c.ContractId == contractId, cancellationToken))
            {
                throw new ArgumentException("That CLIN isn't on this contract.");
            }
        }

        if (kind == RateCeilingKind.FeeAmount)
        {
            if (request.MaxAmount is not >= 0)
            {
                throw new ArgumentException("A fee cap needs the most fee that may be billed, in dollars.");
            }
        }
        else if (request.MaxRate is not (>= 0 and <= 5))
        {
            throw new ArgumentException("A rate cap is a fraction between 0 and 5 (0.45 = 45%).");
        }

        if (request.FiscalYear is < 2000 or > 2200)
        {
            throw new ArgumentException("That fiscal year isn't valid.");
        }

        var row = new RateCeiling
        {
            ContractId = contractId,
            ClinId = request.ClinId,
            Kind = kind,
            FiscalYear = request.FiscalYear,
            MaxRate = kind == RateCeilingKind.FeeAmount ? null : request.MaxRate,
            MaxAmount = kind == RateCeilingKind.FeeAmount ? request.MaxAmount : null,
            Notes = Optional(request.Notes, 1000),
            CreatedBy = access.Email,
            CreatedAtUtc = UtcNow,
        };
        db.Set<RateCeiling>().Add(row);
        audit.Record(db, RateCeilingEntity, row.Id, "Created", after: new { row.ContractId, row.ClinId, Kind = row.Kind.ToString(), row.FiscalYear, row.MaxRate, row.MaxAmount });
        await db.SaveChangesAsync(cancellationToken);
        return (await RateCeilingsAsync(db, contractId, cancellationToken)).First(r => r.Id == row.Id);
    }

    public async Task<RateCeilingDto> DeactivateRateCeilingAsync(Guid rateCeilingId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<RateCeiling>().FirstOrDefaultAsync(r => r.Id == rateCeilingId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Rate ceiling", rateCeilingId);
        if (row.IsActive)
        {
            row.IsActive = false;
            audit.Record(db, RateCeilingEntity, row.Id, "Deactivated");
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await RateCeilingsAsync(db, row.ContractId, cancellationToken)).First(r => r.Id == row.Id);
    }

    public async Task<int> CheckAlertsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        return await guard.AlertAsync(db, await CeilingEngine.ForContractAsync(db, contractId, false, UtcNow, cancellationToken), cancellationToken);
    }

    // ---- helpers ----

    private async Task AlertAsync(Ceiling ceiling, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        await guard.AlertAsync(db, await CeilingEngine.ForIdsAsync(db, [ceiling.Id], UtcNow, ct), ct);
    }

    private async Task<bool> IsFinanceAsync(CancellationToken ct) => (await access.GetOrgRolesAsync(ct)).IsFinance();

    private static async Task SetTargetAsync(IWorkbenchDbContext db, Ceiling c, SaveCeilingRequest r, CancellationToken ct)
    {
        switch (c.Level)
        {
            case CeilingLevel.Contract:
                var contractId = r.ContractId ?? throw new ArgumentException("Pick the contract.");
                if (!await db.Set<Contract>().AnyAsync(x => x.Id == contractId, ct))
                {
                    throw WorkbenchNotFoundException.For("Contract", contractId);
                }

                c.ContractId = contractId;
                break;
            case CeilingLevel.Clin:
                var clinId = r.ClinId ?? throw new ArgumentException("Pick the CLIN.");
                var clin = await db.Set<Clin>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == clinId, ct) ?? throw WorkbenchNotFoundException.For("CLIN", clinId);
                (c.ClinId, c.ContractId) = (clin.Id, clin.ContractId);
                break;
            case CeilingLevel.Project:
                var projectId = r.ProjectId ?? throw new ArgumentException("Pick the project.");
                var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct) ?? throw WorkbenchNotFoundException.For("Project", projectId);
                (c.ProjectId, c.ContractId) = (project.Id, project.ContractId);
                break;
            case CeilingLevel.Wbs or CeilingLevel.LaborCategory:
                var wbsId = r.WbsElementId ?? throw new ArgumentException("Pick the WBS element.");
                var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == wbsId, ct) ?? throw WorkbenchNotFoundException.For("WBS element", wbsId);
                var owner = await db.Set<Project>().AsNoTracking().FirstAsync(x => x.Id == wbs.ProjectId, ct);
                (c.WbsElementId, c.ProjectId, c.ContractId) = (wbs.Id, owner.Id, owner.ContractId);
                if (c.Level == CeilingLevel.LaborCategory)
                {
                    var categoryId = r.LaborCategoryId ?? throw new ArgumentException("Pick the labor category.");
                    if (!await db.Set<LaborCategory>().AnyAsync(x => x.Id == categoryId, ct))
                    {
                        throw WorkbenchNotFoundException.For("Labor category", categoryId);
                    }

                    c.LaborCategoryId = categoryId;
                }

                break;
            default:
                throw new ArgumentException("Unknown ceiling scope.");
        }
    }

    private static void Apply(Ceiling c, SaveCeilingRequest r)
    {
        if (r.HoursCeiling is null && r.CostCeiling is null && r.BillingCeiling is null)
        {
            throw new ArgumentException("Set a ceiling on hours, cost or billing.");
        }

        if (new[] { r.HoursCeiling, r.CostCeiling, r.BillingCeiling }.Any(v => v is <= 0))
        {
            throw new ArgumentException("Ceiling amounts must be more than zero.");
        }

        if (r.BillingCeiling is not null && c.Level is not (CeilingLevel.Contract or CeilingLevel.Clin))
        {
            throw new ArgumentException("A billing ceiling is set on a contract or a CLIN.");
        }

        if (!Enum.IsDefined((CeilingEnforcement)(int)r.Mode))
        {
            throw new ArgumentException("Unknown enforcement mode.");
        }

        var thresholds = CeilingEngine.Thresholds(string.IsNullOrWhiteSpace(r.AlertThresholds) ? "80,95,100" : r.AlertThresholds);
        c.HoursCeiling = r.HoursCeiling;
        c.CostCeiling = r.CostCeiling;
        c.BillingCeiling = r.BillingCeiling;
        c.Enforcement = (CeilingEnforcement)(int)r.Mode;
        c.AlertThresholds = string.Join(",", thresholds);
        c.ProjectManagerMayOverride = r.ProjectManagerMayOverride;
        c.CheckAtRequisition = r.CheckAtRequisition;
        c.Notes = Optional(r.Notes, 1000);
    }

    private static object Snapshot(Ceiling c) => new
    {
        Level = c.Level.ToString(), c.ContractId, c.ClinId, c.ProjectId, c.WbsElementId, c.LaborCategoryId,
        c.HoursCeiling, c.CostCeiling, c.BillingCeiling, Enforcement = c.Enforcement.ToString(), c.AlertThresholds, c.ProjectManagerMayOverride, c.CheckAtRequisition, c.IsActive,
    };

    private async Task<CeilingDto> DtoAsync(IWorkbenchDbContext db, Guid ceilingId, CancellationToken ct)
    {
        var states = await CeilingEngine.ForIdsAsync(db, [ceilingId], UtcNow, ct);
        return (await StatusesAsync(db, states, true, ct))[0].Ceiling;
    }

    private async Task<List<CeilingStatusDto>> StatusesAsync(IWorkbenchDbContext db, IReadOnlyList<CeilingState> states, bool finance, CancellationToken ct)
    {
        var contractIds = states.Select(s => s.Ceiling.ContractId).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var projectIds = states.Select(s => s.Ceiling.ProjectId).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var wbsIds = states.Select(s => s.Ceiling.WbsElementId).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var clinIds = states.Select(s => s.Ceiling.ClinId).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var categoryIds = states.Select(s => s.Ceiling.LaborCategoryId).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct);
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => clinIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var categories = await db.Set<LaborCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        // A project manager may override where the ceiling allows it and covers a project they manage.
        HashSet<Guid> managed = [];
        if (!finance && await access.GetEmployeeIdAsync(ct) is { } me)
        {
            managed = (await db.Set<Project>().AsNoTracking().Where(p => p.ProjectManagerId == me).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        }

        static string? Of(Dictionary<Guid, string> map, Guid? id) => id is { } x ? map.GetValueOrDefault(x) : null;
        return states
            .OrderBy(s => s.Ceiling.IsActive ? 0 : 1).ThenBy(s => (int)s.Ceiling.Level).ThenBy(s => s.Label, StringComparer.Ordinal)
            .Select(s =>
            {
                var c = s.Ceiling;
                var thresholds = CeilingEngine.Thresholds(c.AlertThresholds);
                var dto = new CeilingDto(c.Id, (CeilingScope)(int)c.Level, c.ContractId, Of(contracts, c.ContractId), c.ClinId, Of(clins, c.ClinId),
                    c.ProjectId, Of(projects, c.ProjectId), c.WbsElementId, Of(wbs, c.WbsElementId), c.LaborCategoryId, Of(categories, c.LaborCategoryId),
                    c.HoursCeiling, c.CostCeiling, c.BillingCeiling, (CeilingMode)(int)c.Enforcement, thresholds, c.ProjectManagerMayOverride, c.Notes, c.IsActive, s.Label,
                    c.CheckAtRequisition);
                var measures = CeilingEngine.Measures.Where(m => s.Limit(m) is not null).Select(m => Measure(s, m, thresholds)).ToList();
                var level = measures.Count == 0 ? CeilingAlertLevel.Ok : measures.Max(m => m.Level);
                var canOverride = c.IsActive && (finance || (c.ProjectManagerMayOverride && s.Projects.Overlaps(managed)));
                return new CeilingStatusDto(dto, measures, s.Overrides.Select(ToDto).ToList(), c.IsActive ? level : CeilingAlertLevel.Ok, canOverride);
            })
            .ToList();
    }

    private static CeilingMeasureDto Measure(CeilingState s, CeilingMeasure m, IReadOnlyList<int> thresholds)
    {
        var limit = s.Limit(m)!.Value;
        var consumed = s.Consumed(m);
        var effective = s.EffectiveLimit(m);
        var percent = limit == 0 ? 0 : Math.Round(consumed / limit * 100m, 1, MidpointRounding.AwayFromZero);
        var upTo100 = thresholds.Where(t => t <= 100).ToList();
        var level = consumed > limit ? CeilingAlertLevel.Over
            : upTo100.Count > 1 && percent >= upTo100[^1] ? CeilingAlertLevel.Critical
            : upTo100.Count > 0 && percent >= upTo100[0] ? CeilingAlertLevel.Warning
            : CeilingAlertLevel.Ok;
        return new CeilingMeasureDto((CeilingMetric)(int)m, limit, effective, consumed, (effective ?? limit) - consumed, percent, level);
    }

    private CeilingOverrideDto ToDto(CeilingOverride o) => new(o.Id, o.CeilingId, o.ExtraHours, o.ExtraCost, o.ExtraBilling, o.Reason, o.ExpiresAtUtc,
        o.ApprovedBy, o.ApprovedAtUtc, o.RevokedBy, o.RevokedAtUtc, o.RevokeReason, o.IsActiveAt(UtcNow));

    private static async Task<List<RateCeilingDto>> RateCeilingsAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct)
    {
        var rows = await db.Set<RateCeiling>().AsNoTracking().Where(r => r.ContractId == contractId).ToListAsync(ct);
        var clinIds = rows.Where(r => r.ClinId != null).Select(r => r.ClinId!.Value).ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => clinIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        return rows.OrderBy(r => r.IsActive ? 0 : 1).ThenBy(r => r.Kind).ThenBy(r => r.FiscalYear)
            .Select(r =>
            {
                var clin = r.ClinId is { } id ? clins.GetValueOrDefault(id) : null;
                return new RateCeilingDto(r.Id, r.ContractId, r.ClinId, clin, (RateCapKind)(int)r.Kind, r.FiscalYear, r.MaxRate, r.MaxAmount, r.Notes, r.IsActive, RateLabel(r, clin));
            })
            .ToList();
    }

    internal static string RateLabel(RateCeiling r, string? clin)
    {
        var what = r.Kind switch
        {
            RateCeilingKind.Fringe => $"Fringe capped at {r.MaxRate:P2}",
            RateCeilingKind.Overhead => $"Overhead capped at {r.MaxRate:P2}",
            RateCeilingKind.GeneralAndAdministrative => $"G&A capped at {r.MaxRate:P2}",
            RateCeilingKind.FeeRate => $"Fee rate capped at {r.MaxRate:P2}",
            _ => $"Fee capped at {r.MaxAmount:C}",
        };
        return what + (clin is null ? "" : $" on CLIN {clin}") + (r.FiscalYear is { } fy ? $" (FY{fy})" : "");
    }

    internal static string Required(string? value, string what, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            throw new ArgumentException($"{what} is required.");
        }

        return v.Length <= max ? v : throw new ArgumentException($"{what} can be at most {max} characters.");
    }

    internal static string? Optional(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : throw new ArgumentException($"Text can be at most {max} characters.");
    }
}
