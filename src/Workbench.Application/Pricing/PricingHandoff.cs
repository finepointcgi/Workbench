using Microsoft.EntityFrameworkCore;
using Workbench.Application.ContractDetail;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Pricing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Pricing;

/// <summary>
/// The won handoff, in the caller's unit of work: execution starts from what was bid. From a locked price it adds the
/// contract's CLINs (one per CLIN and period, 0001 → 1001 for option 1; existing unfunded CLINs get the bid value),
/// T&amp;M / labor-hour bill rates (per labor category, effective at each period's start), the project's WBS elements
/// (<c>{project}.{code}</c>, leaves mapped to their base-period CLIN) and a budget version: labor hours by WBS leaf, labor
/// category and fiscal period at the bid's direct rates, and ODCs by type, spread evenly over the fiscal periods each
/// period of performance covers. Everything is audited.
/// </summary>
internal static class PricingHandoff
{
    public static async Task<HandoffResultDto> RunAsync(IWorkbenchDbContext db, AuditRecorder audit, DateTime now, Proposal proposalRow, PriceModel model,
        PriceResultDto result, HandoffRequest request, CancellationToken ct)
    {
        var proposal = await db.Set<Proposal>().FirstAsync(p => p.Id == proposalRow.Id, ct);
        if (proposal.HandedOffAtUtc is not null)
        {
            throw new ArgumentException($"{proposal.Number} was already handed off to its contract on {proposal.HandedOffAtUtc:yyyy-MM-dd}.");
        }

        var contract = await db.Set<Contract>().FirstOrDefaultAsync(c => c.Id == request.ContractId, ct)
            ?? throw WorkbenchNotFoundException.For("Contract", request.ContractId);
        var project = await db.Set<Project>().FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct)
            ?? throw WorkbenchNotFoundException.For("Project", request.ProjectId);
        if (project.ContractId != contract.Id)
        {
            throw new ArgumentException($"Project {project.Code} isn't on contract {contract.ContractNumber}.");
        }

        var notes = new List<string>();
        var periods = model.Periods.OrderBy(p => p.Order).ThenBy(p => p.Start).ToList();
        if (periods.Count > 10)
        {
            throw new ArgumentException("CLIN numbers carry the period in their first digit, so at most 10 periods can be handed off.");
        }

        var periodIndex = periods.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var isTm = proposal.ContractType is PricingContractType.TimeAndMaterials or PricingContractType.LaborHour;
        var isCostPlus = proposal.ContractType is PricingContractType.CostPlusFixedFee or PricingContractType.CostPlusAwardFee or PricingContractType.CostPlusIncentiveFee;

        // CLINs.
        var (created, updated) = (0, 0);
        var awardClins = new Dictionary<(Guid ClinId, Guid PeriodId), Clin>();
        if (request.CreateClins)
        {
            var clins = await ClinRules.LoadTrackedAsync(db, contract.Id, ct);
            foreach (var cell in result.Cells.Where(c => c.ClinId is not null && c.Figures.Price != 0))
            {
                var source = model.Clins.First(c => c.Id == cell.ClinId);
                var period = periods.First(p => p.Id == cell.PeriodId);
                var number = AwardNumber(source.Number, periodIndex[period.Id]);
                var existing = clins.FirstOrDefault(c => c.Number == number);
                if (existing is not null)
                {
                    var funded = existing.FundedValue != 0 || await db.Set<FundingAction>().AnyAsync(f => f.ClinId == existing.Id, ct);
                    if (funded)
                    {
                        notes.Add($"CLIN {number} is already funded; its value stays {existing.CeilingValue:C} (change it through a modification).");
                    }
                    else if (existing.CeilingValue != cell.Figures.Price)
                    {
                        var clinBefore = existing.ToDto(clins);
                        existing.CeilingValue = cell.Figures.Price;
                        audit.Record(db, nameof(Clin), existing.Id, "Updated", clinBefore, existing.ToDto(clins), $"Bid value from {proposal.Number} version {model.Version}");
                        updated++;
                    }

                    awardClins[(source.Id, period.Id)] = existing;
                    continue;
                }

                var clin = await ClinService.AddClinAsync(db, contract, clins, new SaveClinRequest(number,
                        period.IsOption ? $"{source.Description} ({period.Name})" : source.Description,
                        (ClinPricingKind)(int)proposal.ContractType, period.Start, period.End, cell.Figures.Price,
                        isCostPlus ? result.FeeRate : null, period.IsOption), ct);
                audit.Record(db, nameof(Clin), clin.Id, "Created", after: clin.ToDto(clins), reason: $"Won handoff from {proposal.Number} version {model.Version}");
                awardClins[(source.Id, period.Id)] = clin;
                created++;
            }

            if (created + updated > 0)
            {
                ClinRules.SyncTotals(db, audit, contract, clins, $"Won handoff from {proposal.Number}");
            }
        }

