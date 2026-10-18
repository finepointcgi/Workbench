using Microsoft.EntityFrameworkCore;
using Workbench.Application.ContractDetail;
using Workbench.Application.Financials;
using Workbench.Application.Purchasing;
using Workbench.Contracts.Ceilings;
using Workbench.Domain.Billing;
using Workbench.Domain.Ceilings;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Ceilings;

/// <summary>A proposed change in what's charged to a WBS element: hours (labor only) and cost.</summary>
internal sealed record ChargeDelta(Guid WbsElementId, Guid? LaborCategoryId, decimal Hours, decimal Cost);

/// <summary>A proposed change in what's billed on a CLIN (null for lines without one).</summary>
internal sealed record BillingDelta(Guid? ClinId, decimal Amount);

/// <summary>A ceiling with the WBS it covers, what's consumed against it and its overrides in force.</summary>
internal sealed class CeilingState
{
    public required Ceiling Ceiling { get; init; }
    public required string Label { get; init; }
    public required IReadOnlySet<Guid> Wbs { get; init; }
    public required IReadOnlySet<Guid> Clins { get; init; }
    public required IReadOnlySet<Guid> Projects { get; init; }
    public required IReadOnlyList<CeilingOverride> Overrides { get; init; }
    public decimal Hours { get; set; }
    public decimal Cost { get; set; }
    public decimal Billed { get; set; }

    public bool Covers(Guid wbsId, Guid? laborCategoryId) =>
        Wbs.Contains(wbsId) && (Ceiling.Level != CeilingLevel.LaborCategory || laborCategoryId == Ceiling.LaborCategoryId);

    public decimal? Limit(CeilingMeasure m) => m switch
    {
        CeilingMeasure.Hours => Ceiling.HoursCeiling,
        CeilingMeasure.Cost => Ceiling.CostCeiling,
        _ => Ceiling.BillingCeiling,
    };

    public decimal Consumed(CeilingMeasure m) => m switch
    {
        CeilingMeasure.Hours => Hours,
        CeilingMeasure.Cost => Cost,
        _ => Billed,
    };

    /// <summary>The ceiling plus active overrides, or null while an override without amounts lifts it.</summary>
    public decimal? EffectiveLimit(CeilingMeasure m)
    {
        if (Limit(m) is not { } limit)
        {
            return null;
        }

        if (Overrides.Any(o => o.ExtraHours is null && o.ExtraCost is null && o.ExtraBilling is null))
        {
            return null;
        }

        return limit + Overrides.Sum(o => m switch
        {
            CeilingMeasure.Hours => o.ExtraHours ?? 0,
            CeilingMeasure.Cost => o.ExtraCost ?? 0,
            _ => o.ExtraBilling ?? 0,
        });
    }
}

/// <summary>
/// Works out what each ceiling covers and what's consumed against it, and checks proposed charges and invoices.
/// Hours: every time entry on record (any timesheet status). Cost: those hours at the employee's cost rate, submitted and
/// approved allowable ODCs (AP bills become ODCs when approved) and open PO commitments. Billing: lines of approved,
/// sent and paid invoices (credit memos net).
/// </summary>
internal static class CeilingEngine
{
    public static readonly CeilingMeasure[] Measures = [CeilingMeasure.Hours, CeilingMeasure.Cost, CeilingMeasure.Billing];

    private static readonly InvoiceStatus[] Billed = [InvoiceStatus.Approved, InvoiceStatus.Sent, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid];

