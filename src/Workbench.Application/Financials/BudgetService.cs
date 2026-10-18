using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Financials;

public sealed class BudgetService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IBudgetService
{
    public const string PlannedHoursColumn = "Planned hours";
    public const string BudgetColumn = "Budget";

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        var versions = await db.Set<BudgetVersion>().AsNoTracking().Include(v => v.Lines).Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken);
        return versions
            .OrderBy(v => v.Kind)
            .ThenByDescending(v => v.CreatedAtUtc)
            .Select(ToDto)
            .ToList();
    }

    public async Task<BudgetVersionDetailDto?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var version = await db.Set<BudgetVersion>().AsNoTracking().Include(v => v.Lines).FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        return version is null ? null : await ToDetailAsync(db, version, cancellationToken);
    }

    public async Task<BudgetVersionDetailDto> CreateVersionAsync(Guid projectId, CreateBudgetVersionRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Required("Name");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var kind = (BudgetKind)(int)request.Kind;
        if (kind != BudgetKind.Forecast && await db.Set<BudgetVersion>().AnyAsync(v => v.ProjectId == projectId && v.Kind == kind, cancellationToken))
        {
            throw new ArgumentException(kind == BudgetKind.Baseline
                ? "This project already has a Baseline."
                : "This project already has a Current budget; promote a Forecast to replace it.");
        }

        var version = new BudgetVersion
        {
            ProjectId = projectId,
            Name = name,
            Kind = kind,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            CreatedBy = audit.Actor,
        };

        switch (request.Source)
        {
            case BudgetVersionSource.CopyVersion:
                var sourceId = request.SourceVersionId ?? throw new ArgumentException("Choose a version to copy.");
                var source = await db.Set<BudgetVersion>().AsNoTracking().Include(v => v.Lines).FirstOrDefaultAsync(v => v.Id == sourceId, cancellationToken)
                    ?? throw WorkbenchNotFoundException.For("Budget version", sourceId);
                if (source.ProjectId != projectId)
                {
                    throw new ArgumentException("A version can only be copied within its project.");
                }

                version.SourceVersionId = source.Id;
                version.Lines.AddRange(source.Lines.Select(l => new BudgetLine
                {
                    WbsElementId = l.WbsElementId,
                    CostType = l.CostType,
                    LaborCategoryId = l.LaborCategoryId,
                    OdcType = l.OdcType,
                    PeriodStart = l.PeriodStart,
                    Hours = l.Hours,
                    PlanningRate = l.PlanningRate,
                    Amount = l.Amount,
                }));
                break;
            case BudgetVersionSource.PlanSheet:
                version.Lines.AddRange(await LinesFromPlanSheetAsync(db, project, cancellationToken));
                break;
        }

        foreach (var line in version.Lines)
        {
            line.BudgetVersionId = version.Id;
        }

        db.Set<BudgetVersion>().Add(version);
        var detail = await ToDetailAsync(db, version, cancellationToken);
        audit.Record(db, nameof(BudgetVersion), version.Id, "Created", after: new { detail.Version, Source = request.Source.ToString(), Lines = Snapshot(version.Lines) });
        await db.SaveChangesAsync(cancellationToken);
        return detail;
    }

    public async Task<BudgetVersionDetailDto> SaveLinesAsync(Guid versionId, IReadOnlyList<SaveBudgetLineRequest> lines, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var version = await LoadEditableAsync(db, versionId, cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == version.ProjectId).ToListAsync(cancellationToken);
        var wbsById = wbs.ToDictionary(w => w.Id);
        var parents = wbs.Where(w => w.ParentId is not null).Select(w => w.ParentId!.Value).ToHashSet();
        var categories = await db.Set<LaborCategory>().AsNoTracking().Select(c => c.Id).ToListAsync(cancellationToken);
        var periodStarts = (await db.Set<FiscalPeriod>().AsNoTracking().Select(p => p.Start).ToListAsync(cancellationToken)).ToHashSet();
        var rates = await PlanningRatesAsync(db, version.ProjectId, cancellationToken);

        var merged = new Dictionary<LineKey, (decimal Hours, decimal Amount)>();
        foreach (var line in lines)
        {
            if (!wbsById.ContainsKey(line.WbsElementId))
            {
                throw new ArgumentException("Every budget line must be on a WBS element of this project.");
            }

            if (line.Hours < 0 || line.Amount < 0)
            {
                throw new ArgumentException("Budget hours and amounts can't be negative.");
            }

            if (line.PeriodStart is { } start && !periodStarts.Contains(start))
            {
                throw new ArgumentException($"{start:yyyy-MM-dd} isn't the first day of a fiscal period.");
            }

            LineKey key;
            if (line.CostType == BudgetCostKind.Labor)
            {
                if (parents.Contains(line.WbsElementId))
                {
                    throw new ArgumentException($"Labor budget goes on WBS leaves; {wbsById[line.WbsElementId].Code} has children.");
                }

                if (line.LaborCategoryId is { } cat && !categories.Contains(cat))
                {
                    throw WorkbenchNotFoundException.For("Labor category", cat);
                }

                key = new LineKey(line.WbsElementId, BudgetCostType.Labor, line.LaborCategoryId, null, line.PeriodStart);
            }
            else
            {
                var odcType = line.OdcType ?? throw new ArgumentException("ODC budget lines need an ODC type.");
                key = new LineKey(line.WbsElementId, BudgetCostType.Odc, null, (OdcType)(int)odcType, line.PeriodStart);
            }

            if (line.Hours == 0 && line.Amount == 0)
            {
                continue;
            }

            var existing = merged.GetValueOrDefault(key);
            merged[key] = (existing.Hours + (key.CostType == BudgetCostType.Labor ? line.Hours : 0), existing.Amount + line.Amount);
        }

        var before = Snapshot(version.Lines);
        var previousRates = version.Lines
            .Where(l => l.CostType == BudgetCostType.Labor && l.PlanningRate > 0)
            .GroupBy(KeyOf)
            .ToDictionary(g => g.Key, g => g.First().PlanningRate);
        db.Set<BudgetLine>().RemoveRange(version.Lines);
        version.Lines.Clear();
        foreach (var (key, value) in merged)
        {
            var line = new BudgetLine
            {
                BudgetVersionId = version.Id,
                WbsElementId = key.WbsElementId,
                CostType = key.CostType,
                LaborCategoryId = key.LaborCategoryId,
                OdcType = key.OdcType,
                PeriodStart = key.PeriodStart,
                Hours = value.Hours,
                Amount = value.Amount,
            };
            if (key.CostType == BudgetCostType.Labor)
            {
                line.PlanningRate = previousRates.TryGetValue(key, out var rate) ? rate : rates.RateFor(key.WbsElementId, key.LaborCategoryId, key.PeriodStart ?? Today);
                line.Amount = Money.Round(line.Hours * line.PlanningRate);
            }

            version.Lines.Add(line);
            db.Set<BudgetLine>().Add(line);
        }

        audit.Record(db, nameof(BudgetVersion), version.Id, "LinesSaved", before, Snapshot(version.Lines));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(db, version, cancellationToken);
    }

    public async Task<BudgetVersionDto> LockBaselineAsync(Guid versionId, string? reason = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var version = await LoadEditableAsync(db, versionId, cancellationToken);
        if (version.Kind != BudgetKind.Baseline)
        {
            throw new ArgumentException("Only a Baseline can be locked.");
        }

        var before = ToDto(version);
        version.IsLocked = true;
        version.LockedAtUtc = clock.GetUtcNow().UtcDateTime;
        version.LockedBy = audit.Actor;
        var after = ToDto(version);
        audit.Record(db, nameof(BudgetVersion), version.Id, "Locked", before, after, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        await db.SaveChangesAsync(cancellationToken);
        return after;
    }

    public async Task<BudgetVersionDto> PromoteToCurrentAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var version = await db.Set<BudgetVersion>().Include(v => v.Lines).FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Budget version", versionId);
        if (version.Kind != BudgetKind.Forecast)
        {
            throw new ArgumentException("Only a Forecast can be promoted to Current.");
        }

        var current = await db.Set<BudgetVersion>().Include(v => v.Lines)
            .FirstOrDefaultAsync(v => v.ProjectId == version.ProjectId && v.Kind == BudgetKind.Current, cancellationToken);
        if (current is not null)
        {
            var currentBefore = ToDto(current);
            current.Kind = BudgetKind.Forecast;
            audit.Record(db, nameof(BudgetVersion), current.Id, "Demoted", currentBefore, ToDto(current), $"Replaced by '{version.Name}'.");
        }

        var before = ToDto(version);
        version.Kind = BudgetKind.Current;
        var after = ToDto(version);
        audit.Record(db, nameof(BudgetVersion), version.Id, "Promoted", before, after);
        await db.SaveChangesAsync(cancellationToken);
        return after;
    }

    public async Task<BudgetVersionDetailDto> RepriceAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var version = await LoadEditableAsync(db, versionId, cancellationToken);
        var rates = await PlanningRatesAsync(db, version.ProjectId, cancellationToken);
        var before = Snapshot(version.Lines);
        foreach (var line in version.Lines.Where(l => l.CostType == BudgetCostType.Labor))
        {
            line.PlanningRate = rates.RateFor(line.WbsElementId, line.LaborCategoryId, line.PeriodStart ?? Today);
            line.Amount = Money.Round(line.Hours * line.PlanningRate);
        }

        audit.Record(db, nameof(BudgetVersion), version.Id, "Repriced", before, Snapshot(version.Lines));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(db, version, cancellationToken);
    }

    /// <summary>
    /// One labor line per WBS leaf linked to a plan item: hours from "Planned hours" and dollars from "Budget" summed
    /// over the item's subtasks, in the labor category of the item's owner. With no budget, the hours are priced at the
    /// planning rate; budget without hours becomes an "Other" ODC line.
    /// </summary>
    private async Task<List<BudgetLine>> LinesFromPlanSheetAsync(IWorkbenchDbContext db, Project project, CancellationToken ct)
    {
        var sheetId = project.SheetId ?? throw new ArgumentException("This project has no plan sheet.");
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(ct);
        var hoursColumn = columns.FirstOrDefault(c => c.Type == ColumnType.Number && string.Equals(c.Name, PlannedHoursColumn, StringComparison.OrdinalIgnoreCase));
        var budgetColumn = columns.FirstOrDefault(c => c.Type == ColumnType.Currency && string.Equals(c.Name, BudgetColumn, StringComparison.OrdinalIgnoreCase));
        if (hoursColumn is null && budgetColumn is null)
        {
            throw new ArgumentException($"The plan sheet has no \"{PlannedHoursColumn}\" Number column or \"{BudgetColumn}\" Currency column.");
        }

        var ownerColumn = columns.Where(c => c.Type == ColumnType.Person).OrderByDescending(c => c.Name == "Owner").ThenBy(c => c.Order).FirstOrDefault();
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(ct);
        var rows = items.Select(i => new PlanSheetRow(i.Id, i.ParentId, Number(i, hoursColumn), Number(i, budgetColumn))).ToList();

        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == project.Id).ToListAsync(ct);
        var parents = wbs.Where(w => w.ParentId is not null).Select(w => w.ParentId!.Value).ToHashSet();
        var categoryByEmail = (await db.Set<Employee>().AsNoTracking().ToListAsync(ct))
            .GroupBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DefaultLaborCategoryId, StringComparer.OrdinalIgnoreCase);
        var rates = await PlanningRatesAsync(db, project.Id, ct);

        Guid? CategoryOf(Guid itemId)
        {
            var item = items.FirstOrDefault(i => i.Id == itemId);
            var email = ownerColumn is null || item is null ? null : item.Values.GetValueOrDefault(ownerColumn.Id.ToString());
            if (string.IsNullOrWhiteSpace(email))
            {
                return items.Where(i => i.ParentId == itemId).Select(c => CategoryOf(c.Id)).FirstOrDefault(c => c is not null);
            }

            return categoryByEmail.GetValueOrDefault(email.Trim());
        }

        var lines = new List<BudgetLine>();
        foreach (var element in wbs.Where(w => w.ItemId is not null && !parents.Contains(w.Id)).OrderBy(w => w.Code, StringComparer.Ordinal))
        {
            var (hours, budget) = PlanSheetBudget.Sum(rows, element.ItemId!.Value);
            if (hours > 0)
            {
                var category = CategoryOf(element.ItemId.Value);
                var rate = budget > 0 ? Money.RoundRate(budget / hours) : rates.RateFor(element.Id, category, Today);
                lines.Add(new BudgetLine
                {
                    WbsElementId = element.Id,
                    CostType = BudgetCostType.Labor,
                    LaborCategoryId = category,
                    Hours = hours,
                    PlanningRate = rate,
                    Amount = budget > 0 ? Money.Round(budget) : Money.Round(hours * rate),
                });
            }
            else if (budget > 0)
            {
                lines.Add(new BudgetLine { WbsElementId = element.Id, CostType = BudgetCostType.Odc, OdcType = OdcType.Other, Amount = Money.Round(budget) });
            }
        }

        return lines;
    }

    private static decimal? Number(Item item, Column? column) =>
        column is not null
        && item.Values.GetValueOrDefault(column.Id.ToString()) is { } raw
        && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static async Task<PlanningRateCalculator> PlanningRatesAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        var wbsIds = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).Select(w => w.Id).ToListAsync(ct);
        var assignments = await db.Set<ChargeAssignment>().AsNoTracking().Where(a => wbsIds.Contains(a.WbsElementId))
            .Select(a => new PlanningAssignment(a.WbsElementId, a.EmployeeId, a.LaborCategoryId, a.From, a.To))
            .ToListAsync(ct);
        var employees = await db.Set<Employee>().AsNoTracking()
            .Select(e => new PlanningEmployee(e.Id, e.DefaultLaborCategoryId, e.HireDate, e.TerminationDate))
            .ToListAsync(ct);
        var costRates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().ToListAsync(ct))
        {
            costRates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        return new PlanningRateCalculator(assignments, employees, costRates);
    }

    private static async Task<BudgetVersion> LoadEditableAsync(IWorkbenchDbContext db, Guid versionId, CancellationToken ct)
    {
        var version = await db.Set<BudgetVersion>().Include(v => v.Lines).FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw WorkbenchNotFoundException.For("Budget version", versionId);
        if (version.IsLocked)
        {
            throw new ArgumentException($"'{version.Name}' is a locked Baseline and can't be changed.");
        }

        return version;
    }

    private static LineKey KeyOf(BudgetLine l) => new(l.WbsElementId, l.CostType, l.LaborCategoryId, l.OdcType, l.PeriodStart);

    private static object Snapshot(IEnumerable<BudgetLine> lines) =>
        lines.Select(l => new { l.WbsElementId, CostType = l.CostType.ToString(), l.LaborCategoryId, OdcType = l.OdcType?.ToString(), l.PeriodStart, l.Hours, l.PlanningRate, l.Amount }).ToList();

    private static BudgetVersionDto ToDto(BudgetVersion v) =>
        new(v.Id, v.ProjectId, v.Name, (BudgetVersionKind)(int)v.Kind, v.IsLocked, v.Description, v.CreatedAtUtc, v.CreatedBy, v.LockedAtUtc, v.LockedBy,
            v.SourceVersionId, v.Lines.Sum(l => l.Hours), v.Lines.Sum(l => l.Amount), v.Lines.Count);

    private static async Task<BudgetVersionDetailDto> ToDetailAsync(IWorkbenchDbContext db, BudgetVersion version, CancellationToken ct)
    {
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == version.ProjectId).ToDictionaryAsync(w => w.Id, ct);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var lines = version.Lines
            .Select(l => new BudgetLineDto(l.Id, l.WbsElementId, wbs.GetValueOrDefault(l.WbsElementId)?.Code ?? "?", wbs.GetValueOrDefault(l.WbsElementId)?.Name ?? "?",
                (BudgetCostKind)(int)l.CostType, l.LaborCategoryId, l.LaborCategoryId is { } c ? categories.GetValueOrDefault(c) : null,
                l.OdcType is { } o ? (BudgetOdcType)(int)o : null, l.PeriodStart, l.Hours, l.PlanningRate, l.Amount))
            .OrderBy(l => l.WbsCode, StringComparer.Ordinal)
            .ThenBy(l => l.CostType)
            .ThenBy(l => l.LaborCategoryCode, StringComparer.Ordinal)
            .ThenBy(l => l.OdcType)
            .ThenBy(l => l.PeriodStart)
            .ToList();
        return new BudgetVersionDetailDto(ToDto(version), lines);
    }

    private readonly record struct LineKey(Guid WbsElementId, BudgetCostType CostType, Guid? LaborCategoryId, OdcType? OdcType, DateOnly? PeriodStart);
}
