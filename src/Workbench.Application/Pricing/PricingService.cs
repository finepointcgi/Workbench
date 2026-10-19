using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ContractDetail;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Pricing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Pricing;

/// <summary>
/// Proposal pricing. A proposal's working price is edited in place; scenarios are copies for what-ifs; locking copies the
/// price into the next read-only version and freezes its computed result. Every change is audited (entity types
/// <see cref="ProposalEntity"/> and <see cref="ModelEntity"/>).
/// </summary>
public sealed class PricingService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IProposalPricingService
{
    public const string ProposalEntity = "Proposal";
    public const string ModelEntity = "PriceModel";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    // ---- Proposals ----

    public async Task<IReadOnlyList<ProposalSummaryDto>> ListProposalsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var proposals = await db.Set<Proposal>().AsNoTracking().ToListAsync(cancellationToken);
        // The submitted price is the latest locked version's frozen total: read only those snapshots.
        var locked = await db.Set<PriceModel>().AsNoTracking().Where(m => m.IsLocked)
            .Select(m => new { m.Id, m.ProposalId, m.Version }).ToListAsync(cancellationToken);
        var latestIds = locked.GroupBy(m => m.ProposalId).Select(g => g.MaxBy(m => m.Version)!.Id).ToList();
        var snapshots = await db.Set<PriceModel>().AsNoTracking().Where(m => latestIds.Contains(m.Id))
            .Select(m => new { m.ProposalId, m.LockedSnapshot }).ToListAsync(cancellationToken);
        var prices = snapshots.Where(s => s.LockedSnapshot is not null)
            .ToDictionary(s => s.ProposalId, s => JsonSerializer.Deserialize<PriceResultDto>(s.LockedSnapshot!, Json)?.Totals.Price);
        return proposals
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Select(p => new ProposalSummaryDto(p.Id, p.Number, p.Title, p.Customer, p.OpportunityId, p.OpportunityName, (PricingContractKind)(int)p.ContractType,
                (ProposalState)(int)p.Status, p.TargetPrice, p.DueDate, prices.GetValueOrDefault(p.Id), locked.Count(m => m.ProposalId == p.Id), p.UpdatedAtUtc))
            .ToList();
    }

    public async Task<ProposalDto> GetProposalAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var proposal = await db.Set<Proposal>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Proposal", proposalId);
        return await ToDtoAsync(db, proposal, cancellationToken);
    }

    public async Task<ProposalDto> CreateProposalAsync(SaveProposalRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var now = UtcNow;
        var proposal = new Proposal { Number = "", Title = "", CreatedBy = audit.Actor, CreatedAtUtc = now };
        await ApplyAsync(db, proposal, request, cancellationToken);
        proposal.UpdatedAtUtc = now;
        db.Set<Proposal>().Add(proposal);

        var start = request.FirstPeriodStart ?? new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);
        var options = Math.Clamp(request.OptionYears, 0, 9);
        var type = proposal.ContractType;
        var model = new PriceModel
        {
            ProposalId = proposal.Id,
            Name = "Working price",
            IsBase = true,
            RatesAsOf = Today,
            DefaultEscalation = 0.03m,
            FeeRate = type == PricingContractType.FirmFixedPrice ? 0.10m : 0.08m,
            WgContractTypeRisk = FeeTerms.NormalContractTypeRisk(type),
            CreatedBy = audit.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        for (var i = 0; i <= options; i++)
        {
            model.Periods.Add(new PricePeriod
            {
                ModelId = model.Id,
                Name = i == 0 ? "Base period" : $"Option {i}",
                Order = i,
                Start = start.AddYears(i),
                End = start.AddYears(i + 1).AddDays(-1),
                IsOption = i > 0,
            });
        }

        model.Clins.Add(new PriceClin { ModelId = model.Id, Number = "0001", Description = "Labor", Order = 0 });
        model.Wbs.Add(new PriceWbs { ModelId = model.Id, Code = "01", Name = "Program management", Order = 0 });
        db.Set<PriceModel>().Add(model);
        audit.Record(db, ProposalEntity, proposal.Id, "Created", after: Snapshot(proposal));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, proposal, cancellationToken);
    }

    public async Task<ProposalDto> UpdateProposalAsync(Guid proposalId, SaveProposalRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var proposal = await db.Set<Proposal>().FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Proposal", proposalId);
        var before = Snapshot(proposal);
        var typeBefore = proposal.ContractType;
        await ApplyAsync(db, proposal, request, cancellationToken);
        if (request.Status is { } status)
        {
            if (status == ProposalState.Won && proposal.Status != ProposalStatus.Won)
            {
                throw new ArgumentException("A proposal is marked Won by handing off a locked price to its contract.");
            }

            proposal.Status = (ProposalStatus)(int)status;
        }

        if (typeBefore != proposal.ContractType)
        {
            // Keep the weighted-guidelines contract type risk in step with the type on unlocked prices.
            foreach (var m in await db.Set<PriceModel>().Where(m => m.ProposalId == proposalId && !m.IsLocked).ToListAsync(cancellationToken))
            {
                if (m.WgContractTypeRisk == FeeTerms.NormalContractTypeRisk(typeBefore))
                {
                    m.WgContractTypeRisk = FeeTerms.NormalContractTypeRisk(proposal.ContractType);
                }
            }
        }

        proposal.UpdatedAtUtc = UtcNow;
        audit.Record(db, ProposalEntity, proposal.Id, "Updated", before, Snapshot(proposal));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, proposal, cancellationToken);
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, Proposal proposal, SaveProposalRequest request, CancellationToken ct)
    {
        var number = request.Number.Required("Proposal number").ToUpperInvariant();
        if (number.Length > 32)
        {
            throw new ArgumentException("The proposal number is at most 32 characters.");
        }

        if (await db.Set<Proposal>().AnyAsync(p => p.Number == number && p.Id != proposal.Id, ct))
        {
            throw new ArgumentException($"Proposal {number} already exists.");
        }

        if (request.TargetPrice is < 0)
        {
            throw new ArgumentException("The target price can't be negative.");
        }

        proposal.Number = number;
        proposal.Title = request.Title.Required("Title").Limit(200, "Title");
        proposal.Customer = Optional(request.Customer, 200, "Customer");
        proposal.OpportunityId = request.OpportunityId;
        proposal.OpportunityName = Optional(request.OpportunityName, 200, "Opportunity");
        proposal.ContractType = (PricingContractType)(int)request.ContractType;
        proposal.TargetPrice = request.TargetPrice;
        proposal.DueDate = request.DueDate;
        proposal.Notes = Optional(request.Notes, 2000, "Notes");
    }

    // ---- Models ----

    public async Task<PriceModelDto> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (proposal, model) = await LoadAsync(db, modelId, tracked: false, cancellationToken);
        return ToDto(proposal, model);
    }

    public Task<PriceModelDto> SaveSettingsAsync(Guid modelId, SavePriceSettingsRequest request, CancellationToken cancellationToken = default) =>
        EditAsync(modelId, "SettingsSaved", (db, proposal, model) =>
        {
            var s = request.Settings;
            Fraction(s.FringeOverride, 5, "The fringe override");
            Fraction(s.OverheadOverride, 5, "The overhead override");
            Fraction(s.GaOverride, 5, "The G&A override");
            Fraction(s.DefaultEscalation, 0.5m, "Escalation");
            Fraction(s.FeeRate, 1, "The fee rate");
            Fraction(s.WgTechnicalRisk, 1, "Technical risk");
            Fraction(s.WgManagementRisk, 1, "Management risk");
            Fraction(s.WgContractTypeRisk, 1, "Contract type risk");
            Fraction(s.WgCostEfficiency, 1, "Cost efficiency");
            model.RateBasis = (IndirectRateBasis)(int)s.RateBasis;
            (model.FringeOverride, model.OverheadOverride, model.GaOverride) = (s.FringeOverride, s.OverheadOverride, s.GaOverride);
            model.RatesAsOf = s.RatesAsOf;
            model.DefaultRateMethod = (LaborRateMethod)(int)s.DefaultRateMethod;
            model.DefaultEscalation = s.DefaultEscalation;
            model.FeeMethod = (FeeMethod)(int)s.FeeMethod;
            model.FeeRate = s.FeeRate;
            (model.WgTechnicalRisk, model.WgManagementRisk, model.WgContractTypeRisk, model.WgCostEfficiency) =
                (s.WgTechnicalRisk, s.WgManagementRisk, s.WgContractTypeRisk, s.WgCostEfficiency);
            return Task.FromResult<object>(SettingsOf(model));
        }, cancellationToken);

    public Task<PriceModelDto> SavePeriodsAsync(Guid modelId, IReadOnlyList<SavePricePeriodRequest> periods, CancellationToken cancellationToken = default) =>
        EditAsync(modelId, "PeriodsSaved", (db, proposal, model) =>
        {
            if (periods.Count == 0)
            {
                throw new ArgumentException("A price needs at least one period of performance.");
            }

            var ordered = periods.OrderBy(p => p.Start).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                p.Name.Required("Period name").Limit(60, "Period name");
                if (p.End < p.Start)
                {
                    throw new ArgumentException($"{p.Name} ends before it starts.");
                }

                if (i > 0 && p.Start <= ordered[i - 1].End)
                {
                    throw new ArgumentException($"{p.Name} overlaps {ordered[i - 1].Name}.");
                }
            }

            var kept = Upsert(db, model.Periods, ordered, r => r.Id, () => new PricePeriod { ModelId = model.Id, Name = "" }, (entity, r, order) =>
            {
                (entity.Name, entity.Start, entity.End, entity.IsOption, entity.Order) = (r.Name.Trim(), r.Start, r.End, r.IsOption, order);
            });
            var removed = model.Periods.Where(p => !kept.Contains(p.Id)).ToList();
            foreach (var period in removed)
            {
                if (model.CostLines.Any(c => c.PeriodId == period.Id))
                {
                    throw new ArgumentException($"Move or remove the other costs in {period.Name} before removing it.");
                }

                foreach (var line in model.LaborLines)
                {
                    db.Set<PriceLaborHours>().RemoveRange(line.Hours.Where(h => h.PeriodId == period.Id));
                }

                db.Set<PricePeriod>().Remove(period);
            }

            return Task.FromResult<object>(ordered.Select(p => new { p.Name, p.Start, p.End, p.IsOption }).ToList());
        }, cancellationToken);

    public Task<PriceModelDto> SaveClinsAsync(Guid modelId, IReadOnlyList<SavePriceClinRequest> clins, CancellationToken cancellationToken = default) =>
        EditAsync(modelId, "ClinsSaved", (db, proposal, model) =>
        {
            var numbers = new HashSet<string>();
            var normalized = clins.Select(c =>
            {
                var number = ClinRules.Normalize(c.Number);
                if (!numbers.Add(number))
                {
                    throw new ArgumentException($"CLIN {number} is listed twice.");
                }

                return c with { Number = number, Description = c.Description.Required("CLIN description").Limit(200, "CLIN description") };
            }).ToList();
            var kept = Upsert(db, model.Clins, normalized, r => r.Id, () => new PriceClin { ModelId = model.Id, Number = "", Description = "" },
                (entity, r, order) => (entity.Number, entity.Description, entity.Order) = (r.Number, r.Description, order));
            foreach (var clin in model.Clins.Where(c => !kept.Contains(c.Id)).ToList())
            {
                foreach (var line in model.LaborLines.Where(l => l.ClinId == clin.Id))
                {
                    line.ClinId = null;
                }

                foreach (var line in model.CostLines.Where(l => l.ClinId == clin.Id))
                {
                    line.ClinId = null;
                }

                db.Set<PriceClin>().Remove(clin);
            }

            return Task.FromResult<object>(normalized.Select(c => new { c.Number, c.Description }).ToList());
        }, cancellationToken);

    public Task<PriceModelDto> SaveWbsAsync(Guid modelId, IReadOnlyList<SavePriceWbsRequest> wbs, CancellationToken cancellationToken = default) =>
        EditAsync(modelId, "WbsSaved", (db, proposal, model) =>
        {
            if (wbs.Count == 0)
            {
                throw new ArgumentException("A price needs at least one WBS element.");
            }

            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in wbs)
            {
                var code = w.Code.Required("WBS code").Limit(40, "WBS code");
                if (!codes.Add(code))
                {
                    throw new ArgumentException($"WBS {code} is listed twice.");
                }

                w.Name.Required("WBS name").Limit(200, "WBS name");
                Optional(w.BasisOfEstimate, 4000, "Basis of estimate");
            }

            foreach (var w in wbs.Where(w => !string.IsNullOrWhiteSpace(w.ParentCode)))
            {
                if (!codes.Contains(w.ParentCode!.Trim()) || w.ParentCode.Trim() == w.Code.Trim())
                {
                    throw new ArgumentException($"WBS {w.Code}'s parent {w.ParentCode} isn't in the list.");
                }
            }

            var kept = Upsert(db, model.Wbs, wbs, r => r.Id, () => new PriceWbs { ModelId = model.Id, Code = "", Name = "" }, (entity, r, order) =>
            {
                (entity.Code, entity.Name, entity.BasisOfEstimate, entity.Order) = (r.Code.Trim(), r.Name.Trim(), Optional(r.BasisOfEstimate, 4000, "Basis of estimate"), order);
            });
            var byCode = model.Wbs.Where(w => kept.Contains(w.Id)).ToDictionary(w => w.Code, StringComparer.Ordinal);
            foreach (var r in wbs)
            {
                var entity = byCode[r.Code.Trim()];
                entity.ParentId = string.IsNullOrWhiteSpace(r.ParentCode) ? null : byCode[r.ParentCode.Trim()].Id;
            }

            // No cycles: walking up from any element must end.
            foreach (var entity in byCode.Values)
            {
                var seen = new HashSet<Guid>();
                for (var at = entity; at.ParentId is { } parent; at = model.Wbs.First(w => w.Id == parent))
                {
                    if (!seen.Add(at.Id))
                    {
                        throw new ArgumentException($"WBS {entity.Code} is its own ancestor.");
                    }
                }
            }

            foreach (var removed in model.Wbs.Where(w => !kept.Contains(w.Id)).ToList())
            {
                if (model.LaborLines.Any(l => l.WbsId == removed.Id) || model.CostLines.Any(c => c.WbsId == removed.Id))
                {
                    throw new ArgumentException($"WBS {removed.Code} has labor or other costs; move them before removing it.");
                }

                db.Set<PriceWbs>().Remove(removed);
            }

            return Task.FromResult<object>(wbs.Select(w => new { w.Code, w.Name, w.ParentCode }).ToList());
        }, cancellationToken);

    public Task<PriceModelDto> SaveEscalationsAsync(Guid modelId, IReadOnlyList<PriceEscalationDto> escalations, CancellationToken cancellationToken = default) =>
        EditAsync(modelId, "EscalationSaved", async (db, proposal, model) =>
        {
            if (escalations.Select(e => e.LaborCategoryId).Distinct().Count() != escalations.Count)
            {
                throw new ArgumentException("Each labor category can have one escalation rate.");
            }

            var ids = escalations.Select(e => e.LaborCategoryId).ToList();
            if (await db.Set<LaborCategory>().CountAsync(c => ids.Contains(c.Id)) != ids.Count)
            {
                throw new ArgumentException("Choose an existing labor category for each escalation rate.");
            }

            foreach (var e in escalations)
            {
                Fraction(e.Rate, 0.5m, "Escalation");
            }

            db.Set<PriceEscalation>().RemoveRange(model.Escalations);
            model.Escalations.Clear();
            foreach (var e in escalations)
            {
                var row = new PriceEscalation { ModelId = model.Id, LaborCategoryId = e.LaborCategoryId, Rate = e.Rate };
                model.Escalations.Add(row);
                db.Set<PriceEscalation>().Add(row);
            }

            return escalations;
        }, cancellationToken);

    public Task<PriceModelDto> SaveLaborLinesAsync(Guid modelId, IReadOnlyList<SavePriceLaborLineRequest> lines, CancellationToken cancellationToken = default) =>
        EditAsync(modelId, "LaborSaved", async (db, proposal, model) =>
        {
            var categoryIds = lines.Select(l => l.LaborCategoryId).Distinct().ToList();
            var categories = await db.Set<LaborCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
            var employeeIds = lines.SelectMany(l => (l.SelectedEmployeeIds ?? []).Append(l.EmployeeId ?? Guid.Empty)).Where(id => id != Guid.Empty).Distinct().ToList();
            var employees = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).Select(e => e.Id).ToListAsync();
            var periodIds = model.Periods.Select(p => p.Id).ToHashSet();
            foreach (var l in lines)
            {
                RequireIn(model, l.WbsId, l.ClinId);
                if (!categories.Contains(l.LaborCategoryId))
                {
                    throw WorkbenchNotFoundException.For("Labor category", l.LaborCategoryId);
                }

                if ((l.SelectedEmployeeIds ?? []).Append(l.EmployeeId ?? Guid.Empty).Any(id => id != Guid.Empty && !employees.Contains(id)))
                {
                    throw new ArgumentException("A labor line names someone who isn't an employee.");
                }

                if (l.HoursByPeriod.Keys.Any(p => !periodIds.Contains(p)))
                {
                    throw new ArgumentException("Labor hours must be on this price's periods.");
                }

                if (l.HoursByPeriod.Values.Any(h => h < 0 || h > 100_000))
                {
                    throw new ArgumentException("Hours must be between 0 and 100,000 per period.");
                }

                switch (l.RateMethod)
                {
                    case LaborRateSource.NamedEmployee when l.EmployeeId is null:
                        throw new ArgumentException("Name the employee whose cost rate prices the line, or choose another rate method.");
                    case LaborRateSource.SelectedPeople when (l.SelectedEmployeeIds ?? []).Count == 0:
                        throw new ArgumentException("Select the people whose average cost rate prices the line.");
                    case LaborRateSource.Manual when l.ManualRate is not > 0:
                        throw new ArgumentException("Enter the hourly rate for a manually priced line.");
                }

                if (l.ManualRate is < 0 or > 10_000)
                {
                    throw new ArgumentException("A labor rate must be between 0 and 10,000 an hour.");
                }

                Optional(l.StaffName, 100, "Staff");
                Optional(l.Note, 500, "Note");
            }

            var kept = Upsert(db, model.LaborLines, lines, r => r.Id, () => new PriceLaborLine { ModelId = model.Id }, (entity, r, order) =>
            {
                entity.WbsId = r.WbsId;
                entity.ClinId = r.ClinId;
                entity.LaborCategoryId = r.LaborCategoryId;
                entity.EmployeeId = r.EmployeeId;
                entity.StaffName = r.EmployeeId is null ? Optional(r.StaffName, 100, "Staff") : null;
                entity.RateMethod = (LaborRateMethod)(int)r.RateMethod;
                entity.SelectedEmployeeIds = r.RateMethod == LaborRateSource.SelectedPeople ? string.Join(",", (r.SelectedEmployeeIds ?? []).Distinct()) : null;
                entity.ManualRate = r.ManualRate;
                entity.Note = Optional(r.Note, 500, "Note");
                entity.Order = order;
                // Update hours in place (one row per period), so the unique (line, period) index never sees a duplicate.
                foreach (var row in entity.Hours.Where(h => r.HoursByPeriod.GetValueOrDefault(h.PeriodId) == 0).ToList())
                {
                    entity.Hours.Remove(row);
                    db.Set<PriceLaborHours>().Remove(row);
                }

                foreach (var (periodId, hours) in r.HoursByPeriod.Where(h => h.Value != 0))
                {
                    var value = Math.Round(hours, 2, MidpointRounding.AwayFromZero);
                    if (entity.Hours.FirstOrDefault(h => h.PeriodId == periodId) is { } existing)
                    {
                        existing.Hours = value;
                        continue;
                    }

                    var row = new PriceLaborHours { LineId = entity.Id, PeriodId = periodId, Hours = value };
                    entity.Hours.Add(row);
                    db.Set<PriceLaborHours>().Add(row);
                }
            });
            db.Set<PriceLaborLine>().RemoveRange(model.LaborLines.Where(l => !kept.Contains(l.Id)));
            return lines.Select(l => new { l.WbsId, l.ClinId, l.LaborCategoryId, l.EmployeeId, Method = l.RateMethod.ToString(), Hours = l.HoursByPeriod.Values.Sum() }).ToList();
        }, cancellationToken);

    public Task<PriceModelDto> SaveCostLinesAsync(Guid modelId, IReadOnlyList<SavePriceCostLineRequest> lines, CancellationToken cancellationToken = default) =>
        EditAsync(modelId, "CostsSaved", (db, proposal, model) =>
        {
            foreach (var l in lines)
            {
                RequireIn(model, l.WbsId, l.ClinId);
                if (model.Periods.All(p => p.Id != l.PeriodId))
                {
                    throw new ArgumentException("Choose one of this price's periods for each cost.");
                }

                l.Description.Required("Description").Limit(200, "Description");
                if (l.Quantity < 0 || l.UnitCost < 0 || l.AirfarePerTrip < 0 || l.OtherPerTrip < 0 || l.Trips < 0 || l.Travelers < 0 || l.Nights < 0)
                {
                    throw new ArgumentException("Quantities, costs, trips, travelers and nights can't be negative.");
                }

                if (l.Kind == PriceCostType.Travel && (l.Trips == 0 || l.Travelers == 0))
                {
                    throw new ArgumentException($"Travel \"{l.Description}\" needs the number of trips and travelers.");
                }

                Optional(l.Vendor, 200, "Vendor");
                Optional(l.QuoteReference, 100, "Quote reference");
                Optional(l.Destination, 120, "Destination");
                Optional(l.State, 60, "State");
            }

            var kept = Upsert(db, model.CostLines, lines, r => r.Id, () => new PriceCostLine { ModelId = model.Id, Description = "" }, (entity, r, order) =>
            {
                entity.WbsId = r.WbsId;
                entity.ClinId = r.ClinId;
                entity.PeriodId = r.PeriodId;
                entity.Kind = (PriceCostKind)(int)r.Kind;
                entity.Description = r.Description.Trim();
                entity.Quantity = r.Kind == PriceCostType.Travel ? 1 : r.Quantity;
                entity.UnitCost = r.Kind == PriceCostType.Travel ? 0 : r.UnitCost;
                entity.IsUnallowable = r.IsUnallowable;
                entity.Vendor = Optional(r.Vendor, 200, "Vendor");
                entity.QuoteReference = Optional(r.QuoteReference, 100, "Quote reference");
                entity.Destination = r.Kind == PriceCostType.Travel ? Optional(r.Destination, 120, "Destination") : null;
                entity.State = r.Kind == PriceCostType.Travel ? Optional(r.State, 60, "State") : null;
                (entity.Trips, entity.Travelers, entity.Nights) = r.Kind == PriceCostType.Travel ? (r.Trips, r.Travelers, r.Nights) : (0, 0, 0);
                (entity.AirfarePerTrip, entity.OtherPerTrip) = r.Kind == PriceCostType.Travel ? (r.AirfarePerTrip, r.OtherPerTrip) : (0, 0);
                entity.Order = order;
            });
            db.Set<PriceCostLine>().RemoveRange(model.CostLines.Where(l => !kept.Contains(l.Id)));
            return Task.FromResult<object>(lines.Select(l => new { Kind = l.Kind.ToString(), l.Description, l.PeriodId, l.Quantity, l.UnitCost, l.IsUnallowable }).ToList());
        }, cancellationToken);

    public async Task<PriceResultDto> CalculateAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (proposal, model) = await LoadAsync(db, modelId, tracked: false, cancellationToken);
        return await ResultOfAsync(db, proposal, model, cancellationToken);
    }

    internal async Task<PriceResultDto> ResultOfAsync(IWorkbenchDbContext db, Proposal proposal, PriceModel model, CancellationToken ct)
    {
        if (model.IsLocked && model.LockedSnapshot is { } snapshot)
        {
            var frozen = JsonSerializer.Deserialize<PriceResultDto>(snapshot, Json)!;

            // The target can move after submission; price-to-win is measured against today's target.
            return frozen with { PriceToWin = proposal.TargetPrice is { } t && t > 0 ? PriceCalculator.PriceToWin(t, frozen.Totals) : null };
        }

        return PriceCalculator.Calculate(await PricingInputs.BuildAsync(db, proposal, model, ct), UtcNow);
    }

    public async Task<PriceModelDto> CopyModelAsync(Guid modelId, CopyPriceModelRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name.Required("Scenario name").Limit(100, "Scenario name");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (proposal, source) = await LoadAsync(db, modelId, tracked: false, cancellationToken);
        var copy = Clone(source, name, Optional(request.Description, 1000, "Description"));
        db.Set<PriceModel>().Add(copy);
        await TouchAsync(db, proposal.Id, cancellationToken);
        audit.Record(db, ModelEntity, copy.Id, "Copied", after: new { From = source.Id, FromName = source.Name, copy.Name });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(proposal, copy);
    }

    public async Task DeleteModelAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (proposal, model) = await LoadAsync(db, modelId, tracked: true, cancellationToken);
        if (model.IsBase)
        {
            throw new ArgumentException("The working price can't be deleted.");
        }

        if (model.IsLocked)
        {
            throw new ArgumentException($"Version {model.Version} is locked; submitted prices are kept.");
        }

        db.Set<PriceModel>().Remove(model);
        await TouchAsync(db, proposal.Id, cancellationToken);
        audit.Record(db, ModelEntity, model.Id, "Deleted", before: new { model.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PriceModelDto> LockModelAsync(Guid modelId, LockPriceModelRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (proposal, source) = await LoadAsync(db, modelId, tracked: false, cancellationToken);
        if (source.IsLocked)
        {
            throw new ArgumentException($"Version {source.Version} is already locked.");
        }

        if (source.LaborLines.Count == 0 && source.CostLines.Count == 0)
        {
            throw new ArgumentException("There's nothing to lock yet: add labor or other costs first.");
        }

        var note = Optional(request.Note, 1000, "Note");
        var version = (await db.Set<PriceModel>().Where(m => m.ProposalId == proposal.Id && m.IsLocked).MaxAsync(m => m.Version, cancellationToken) ?? 0) + 1;
        var now = UtcNow;
        var locked = Clone(source, $"Version {version}", source.Description);
        locked.IsLocked = true;
        locked.Version = version;
        locked.LockedAtUtc = now;
        locked.LockedBy = audit.Actor;
        locked.LockNote = note;
        var result = PriceCalculator.Calculate(await PricingInputs.BuildAsync(db, proposal, locked, cancellationToken), now);
        locked.LockedSnapshot = JsonSerializer.Serialize(result, Json);
        db.Set<PriceModel>().Add(locked);

        var tracked = await db.Set<Proposal>().FirstAsync(p => p.Id == proposal.Id, cancellationToken);
        if (request.MarkSubmitted && tracked.Status == ProposalStatus.Draft)
        {
            tracked.Status = ProposalStatus.Submitted;
        }

        tracked.UpdatedAtUtc = now;
        audit.Record(db, ModelEntity, locked.Id, "Locked",
            after: new { Version = version, From = source.Id, FromName = source.Name, result.Totals.TotalCost, result.Totals.Fee, result.Totals.Price, Basis = result.RateBasis.ToString() },
            reason: note);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(proposal, locked);
    }

    public async Task<ScenarioComparisonDto> CompareAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var proposal = await db.Set<Proposal>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Proposal", proposalId);
        var ids = await db.Set<PriceModel>().AsNoTracking().Where(m => m.ProposalId == proposalId).Select(m => m.Id).ToListAsync(cancellationToken);
        var rows = new List<ScenarioRowDto>();
        foreach (var id in ids)
        {
            var (_, model) = await LoadAsync(db, id, tracked: false, cancellationToken);
            var r = await ResultOfAsync(db, proposal, model, cancellationToken);
            rows.Add(new ScenarioRowDto(RefOf(model), r.Totals.Hours, r.Totals.DirectLabor, r.Totals.TotalCost, r.Totals.Fee, r.Totals.Price, r.FeeRate, r.WrapRate,
                r.Margin, r.PriceToWin));
        }

        return new ScenarioComparisonDto(proposalId, proposal.TargetPrice, rows.OrderBy(r => ModelOrder(r.Model)).ThenBy(r => r.Model.Version).ThenBy(r => r.Model.Name).ToList());
    }

    public async Task<IReadOnlyList<PricingHistoryDto>> ListHistoryAsync(Guid proposalId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Proposal>().AnyAsync(p => p.Id == proposalId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Proposal", proposalId);
        }

        // Deleted scenarios are gone from the table but their audit rows remain; find them through the proposal's events too.
        var modelIds = await db.Set<PriceModel>().AsNoTracking().Where(m => m.ProposalId == proposalId).Select(m => m.Id).ToListAsync(cancellationToken);
        var ids = modelIds.Append(proposalId).ToList();
        var rows = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => (a.EntityType == ProposalEntity || a.EntityType == ModelEntity) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.OccurredAtUtc).Take(500).ToListAsync(cancellationToken);
        return rows.Select(a => new PricingHistoryDto(a.OccurredAtUtc, a.Actor, a.EntityType, a.EntityId, a.Action, a.Reason)).ToList();
    }

    public async Task<PricingOptionsDto> GetOptionsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var date = asOf ?? Today;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var book = await LaborRateBook.LoadAsync(db, date, cancellationToken);
        var categories = await db.Set<LaborCategory>().AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken);
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => c.Status != ContractStatus.Closed).ToListAsync(cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId != null && p.Status != ProjectStatus.Closed).ToListAsync(cancellationToken);
        return new PricingOptionsDto(
            date,
            categories.OrderBy(c => c.Code, StringComparer.Ordinal).Select(c =>
            {
                var rates = book.CategoryRates(c.Id);
                return new PricingLaborCategoryDto(c.Id, c.Code, c.Name, rates.Count, LaborRateBook.Average(rates), LaborRateBook.Median(rates));
            }).ToList(),
            book.Employees.Where(e => e.IsActiveOn(date)).OrderBy(e => e.DisplayName, StringComparer.Ordinal)
                .Select(e => new PricingEmployeeDto(e.Id, e.DisplayName, e.DefaultLaborCategoryId, book.RateOf(e.Id))).ToList(),
            contracts.OrderBy(c => c.ContractNumber, StringComparer.Ordinal).Select(c => new PricingContractRefDto(c.Id, c.ContractNumber, c.Name,
                projects.Where(p => p.ContractId == c.Id).OrderBy(p => p.Code, StringComparer.Ordinal).Select(p => new PricingProjectRefDto(p.Id, p.Code, p.Name)).ToList())).ToList());
    }

    public async Task<PricingExportFile> ExportCostVolumeAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (proposal, model) = await LoadAsync(db, modelId, tracked: false, cancellationToken);
        var result = await ResultOfAsync(db, proposal, model, cancellationToken);
        var content = CostVolumeWorkbook.Build(proposal, model, result);
        var suffix = model.IsLocked ? $"v{model.Version}" : Slug(model.Name);
        return new PricingExportFile($"{Slug(proposal.Number)}-cost-volume-{suffix}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", content);
    }

    public async Task<HandoffResultDto> HandOffAsync(Guid modelId, HandoffRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (proposal, model) = await LoadAsync(db, modelId, tracked: false, cancellationToken);
        if (!model.IsLocked)
        {
            throw new ArgumentException("Hand off a locked version: lock the price as submitted first.");
        }

        var result = await ResultOfAsync(db, proposal, model, cancellationToken);
        var outcome = await PricingHandoff.RunAsync(db, audit, UtcNow, proposal, model, result, request, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    // ---- Helpers ----

    internal static async Task<(Proposal Proposal, PriceModel Model)> LoadAsync(IWorkbenchDbContext db, Guid modelId, bool tracked, CancellationToken ct)
    {
        // One query per collection (a single query with every Include multiplies the rows). Tracked loads fix up the
        // navigations themselves; untracked ones are filled here.
        IQueryable<T> Q<T>() where T : class => tracked ? db.Set<T>() : db.Set<T>().AsNoTracking();
        static void Fill<T>(List<T> target, IEnumerable<T> rows)
        {
            foreach (var row in rows)
            {
                if (!target.Contains(row))
                {
                    target.Add(row);
                }
            }
        }

        var model = await Q<PriceModel>().FirstOrDefaultAsync(m => m.Id == modelId, ct) ?? throw WorkbenchNotFoundException.For("Price", modelId);
        Fill(model.Periods, await Q<PricePeriod>().Where(p => p.ModelId == modelId).ToListAsync(ct));
        Fill(model.Clins, await Q<PriceClin>().Where(c => c.ModelId == modelId).ToListAsync(ct));
        Fill(model.Wbs, await Q<PriceWbs>().Where(w => w.ModelId == modelId).ToListAsync(ct));
        Fill(model.Escalations, await Q<PriceEscalation>().Where(e => e.ModelId == modelId).ToListAsync(ct));
        Fill(model.CostLines, await Q<PriceCostLine>().Where(c => c.ModelId == modelId).ToListAsync(ct));
        Fill(model.LaborLines, await Q<PriceLaborLine>().Where(l => l.ModelId == modelId).ToListAsync(ct));
        var lineIds = model.LaborLines.Select(l => l.Id).ToList();
        var hours = (await Q<PriceLaborHours>().Where(h => lineIds.Contains(h.LineId)).ToListAsync(ct)).ToLookup(h => h.LineId);
        foreach (var line in model.LaborLines)
        {
            Fill(line.Hours, hours[line.Id]);
        }

        var proposal = await Q<Proposal>().FirstAsync(p => p.Id == model.ProposalId, ct);
        return (proposal, model);
    }

    private async Task<PriceModelDto> EditAsync(Guid modelId, string action, Func<IWorkbenchDbContext, Proposal, PriceModel, Task<object>> edit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var (proposal, model) = await LoadAsync(db, modelId, tracked: true, ct);
        if (model.IsLocked)
        {
            throw new ArgumentException($"Version {model.Version} is locked; copy it to a scenario to change it.");
        }

        var after = await edit(db, proposal, model);
        var now = UtcNow;
        model.UpdatedAtUtc = now;
        proposal.UpdatedAtUtc = now;
        audit.Record(db, ModelEntity, model.Id, action, after: after);
        await db.SaveChangesAsync(ct);
        await using var fresh = await dbFactory.CreateAsync(ct);
        var (p, m) = await LoadAsync(fresh, modelId, tracked: false, ct);
        return ToDto(p, m);
    }

    /// <summary>Updates rows with a matching id, adds the rest; returns the ids kept. <paramref name="apply"/> gets the row's position.</summary>
    private static HashSet<Guid> Upsert<TEntity, TRequest>(IWorkbenchDbContext db, List<TEntity> existing, IReadOnlyList<TRequest> requests, Func<TRequest, Guid?> idOf,
        Func<TEntity> create, Action<TEntity, TRequest, int> apply)
        where TEntity : Domain.Entity
    {
        var kept = new HashSet<Guid>();
        var byId = existing.ToDictionary(e => e.Id);
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (idOf(request) is { } id && byId.TryGetValue(id, out var entity) && !kept.Contains(id))
            {
                apply(entity, request, i);
            }
            else
            {
                entity = create();
                apply(entity, request, i);
                existing.Add(entity);
                db.Set<TEntity>().Add(entity);
            }

            kept.Add(entity.Id);
        }

        return kept;
    }

    private static void RequireIn(PriceModel model, Guid wbsId, Guid? clinId)
    {
        if (model.Wbs.All(w => w.Id != wbsId))
        {
            throw new ArgumentException("Choose one of this price's WBS elements for each line.");
        }

        if (clinId is { } c && model.Clins.All(x => x.Id != c))
        {
            throw new ArgumentException("Choose one of this price's CLINs for each line.");
        }
    }

    /// <summary>A deep copy with new ids, unlocked.</summary>
    internal PriceModel Clone(PriceModel source, string name, string? description)
    {
        var now = UtcNow;
        var copy = new PriceModel
        {
            ProposalId = source.ProposalId,
            Name = name,
            Description = description,
            SourceModelId = source.Id,
            RateBasis = source.RateBasis,
            FringeOverride = source.FringeOverride,
            OverheadOverride = source.OverheadOverride,
            GaOverride = source.GaOverride,
            RatesAsOf = source.RatesAsOf,
            DefaultRateMethod = source.DefaultRateMethod,
            DefaultEscalation = source.DefaultEscalation,
            FeeMethod = source.FeeMethod,
            FeeRate = source.FeeRate,
            WgTechnicalRisk = source.WgTechnicalRisk,
            WgManagementRisk = source.WgManagementRisk,
            WgContractTypeRisk = source.WgContractTypeRisk,
            WgCostEfficiency = source.WgCostEfficiency,
            CreatedBy = audit.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var periods = source.Periods.ToDictionary(p => p.Id, p => new PricePeriod { ModelId = copy.Id, Name = p.Name, Order = p.Order, Start = p.Start, End = p.End, IsOption = p.IsOption });
        var clins = source.Clins.ToDictionary(c => c.Id, c => new PriceClin { ModelId = copy.Id, Number = c.Number, Description = c.Description, Order = c.Order });
        var wbs = source.Wbs.ToDictionary(w => w.Id, w => new PriceWbs { ModelId = copy.Id, Code = w.Code, Name = w.Name, BasisOfEstimate = w.BasisOfEstimate, Order = w.Order });
        foreach (var w in source.Wbs.Where(w => w.ParentId is not null))
        {
            wbs[w.Id].ParentId = wbs.TryGetValue(w.ParentId!.Value, out var parent) ? parent.Id : null;
        }

        copy.Periods.AddRange(periods.Values);
        copy.Clins.AddRange(clins.Values);
        copy.Wbs.AddRange(wbs.Values);
        copy.Escalations.AddRange(source.Escalations.Select(e => new PriceEscalation { ModelId = copy.Id, LaborCategoryId = e.LaborCategoryId, Rate = e.Rate }));
        foreach (var l in source.LaborLines)
        {
            var line = new PriceLaborLine
            {
                ModelId = copy.Id,
                WbsId = wbs[l.WbsId].Id,
                ClinId = l.ClinId is { } c ? clins[c].Id : null,
                LaborCategoryId = l.LaborCategoryId,
                EmployeeId = l.EmployeeId,
                StaffName = l.StaffName,
                RateMethod = l.RateMethod,
                SelectedEmployeeIds = l.SelectedEmployeeIds,
                ManualRate = l.ManualRate,
                Note = l.Note,
                Order = l.Order,
            };
            line.Hours.AddRange(l.Hours.Where(h => periods.ContainsKey(h.PeriodId)).Select(h => new PriceLaborHours { LineId = line.Id, PeriodId = periods[h.PeriodId].Id, Hours = h.Hours }));
            copy.LaborLines.Add(line);
        }

        copy.CostLines.AddRange(source.CostLines.Select(c => new PriceCostLine
        {
            ModelId = copy.Id,
            WbsId = wbs[c.WbsId].Id,
            ClinId = c.ClinId is { } clin ? clins[clin].Id : null,
            PeriodId = periods[c.PeriodId].Id,
            Kind = c.Kind,
            Description = c.Description,
            Quantity = c.Quantity,
            UnitCost = c.UnitCost,
            IsUnallowable = c.IsUnallowable,
            Vendor = c.Vendor,
            QuoteReference = c.QuoteReference,
            Destination = c.Destination,
            State = c.State,
            Trips = c.Trips,
            Travelers = c.Travelers,
            Nights = c.Nights,
            AirfarePerTrip = c.AirfarePerTrip,
            OtherPerTrip = c.OtherPerTrip,
            Order = c.Order,
        }));
        return copy;
    }

    private async Task TouchAsync(IWorkbenchDbContext db, Guid proposalId, CancellationToken ct)
    {
        var proposal = await db.Set<Proposal>().FirstAsync(p => p.Id == proposalId, ct);
        proposal.UpdatedAtUtc = UtcNow;
    }

    private static async Task<ProposalDto> ToDtoAsync(IWorkbenchDbContext db, Proposal p, CancellationToken ct)
    {
        var models = await db.Set<PriceModel>().AsNoTracking().Where(m => m.ProposalId == p.Id).ToListAsync(ct);
        var handoff = p.HandedOffAtUtc is { } at && p.WonModelId is { } modelId && p.WonContractId is { } contractId && p.WonProjectId is { } projectId
            ? new HandoffDto(modelId, contractId, projectId, p.WonBudgetVersionId, at, p.HandedOffBy ?? "")
            : null;
        return new ProposalDto(p.Id, p.Number, p.Title, p.Customer, p.OpportunityId, p.OpportunityName, (PricingContractKind)(int)p.ContractType, (ProposalState)(int)p.Status,
            p.TargetPrice, p.DueDate, p.Notes, p.CreatedBy, p.CreatedAtUtc,
            models.Select(RefOf).OrderBy(ModelOrder).ThenBy(m => m.Version).ThenBy(m => m.Name, StringComparer.Ordinal).ToList(), handoff);
    }

    private static int ModelOrder(PriceModelRefDto m) => m.IsBase ? 0 : m.IsLocked ? 2 : 1;

    internal static PriceModelRefDto RefOf(PriceModel m) =>
        new(m.Id, m.Name, m.Description, m.IsBase, m.IsLocked, m.Version, m.LockedAtUtc, m.LockedBy, m.LockNote, m.SourceModelId, m.UpdatedAtUtc);

    private static PriceSettingsDto SettingsOf(PriceModel m) =>
        new((RateBasis)(int)m.RateBasis, m.FringeOverride, m.OverheadOverride, m.GaOverride, m.RatesAsOf, (LaborRateSource)(int)m.DefaultRateMethod, m.DefaultEscalation,
            (FeeMethodKind)(int)m.FeeMethod, m.FeeRate, m.WgTechnicalRisk, m.WgManagementRisk, m.WgContractTypeRisk, m.WgCostEfficiency);

    internal static PriceModelDto ToDto(Proposal proposal, PriceModel m)
    {
        var wbsOrder = TreeOrder(m.Wbs);
        return new PriceModelDto(m.Id, proposal.Id, proposal.Number, proposal.Title, (PricingContractKind)(int)proposal.ContractType, RefOf(m), SettingsOf(m),
            m.Periods.OrderBy(p => p.Order).ThenBy(p => p.Start).Select(p => new PricePeriodDto(p.Id, p.Name, p.Order, p.Start, p.End, p.IsOption)).ToList(),
            m.Clins.OrderBy(c => c.Order).ThenBy(c => c.Number, StringComparer.Ordinal).Select(c => new PriceClinDto(c.Id, c.Number, c.Description, c.Order)).ToList(),
            wbsOrder.Select(w => new PriceWbsDto(w.Id, w.ParentId, w.Code, w.Name, w.BasisOfEstimate, w.Order)).ToList(),
            m.Escalations.Select(e => new PriceEscalationDto(e.LaborCategoryId, e.Rate)).ToList(),
            m.LaborLines.OrderBy(l => l.Order).Select(l => new PriceLaborLineDto(l.Id, l.WbsId, l.ClinId, l.LaborCategoryId, l.EmployeeId, l.StaffName,
                (LaborRateSource)(int)l.RateMethod, ParseIds(l.SelectedEmployeeIds), l.ManualRate, l.Note, l.Order,
                l.Hours.GroupBy(h => h.PeriodId).ToDictionary(g => g.Key, g => g.Sum(h => h.Hours)))).ToList(),
            m.CostLines.OrderBy(c => c.Order).Select(c => new PriceCostLineDto(c.Id, c.WbsId, c.ClinId, c.PeriodId, (PriceCostType)(int)c.Kind, c.Description, c.Quantity,
                c.UnitCost, c.IsUnallowable, c.Vendor, c.QuoteReference, c.Destination, c.State, c.Trips, c.Travelers, c.Nights, c.AirfarePerTrip, c.OtherPerTrip, c.Order)).ToList());
    }

    /// <summary>Parents before children, siblings in order.</summary>
    internal static IReadOnlyList<PriceWbs> TreeOrder(IEnumerable<PriceWbs> wbs)
    {
        var all = wbs.ToList();
        var ids = all.Select(w => w.Id).ToHashSet();
        var children = all.Where(w => w.ParentId is { } p && ids.Contains(p)).ToLookup(w => w.ParentId!.Value);
        var result = new List<PriceWbs>();
        void Walk(IEnumerable<PriceWbs> level)
        {
            foreach (var w in level.OrderBy(w => w.Order).ThenBy(w => w.Code, StringComparer.Ordinal))
            {
                result.Add(w);
                Walk(children[w.Id]);
            }
        }

        Walk(all.Where(w => w.ParentId is null || !ids.Contains(w.ParentId.Value)));
        return result;
    }

    internal static IReadOnlyList<Guid> ParseIds(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    private static object Snapshot(Proposal p) =>
        new { p.Number, p.Title, p.Customer, p.OpportunityId, p.OpportunityName, ContractType = p.ContractType.ToString(), Status = p.Status.ToString(), p.TargetPrice, p.DueDate };

    private static string? Optional(string? value, int max, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Limit(max, what);
    }

    private static void Fraction(decimal? value, decimal max, string what)
    {
        if (value is { } v && (v < 0 || v > max))
        {
            throw new ArgumentException($"{what} must be a fraction between 0 and {max} (0.05 = 5%).");
        }
    }

    private static string Slug(string text) =>
        new string(text.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray()).Trim('-');
}

internal static class PricingText
{
    public static string Limit(this string value, int max, string what) =>
        value.Length <= max ? value : throw new ArgumentException($"{what} is at most {max} characters.");
}