        // Bill rates (T&M and labor-hour).
        var billRates = 0;
        if (request.CreateBillRates && isTm)
        {
            var existing = await db.Set<ContractLaborRate>().Where(r => r.ContractId == contract.Id).ToListAsync(ct);
            foreach (var rate in result.BillRates)
            {
                if (existing.Any(r => r.LaborCategoryId == rate.LaborCategoryId && r.EffectiveFrom == rate.PeriodStart))
                {
                    notes.Add($"{rate.LaborCategoryCode} already has a bill rate from {rate.PeriodStart:yyyy-MM-dd}; it was kept.");
                    continue;
                }

                var row = new ContractLaborRate { ContractId = contract.Id, LaborCategoryId = rate.LaborCategoryId, EffectiveFrom = rate.PeriodStart, HourlyBillRate = rate.BillRate };
                db.Set<ContractLaborRate>().Add(row);
                existing.Add(row);
                audit.Record(db, nameof(ContractLaborRate), row.Id, "Created", after: new { row.ContractId, row.LaborCategoryId, row.EffectiveFrom, row.HourlyBillRate },
                    reason: $"Won handoff from {proposal.Number} version {model.Version}");
                billRates++;
            }
        }
        else if (request.CreateBillRates)
        {
            notes.Add("Bill rates are generated for time-and-materials and labor-hour prices only.");
        }

        // WBS.
        var wbsCreated = 0;
        var elements = new Dictionary<Guid, WbsElement>();
        var projectWbs = await db.Set<WbsElement>().Where(w => w.ProjectId == project.Id).ToListAsync(ct);
        var parents = model.Wbs.Where(w => w.ParentId is not null).Select(w => w.ParentId!.Value).ToHashSet();
        foreach (var w in PricingService.TreeOrder(model.Wbs))
        {
            var code = $"{project.Code}.{w.Code}";
            var element = projectWbs.FirstOrDefault(e => e.Code == code);
            if (element is null)
            {
                if (await db.Set<WbsElement>().AnyAsync(e => e.Code == code, ct))
                {
                    throw new ArgumentException($"WBS {code} already exists on another project.");
                }

                var isLeaf = !parents.Contains(w.Id);
                element = new WbsElement
                {
                    ProjectId = project.Id,
                    ParentId = w.ParentId is { } p && elements.TryGetValue(p, out var parent) ? parent.Id : null,
                    Code = code,
                    Name = w.Name,
                    Kind = ChargeKind.Direct,
                    IsChargeable = isLeaf,
                    Order = w.Order,
                };
                if (isLeaf)
                {
                    var clinIds = model.LaborLines.Where(l => l.WbsId == w.Id).Select(l => l.ClinId)
                        .Concat(model.CostLines.Where(c => c.WbsId == w.Id).Select(c => c.ClinId)).OfType<Guid>().Distinct().ToList();
                    if (clinIds.Count == 1 && awardClins.Where(a => a.Key.ClinId == clinIds[0]).OrderBy(a => periodIndex[a.Key.PeriodId]).Select(a => a.Value).FirstOrDefault() is { } clin)
                    {
                        element.ClinId = clin.Id;
                    }
                }

                db.Set<WbsElement>().Add(element);
                projectWbs.Add(element);
                audit.Record(db, nameof(WbsElement), element.Id, "Created", after: new { element.Code, element.Name, element.ClinId }, reason: $"Won handoff from {proposal.Number}");
                wbsCreated++;
            }

            elements[w.Id] = element;
        }