    /// <summary>Active ceilings that cover any of <paramref name="wbsIds"/>.</summary>
    public static async Task<List<CeilingState>> ForWbsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> wbsIds, DateTime utcNow, CancellationToken ct)
    {
        if (wbsIds.Count == 0)
        {
            return [];
        }

        var projectIds = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).Select(w => w.ProjectId).Distinct().ToListAsync(ct);
        var contractIds = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id) && p.ContractId != null)
            .Select(p => p.ContractId!.Value).Distinct().ToListAsync(ct);
        var states = await LoadAsync(db, c => (c.ContractId != null && contractIds.Contains(c.ContractId.Value)) || (c.ProjectId != null && projectIds.Contains(c.ProjectId.Value)), utcNow, ct);
        return states.Where(s => s.Wbs.Overlaps(wbsIds)).ToList();
    }

    /// <summary>Active ceilings of a contract that bill (contract and CLIN billing ceilings).</summary>
    public static async Task<List<CeilingState>> ForBillingAsync(IWorkbenchDbContext db, Guid contractId, DateTime utcNow, CancellationToken ct) =>
        (await LoadAsync(db, c => c.ContractId == contractId && c.BillingCeiling != null, utcNow, ct)).ToList();

    public static async Task<List<CeilingState>> ForContractAsync(IWorkbenchDbContext db, Guid contractId, bool includeInactive, DateTime utcNow, CancellationToken ct) =>
        await LoadAsync(db, c => c.ContractId == contractId, utcNow, ct, includeInactive);

    /// <summary>The project's own ceilings plus its contract's and CLINs' that cover any of its WBS.</summary>
    public static async Task<List<CeilingState>> ForProjectAsync(IWorkbenchDbContext db, Project project, DateTime utcNow, CancellationToken ct)
    {
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == project.Id).Select(w => w.Id).ToListAsync(ct);
        var states = await LoadAsync(db, c => c.ProjectId == project.Id || (project.ContractId != null && c.ContractId == project.ContractId), utcNow, ct);
        return states.Where(s => s.Ceiling.ProjectId == project.Id || s.Wbs.Overlaps(wbs)).ToList();
    }

    public static async Task<List<CeilingState>> ForIdsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ceilingIds, DateTime utcNow, CancellationToken ct) =>
        await LoadAsync(db, c => ceilingIds.Contains(c.Id), utcNow, ct, includeInactive: true);

    private static async Task<List<CeilingState>> LoadAsync(
        IWorkbenchDbContext db, System.Linq.Expressions.Expression<Func<Ceiling, bool>> filter, DateTime utcNow, CancellationToken ct, bool includeInactive = false)
    {
        var query = db.Set<Ceiling>().AsNoTracking().Where(filter);
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        var ceilings = await query.ToListAsync(ct);
        if (ceilings.Count == 0)
        {
            return [];
        }

        var lookups = await Lookups.LoadAsync(db, ceilings, ct);
        var ids = ceilings.Select(c => c.Id).ToList();
        var overrides = (await db.Set<CeilingOverride>().AsNoTracking().Where(o => ids.Contains(o.CeilingId) && o.RevokedAtUtc == null).ToListAsync(ct))
            .Where(o => o.IsActiveAt(utcNow)).ToList();
        var states = ceilings.Select(c => new CeilingState
        {
            Ceiling = c,
            Label = lookups.Label(c),
            Wbs = lookups.WbsOf(c),
            Clins = lookups.ClinsOf(c),
            Projects = lookups.ProjectsOf(c),
            Overrides = overrides.Where(o => o.CeilingId == c.Id).ToList(),
        }).ToList();
        await MeasureAsync(db, states, ct);
        return states;
    }

    private static async Task MeasureAsync(IWorkbenchDbContext db, List<CeilingState> states, CancellationToken ct)
    {
        var wbsIds = states.SelectMany(s => s.Wbs).Distinct().ToList();
        var hours = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where wbsIds.Contains(e.WbsElementId) && e.Hours != 0
                select new { e.WbsElementId, e.LaborCategoryId, t.EmployeeId, e.WorkDate, e.Hours })
            .ToListAsync(ct);
        var employees = hours.Select(h => h.EmployeeId).Distinct().ToList();
        var costRates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employees.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            costRates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        var labor = hours.Select(h => (h.WbsElementId, h.LaborCategoryId, h.Hours, Cost: h.Hours * (costRates.Find(h.EmployeeId, h.WorkDate) ?? 0m))).ToList();
        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => wbsIds.Contains(o.WbsElementId) && !o.IsUnallowable && (o.Status == OdcStatus.Submitted || o.Status == OdcStatus.Approved))
            .Select(o => new { o.WbsElementId, o.Amount })
            .ToListAsync(ct);
        var commitments = await ProcurementSupport.CommitmentsByWbsAsync(db, wbsIds, ct);

        var contractIds = states.Where(s => s.Ceiling.BillingCeiling != null && s.Ceiling.ContractId != null).Select(s => s.Ceiling.ContractId!.Value).Distinct().ToList();
        var billedLines = contractIds.Count == 0 ? [] : await (
                from l in db.Set<InvoiceLine>().AsNoTracking()
                join i in db.Set<Invoice>().AsNoTracking() on l.InvoiceId equals i.Id
                where contractIds.Contains(i.ContractId) && Billed.Contains(i.Status)
                select new { i.ContractId, l.ClinId, l.Amount })
            .ToListAsync(ct);

        foreach (var s in states)
        {
            var mine = labor.Where(l => s.Covers(l.WbsElementId, l.LaborCategoryId)).ToList();
            s.Hours = mine.Sum(l => l.Hours);
            s.Cost = Money.Round(mine.Sum(l => l.Cost));
            if (s.Ceiling.Level != CeilingLevel.LaborCategory)
            {
                s.Cost += odcs.Where(o => s.Wbs.Contains(o.WbsElementId)).Sum(o => o.Amount)
                    + commitments.Where(c => s.Wbs.Contains(c.WbsElementId)).Sum(c => c.Committed);
            }

            if (s.Ceiling.BillingCeiling != null)
            {
                s.Billed = billedLines.Where(l => l.ContractId == s.Ceiling.ContractId
                    && (s.Ceiling.Level == CeilingLevel.Contract || (l.ClinId is { } c && s.Clins.Contains(c)))).Sum(l => l.Amount);
            }
        }
    }

    /// <summary>
    /// Checks charges against the ceilings. Increases past a blocking ceiling (and its overrides) throw
    /// <see cref="CeilingExceededException"/>; past a warning ceiling they come back as warnings. Decreases always pass.
    /// </summary>
    public static List<string> CheckCharges(IEnumerable<CeilingState> states, IReadOnlyCollection<ChargeDelta> deltas, string what)
    {
        var warnings = new List<string>();
        foreach (var s in states.Where(s => s.Ceiling.IsActive))
        {
            var covered = deltas.Where(d => s.Covers(d.WbsElementId, d.LaborCategoryId)).ToList();
            Check(s, CeilingMeasure.Hours, covered.Sum(d => d.Hours), what, warnings);
            Check(s, CeilingMeasure.Cost, covered.Sum(d => d.Cost), what, warnings);
        }

        return warnings;
    }

    /// <summary>
    /// For something already on record being submitted: blocking ceilings it touches that are already past their limit (and
    /// overrides) refuse it; warning ceilings past their limit come back as warnings.
    /// </summary>
    public static List<string> CheckStanding(IEnumerable<CeilingState> states, IReadOnlyCollection<ChargeDelta> own, string what)
    {
        var warnings = new List<string>();
        foreach (var s in states.Where(s => s.Ceiling.IsActive))
        {
            var covered = own.Where(d => s.Covers(d.WbsElementId, d.LaborCategoryId)).ToList();
            foreach (var m in new[] { CeilingMeasure.Hours, CeilingMeasure.Cost })
            {
                if (s.Limit(m) is not { } limit || covered.Sum(d => m == CeilingMeasure.Hours ? d.Hours : d.Cost) <= 0 || s.Consumed(m) <= limit)
                {
                    continue;
                }

                var effective = s.EffectiveLimit(m);
                var message = $"{s.Label}: {Noun(m)} are at {Format(m, s.Consumed(m))}, past the ceiling of {Format(m, limit)}";
                if (s.Ceiling.Enforcement == CeilingEnforcement.Block && effective is { } e && s.Consumed(m) > e)
                {
                    throw new CeilingExceededException(message + $", so {what} can't go ahead. The ceiling blocks it: ask Finance for an override or a higher ceiling.");
                }

                warnings.Add(message + ".");
            }
        }

        return warnings;
    }

    public static List<string> CheckBilling(IEnumerable<CeilingState> states, IReadOnlyCollection<BillingDelta> deltas, string what)
    {
        var warnings = new List<string>();
        foreach (var s in states.Where(s => s.Ceiling.IsActive && s.Ceiling.BillingCeiling != null))
        {
            var amount = deltas.Where(d => s.Ceiling.Level == CeilingLevel.Contract || (d.ClinId is { } c && s.Clins.Contains(c))).Sum(d => d.Amount);
            Check(s, CeilingMeasure.Billing, amount, what, warnings);
        }

        return warnings;
    }

    private static void Check(CeilingState s, CeilingMeasure m, decimal delta, string what, List<string> warnings)
    {
        if (delta <= 0 || s.Limit(m) is not { } limit)
        {
            return;
        }

        var projected = s.Consumed(m) + delta;
        if (projected <= limit)
        {
            return;
        }

        var effective = s.EffectiveLimit(m);
        var message = $"{s.Label}: {what} would bring {Noun(m)} to {Format(m, projected)}, past the ceiling of {Format(m, limit)}";
        if (effective is null || projected <= effective)
        {
            warnings.Add(message + " (an override is in force).");
            return;
        }

        if (s.Ceiling.Enforcement == CeilingEnforcement.Block)
        {
            throw new CeilingExceededException(message + (effective != limit ? $" and its override ({Format(m, effective.Value)})" : "")
                + ". The ceiling blocks it: ask Finance for an override or a higher ceiling.");
        }

        warnings.Add(message + ".");
    }

    public static string Noun(CeilingMeasure m) => m switch
    {
        CeilingMeasure.Hours => "hours",
        CeilingMeasure.Cost => "cost",
        _ => "billed to date",
    };

    public static string Format(CeilingMeasure m, decimal value) => m == CeilingMeasure.Hours ? $"{value:#,0.##} h" : value.ToString("C");

    public static IReadOnlyList<int> Thresholds(string? text)
    {
        var parsed = (text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t.TrimEnd('%'), out var v) ? v : -1)
            .ToList();
        if (parsed.Any(v => v is < 1 or > 1000))
        {
            throw new ArgumentException("Alert thresholds are whole percents between 1 and 1000, separated by commas (for example 80,95,100).");
        }

        return parsed.Distinct().Order().ToList();
    }

    /// <summary>What a ceiling covers, from the contract's or project's WBS and CLINs.</summary>
    private sealed class Lookups
    {
        private Dictionary<Guid, WbsElement> _wbs = [];
        private Dictionary<Guid, Project> _projects = [];
        private Dictionary<Guid, Clin> _clins = [];
        private Dictionary<Guid, string> _contracts = [];
        private Dictionary<Guid, string> _categories = [];

        public static async Task<Lookups> LoadAsync(IWorkbenchDbContext db, IReadOnlyList<Ceiling> ceilings, CancellationToken ct)
        {
            var contractIds = ceilings.Where(c => c.ContractId != null).Select(c => c.ContractId!.Value).Distinct().ToList();
            var projectIds = ceilings.Where(c => c.ProjectId != null).Select(c => c.ProjectId!.Value).Distinct().ToList();
            var projects = await db.Set<Project>().AsNoTracking()
                .Where(p => projectIds.Contains(p.Id) || (p.ContractId != null && contractIds.Contains(p.ContractId.Value))).ToDictionaryAsync(p => p.Id, ct);
            var allProjectIds = projects.Keys.ToList();
            var categoryIds = ceilings.Where(c => c.LaborCategoryId != null).Select(c => c.LaborCategoryId!.Value).Distinct().ToList();
            return new Lookups
            {
                _projects = projects,
                _wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => allProjectIds.Contains(w.ProjectId)).ToDictionaryAsync(w => w.Id, ct),
                _clins = await db.Set<Clin>().AsNoTracking().Where(c => contractIds.Contains(c.ContractId)).ToDictionaryAsync(c => c.Id, ct),
                _contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct),
                _categories = await db.Set<LaborCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct),
            };
        }

        public IReadOnlySet<Guid> ClinsOf(Ceiling c) => c.ClinId is { } id && _clins.TryGetValue(id, out var clin)
            ? _clins.Values.Where(x => x.ContractId == clin.ContractId && (x.Id == id || ClinRules.ParentOf(x.Number) == clin.Number)).Select(x => x.Id).ToHashSet()
            : new HashSet<Guid>();

        public IReadOnlySet<Guid> ProjectsOf(Ceiling c) => c.Level switch
        {
            CeilingLevel.Contract or CeilingLevel.Clin => _projects.Values.Where(p => p.ContractId == c.ContractId).Select(p => p.Id).ToHashSet(),
            _ => c.ProjectId is { } p ? new HashSet<Guid> { p } : [],
        };

        public IReadOnlySet<Guid> WbsOf(Ceiling c)
        {
            switch (c.Level)
            {
                case CeilingLevel.Contract:
                    return _wbs.Values.Where(w => _projects.TryGetValue(w.ProjectId, out var p) && p.ContractId == c.ContractId).Select(w => w.Id).ToHashSet();
                case CeilingLevel.Clin:
                    var clins = ClinsOf(c);
                    return _wbs.Values.Where(w => w.ClinId is { } x && clins.Contains(x)).Select(w => w.Id).ToHashSet();
                case CeilingLevel.Project:
                    return _wbs.Values.Where(w => w.ProjectId == c.ProjectId).Select(w => w.Id).ToHashSet();
                default:
                    var result = new HashSet<Guid>();
                    if (c.WbsElementId is { } root)
                    {
                        var queue = new Queue<Guid>([root]);
                        while (queue.TryDequeue(out var id))
                        {
                            if (result.Add(id))
                            {
                                foreach (var child in _wbs.Values.Where(w => w.ParentId == id))
                                {
                                    queue.Enqueue(child.Id);
                                }
                            }
                        }
                    }

                    return result;
            }
        }

        public string Label(Ceiling c) => c.Level switch
        {
            CeilingLevel.Contract => $"Contract {(c.ContractId is { } id ? _contracts.GetValueOrDefault(id, "?") : "?")}",
            CeilingLevel.Clin => $"CLIN {(c.ClinId is { } id ? _clins.GetValueOrDefault(id)?.Number ?? "?" : "?")}",
            CeilingLevel.Project => $"Project {(c.ProjectId is { } id ? _projects.GetValueOrDefault(id)?.Code ?? "?" : "?")}",
            CeilingLevel.Wbs => Wbs(c.WbsElementId),
            _ => $"{Wbs(c.WbsElementId)} · {(c.LaborCategoryId is { } l ? _categories.GetValueOrDefault(l, "?") : "?")} labor",
        };

        private string WbsCode(Guid? id) => id is { } x ? _wbs.GetValueOrDefault(x)?.Code ?? "?" : "?";

        /// <summary>The name people know the work by, then its code: "Requirements review (WBS 1001.01.02)".</summary>
        private string Wbs(Guid? id) => id is { } x && _wbs.GetValueOrDefault(x) is { } w && !string.IsNullOrWhiteSpace(w.Name)
            ? $"{w.Name} (WBS {w.Code})"
            : $"WBS {WbsCode(id)}";
    }
}