        if (awardClins.Count > 0 && periods.Count > 1)
        {
            notes.Add("WBS leaves are mapped to their base-period CLIN; remap them to the option CLIN when an option is exercised.");
        }

        // Budget.
        Guid? budgetId = null;
        var budgetLines = 0;
        if (request.CreateBudget)
        {
            var kind = (BudgetKind)(int)request.BudgetKind;
            if (kind != BudgetKind.Forecast && await db.Set<BudgetVersion>().AnyAsync(v => v.ProjectId == project.Id && v.Kind == kind, ct))
            {
                throw new ArgumentException(kind == BudgetKind.Baseline
                    ? $"Project {project.Code} already has a Baseline; hand off to a Forecast instead."
                    : $"Project {project.Code} already has a Current budget; hand off to a Forecast and promote it.");
            }

            var fiscalPeriods = await db.Set<FiscalPeriod>().AsNoTracking().OrderBy(p => p.Start).ToListAsync(ct);

            // Budgets go on leaves: a bid element that matched an existing project element with children can't take them.
            var projectParents = projectWbs.Where(w => w.ParentId is not null).Select(w => w.ParentId!.Value).ToHashSet();
            var budgeted = model.LaborLines.Select(l => l.WbsId).Concat(model.CostLines.Where(c => !c.IsUnallowable).Select(c => c.WbsId)).Distinct();
            if (budgeted.Select(id => elements[id]).FirstOrDefault(e => projectParents.Contains(e.Id)) is { } summary)
            {
                throw new ArgumentException($"WBS {summary.Code} already exists on project {project.Code} and has children, so the bid's costs on it can't be budgeted. "
                    + "Renumber that element in the bid (a copy of the version) or hand off without the budget.");
            }

            var lines = new Dictionary<(Guid Wbs, BudgetCostType Type, Guid? Category, OdcType? Odc, DateOnly? Period), (decimal Hours, decimal Amount)>();
            void Add(Guid wbs, BudgetCostType type, Guid? category, OdcType? odc, DateOnly? period, decimal hours, decimal amount)
            {
                var key = (wbs, type, category, odc, period);
                var current = lines.GetValueOrDefault(key);
                lines[key] = (current.Hours + hours, current.Amount + amount);
            }

            var unphased = false;
            foreach (var row in result.Labor)
            {
                var line = model.LaborLines.First(l => l.Id == row.LineId);
                if (parents.Contains(line.WbsId))
                {
                    throw new ArgumentException($"WBS {elements[line.WbsId].Code} has children, so its labor can't be budgeted; move the labor to a leaf.");
                }

                var period = periods.First(p => p.Id == row.PeriodId);
                foreach (var (start, hours) in Spread(row.Hours, period, fiscalPeriods))
                {
                    unphased |= start is null;
                    Add(elements[line.WbsId].Id, BudgetCostType.Labor, row.LaborCategoryId, null, start, hours, Money.Round(hours * row.DirectRate));
                }
            }

            foreach (var cost in result.OtherCosts.Where(c => !c.IsUnallowable && c.Amount != 0))
            {
                var line = model.CostLines.First(l => l.Id == cost.LineId);
                var period = periods.First(p => p.Id == line.PeriodId);
                var type = line.Kind switch
                {
                    PriceCostKind.Travel => OdcType.Travel,
                    PriceCostKind.Materials => OdcType.Materials,
                    PriceCostKind.Subcontract => OdcType.Subcontract,
                    _ => OdcType.Other,
                };
                foreach (var (start, amount) in Spread(cost.Amount, period, fiscalPeriods))
                {
                    unphased |= start is null;
                    Add(elements[line.WbsId].Id, BudgetCostType.Odc, null, type, start, 0, amount);
                }
            }

            if (unphased)
            {
                notes.Add("Some periods of performance fall outside the fiscal calendar, so their budget is unphased; add the fiscal years and reprice the budget to phase it.");
            }

            var version = new BudgetVersion
            {
                ProjectId = project.Id,
                Name = string.IsNullOrWhiteSpace(request.BudgetName) ? $"Bid {proposal.Number} v{model.Version}" : request.BudgetName.Trim().Limit(200, "Budget name"),
                Kind = kind,
                Description = $"From proposal {proposal.Number} ({proposal.Title}), locked version {model.Version}: hours and direct rates as bid.".Limit(2000, "Description"),
                CreatedAtUtc = now,
                CreatedBy = audit.Actor,
            };
            foreach (var ((wbs, type, category, odc, start), (hours, amount)) in lines)
            {
                version.Lines.Add(new BudgetLine
                {
                    BudgetVersionId = version.Id,
                    WbsElementId = wbs,
                    CostType = type,
                    LaborCategoryId = category,
                    OdcType = odc,
                    PeriodStart = start,
                    Hours = hours,
                    PlanningRate = type == BudgetCostType.Labor && hours != 0 ? Money.RoundRate(amount / hours) : 0,
                    Amount = amount,
                });
            }

            db.Set<BudgetVersion>().Add(version);
            audit.Record(db, nameof(BudgetVersion), version.Id, "Created", after: new
            {
                version.Name, Kind = kind.ToString(), Source = "Proposal", Proposal = proposal.Number, model.Version,
                Hours = version.Lines.Sum(l => l.Hours), Amount = version.Lines.Sum(l => l.Amount), Lines = version.Lines.Count,
            });
            budgetId = version.Id;
            budgetLines = version.Lines.Count;
        }

        var before = new { Status = proposal.Status.ToString() };
        proposal.Status = ProposalStatus.Won;
        proposal.WonModelId = model.Id;
        proposal.WonContractId = contract.Id;
        proposal.WonProjectId = project.Id;
        proposal.WonBudgetVersionId = budgetId;
        proposal.HandedOffAtUtc = now;
        proposal.HandedOffBy = audit.Actor;
        proposal.UpdatedAtUtc = now;
        audit.Record(db, PricingService.ProposalEntity, proposal.Id, "HandedOff", before, new
        {
            Status = "Won", Version = model.Version, contract.ContractNumber, Project = project.Code, ClinsCreated = created, ClinsUpdated = updated,
            BillRates = billRates, WbsCreated = wbsCreated, BudgetVersionId = budgetId, BudgetLines = budgetLines,
        });

        return new HandoffResultDto(proposal.Id, contract.Id, project.Id, budgetId, created, updated, billRates, wbsCreated, budgetLines, notes);
    }

    /// <summary>The award CLIN number for a period: the first digit becomes the period's index (0001 → 1001 for the first option).</summary>
    public static string AwardNumber(string number, int periodIndex)
    {
        var n = ClinRules.Normalize(number);
        return periodIndex == 0 ? n : (char)('0' + periodIndex) + n[1..];
    }

    /// <summary>
    /// Splits an amount (hours or dollars) evenly over the fiscal periods a period of performance covers, by days of
    /// overlap, to the cent (the last piece takes the remainder). Unphased (null) when the fiscal calendar doesn't cover it.
    /// </summary>
    public static IReadOnlyList<(DateOnly? Start, decimal Amount)> Spread(decimal amount, PricePeriod period, IReadOnlyList<FiscalPeriod> fiscalPeriods)
    {
        var overlaps = fiscalPeriods.Where(f => f.Start <= period.End && f.End >= period.Start).ToList();
        var covered = overlaps.Sum(f => Days(f, period));
        if (overlaps.Count == 0 || covered < period.End.DayNumber - period.Start.DayNumber + 1)
        {
            return [(null, amount)];
        }

        var result = new List<(DateOnly?, decimal)>();
        var left = amount;
        for (var i = 0; i < overlaps.Count; i++)
        {
            var piece = i == overlaps.Count - 1 ? left : Money.Round(amount * Days(overlaps[i], period) / covered);
            left -= piece;
            if (piece != 0)
            {
                result.Add((overlaps[i].Start, piece));
            }
        }

        return result;

        static int Days(FiscalPeriod f, PricePeriod p) =>
            Math.Min(f.End.DayNumber, p.End.DayNumber) - Math.Max(f.Start.DayNumber, p.Start.DayNumber) + 1;
    }
}
