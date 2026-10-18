using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Contracts;
using Workbench.Domain.ProjectAccounting;
using ChangeKind = Workbench.Contracts.Contracts.ModificationChangeKind;
using DomainChangeKind = Workbench.Domain.ProjectAccounting.ModificationChangeKind;

namespace Workbench.Application.ContractDetail;

public sealed class ContractModificationService(
    IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock, FundingNoticeChecker notices) : IContractModificationService
{
    public async Task<IReadOnlyList<ContractModificationDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        var mods = await db.Set<ContractModification>().AsNoTracking().Where(m => m.ContractId == contractId).ToListAsync(cancellationToken);
        var ids = mods.Select(m => m.Id).ToList();
        var changes = await db.Set<ContractModificationChange>().AsNoTracking().Where(c => ids.Contains(c.ModificationId)).ToListAsync(cancellationToken);
        var funding = await db.Set<FundingAction>().AsNoTracking().Where(f => f.ModificationId != null && ids.Contains(f.ModificationId.Value)).ToListAsync(cancellationToken);
        return mods
            .OrderBy(m => m.Status)
            .ThenByDescending(m => m.EffectiveDate)
            .ThenByDescending(m => m.Number, StringComparer.Ordinal)
            .Select(m => ToDto(m, changes, funding))
            .ToList();
    }

    public async Task<ContractModificationDto?> GetAsync(Guid modificationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var mod = await db.Set<ContractModification>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == modificationId, cancellationToken);
        return mod is null ? null : await LoadDtoAsync(db, mod, cancellationToken);
    }

    public async Task<ContractModificationDto> CreateDraftAsync(Guid contractId, SaveModificationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        var mod = new ContractModification { ContractId = contractId, Number = "", Description = "" };
        await ApplyAsync(db, mod, request, cancellationToken);
        db.Set<ContractModification>().Add(mod);
        var changes = ReplaceChanges(db, mod, request.Changes, []);
        var dto = ToDto(mod, changes, []);
        audit.Record(db, nameof(ContractModification), mod.Id, "Drafted", after: dto);
        await db.SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task<ContractModificationDto> UpdateDraftAsync(Guid modificationId, SaveModificationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var mod = await LoadDraftAsync(db, modificationId, cancellationToken);
        var existing = await db.Set<ContractModificationChange>().Where(c => c.ModificationId == mod.Id).ToListAsync(cancellationToken);
        var before = ToDto(mod, existing, []);
        await ApplyAsync(db, mod, request, cancellationToken);
        var changes = ReplaceChanges(db, mod, request.Changes, existing);
        var after = ToDto(mod, changes, []);
        audit.Record(db, nameof(ContractModification), mod.Id, "DraftUpdated", before, after);
        await db.SaveChangesAsync(cancellationToken);
        return after;
    }

    public async Task DeleteDraftAsync(Guid modificationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var mod = await LoadDraftAsync(db, modificationId, cancellationToken);
        var existing = await db.Set<ContractModificationChange>().Where(c => c.ModificationId == mod.Id).ToListAsync(cancellationToken);
        audit.Record(db, nameof(ContractModification), mod.Id, "DraftDeleted", before: ToDto(mod, existing, []));
        db.Set<ContractModificationChange>().RemoveRange(existing);
        db.Set<ContractModification>().Remove(mod);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ContractModificationDto> ExecuteAsync(Guid modificationId, CancellationToken cancellationToken = default)
    {
        ContractModificationDto result;
        Guid contractId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var mod = await LoadDraftAsync(db, modificationId, cancellationToken);
            contractId = mod.ContractId;
            var contract = await db.Set<Contract>().FirstAsync(c => c.Id == mod.ContractId, cancellationToken);
            var clins = await ClinRules.LoadTrackedAsync(db, contract.Id, cancellationToken);
            var changes = await db.Set<ContractModificationChange>().AsNoTracking()
                .Where(c => c.ModificationId == mod.Id).ToListAsync(cancellationToken);
            if (changes.Count == 0)
            {
                throw new ArgumentException($"{mod.Number} has no changes to execute.");
            }

            var contractBefore = new { contract.CeilingValue, contract.FundedValue, contract.PeriodEnd };
            var now = clock.GetUtcNow().UtcDateTime;
            var funding = new List<FundingAction>();

            // Everything below only changes tracked entities; one SaveChanges at the end makes it all-or-nothing.
            foreach (var change in changes.OrderBy(c => c.Kind).ThenBy(c => c.Sequence))
            {
                var label = $"{mod.Number} change {change.Sequence + 1} ({Describe(change.Kind)})";
                try
                {
                    var action = await ApplyChangeAsync(db, mod, contract, clins, change, now, cancellationToken);
                    if (action is not null)
                    {
                        funding.Add(action);
                    }
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"{label}: {ex.Message}", ex);
                }
            }

            ClinRules.SyncTotals(db, audit, contract, clins, $"{mod.Number} executed");
            mod.Status = ModificationStatus.Executed;
            mod.ExecutedAtUtc = now;
            mod.ExecutedBy = audit.Actor;
            result = ToDto(mod, changes, funding);
            audit.Record(db, nameof(ContractModification), mod.Id, "Executed",
                new { Contract = contractBefore },
                new { Modification = result, Contract = new { contract.CeilingValue, contract.FundedValue, contract.PeriodEnd } });
            await db.SaveChangesAsync(cancellationToken);
        }

        // New funding re-arms the Limitation of Funds thresholds; check after the save so notices see the new totals.
        await notices.CheckAsync(contractId, cancellationToken);
        return result;
    }

    private async Task<FundingAction?> ApplyChangeAsync(
        IWorkbenchDbContext db, ContractModification mod, Contract contract, List<Clin> clins, ContractModificationChange change, DateTime now, CancellationToken ct)
    {
        Clin Find() => clins.FirstOrDefault(c => c.Number == ClinRules.Normalize(change.ClinNumber))
            ?? throw new ArgumentException($"CLIN {change.ClinNumber} isn't on {contract.ContractNumber}.");

        switch (change.Kind)
        {
            case DomainChangeKind.AddClin:
                var request = new SaveClinRequest(change.ClinNumber ?? "", change.ClinDescription ?? "",
                    (ClinPricingKind)(int)(change.Pricing ?? ClinPricing.FirmFixedPrice),
                    change.PeriodStart ?? contract.PeriodStart, change.PeriodEnd ?? contract.PeriodEnd, change.Amount ?? 0m, change.FeeRate, change.IsOption);
                var added = await ClinService.AddClinAsync(db, contract, clins, request, ct);
                audit.Record(db, nameof(Clin), added.Id, "Created", after: added.ToDto(clins), reason: mod.Number);
                return null;

            case DomainChangeKind.ExerciseOption:
                var option = Find();
                if (!option.IsOption)
                {
                    throw new ArgumentException($"CLIN {option.Number} isn't an option.");
                }

                if (option.IsExercised)
                {
                    throw new ArgumentException($"Option CLIN {option.Number} is already exercised.");
                }

                foreach (var c in clins.Where(c => c.Id == option.Id || ClinRules.ParentOf(c.Number) == option.Number))
                {
                    c.IsExercised = true;
                    audit.Record(db, nameof(Clin), c.Id, "OptionExercised", reason: mod.Number);
                }

                return null;

            case DomainChangeKind.ChangeClinValue:
                var valued = Find();
                if (ClinRules.HasSlins(valued, clins))
                {
                    throw new ArgumentException($"CLIN {valued.Number} rolls up its SLINs; change a SLIN's value instead.");
                }

                var delta = change.Amount ?? 0m;
                var ceiling = valued.CeilingValue + delta;
                if (delta == 0 || ceiling < 0)
                {
                    throw new ArgumentException($"Enter a non-zero change that leaves CLIN {valued.Number}'s value at zero or more.");
                }

                if (ceiling < valued.FundedValue)
                {
                    throw new ArgumentException($"CLIN {valued.Number}'s value can't drop below its funded value ({valued.FundedValue:N2}).");
                }

                audit.Record(db, nameof(Clin), valued.Id, "ValueChanged", new { valued.CeilingValue }, new { CeilingValue = ceiling }, mod.Number);
                valued.CeilingValue = ceiling;
                return null;

            case DomainChangeKind.ExtendPeriod:
                var end = change.NewPeriodEnd ?? throw new ArgumentException("Enter the new period-of-performance end.");
                if (change.ClinNumber is null or "")
                {
                    if (end <= contract.PeriodEnd)
                    {
                        throw new ArgumentException($"The new end must be after the contract's current end ({contract.PeriodEnd:yyyy-MM-dd}).");
                    }

                    contract.PeriodEnd = end;
                    return null;
                }

                var extended = Find();
                if (end <= extended.PeriodEnd)
                {
                    throw new ArgumentException($"The new end must be after CLIN {extended.Number}'s current end ({extended.PeriodEnd:yyyy-MM-dd}).");
                }

                audit.Record(db, nameof(Clin), extended.Id, "PeriodExtended", new { extended.PeriodEnd }, new { PeriodEnd = end }, mod.Number);
                foreach (var c in clins.Where(c => c.Id == extended.Id || (ClinRules.ParentOf(c.Number) == extended.Number && c.PeriodEnd == extended.PeriodEnd)))
                {
                    c.PeriodEnd = end;
                }

                if (end > contract.PeriodEnd)
                {
                    contract.PeriodEnd = end;
                }

                return null;

            case DomainChangeKind.Fund:
                var clin = Find();
                if (ClinRules.HasSlins(clin, clins))
                {
                    throw new ArgumentException($"CLIN {clin.Number} rolls up its SLINs; fund a SLIN instead.");
                }

                if (!clin.IsActive())
                {
                    throw new ArgumentException($"Option CLIN {clin.Number} isn't exercised, so it can't be funded. Exercise it first (in this or an earlier modification).");
                }

                var amount = change.Amount ?? 0m;
                var funded = clin.FundedValue + amount;
                if (amount == 0)
                {
                    throw new ArgumentException("Enter a non-zero funding amount.");
                }

                if (funded < 0)
                {
                    throw new ArgumentException($"De-obligating {-amount:N2} would leave CLIN {clin.Number}'s funding negative.");
                }

                if (funded > clin.CeilingValue)
                {
                    throw new ArgumentException($"CLIN {clin.Number}'s funding ({funded:N2}) can't exceed its value ({clin.CeilingValue:N2}).");
                }

                clin.FundedValue = funded;
                var action = new FundingAction
                {
                    ContractId = contract.Id,
                    ClinId = clin.Id,
                    ModificationId = mod.Id,
                    EffectiveDate = mod.EffectiveDate,
                    Amount = amount,
                    Description = string.IsNullOrWhiteSpace(change.Note) ? $"{mod.Number} {mod.Description}" : change.Note.Trim(),
                    CreatedAtUtc = now,
                    CreatedBy = audit.Actor,
                };
                db.Set<FundingAction>().Add(action);
                audit.Record(db, nameof(FundingAction), action.Id, "Created",
                    after: new { ClinNumber = clin.Number, action.Amount, FundedValue = funded, action.EffectiveDate }, reason: mod.Number);
                return action;

            default:
                throw new ArgumentException($"Unknown change kind {change.Kind}.");
        }
    }

    private static async Task<ContractModification> LoadDraftAsync(IWorkbenchDbContext db, Guid modificationId, CancellationToken ct)
    {
        var mod = await db.Set<ContractModification>().FirstOrDefaultAsync(m => m.Id == modificationId, ct)
            ?? throw WorkbenchNotFoundException.For("Modification", modificationId);
        if (mod.Status == ModificationStatus.Executed)
        {
            throw new ArgumentException($"{mod.Number} is executed and can't change; make a correction with another modification.");
        }

        return mod;
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, ContractModification mod, SaveModificationRequest request, CancellationToken ct)
    {
        var number = request.Number.Required("Modification number").ToUpperInvariant();
        if (number.Length > 16)
        {
            throw new ArgumentException("Modification numbers are at most 16 characters (for example P00002).");
        }

        if (await db.Set<ContractModification>().AnyAsync(m => m.ContractId == mod.ContractId && m.Id != mod.Id && m.Number == number, ct))
        {
            throw new ArgumentException($"Modification {number} already exists on this contract.");
        }

        foreach (var (change, i) in (request.Changes ?? []).Select((c, i) => (c, i)))
        {
            ValidateShape(change, i);
        }

        mod.Number = number;
        mod.EffectiveDate = request.EffectiveDate;
        mod.Description = request.Description.Required("Description");
    }

    /// <summary>Checks that each change has the fields its kind needs; whether it applies is checked on execution.</summary>
    private static void ValidateShape(ModificationChangeDto change, int index)
    {
        string? Problem() => change.Kind switch
        {
            ChangeKind.AddClin when string.IsNullOrWhiteSpace(change.ClinDescription) => "a new CLIN needs a description",
            ChangeKind.AddClin when change.Pricing is null => "a new CLIN needs a pricing type",
            ChangeKind.AddClin when change.Amount is null or < 0 => "a new CLIN needs a value of zero or more",
            ChangeKind.ExtendPeriod when change.NewPeriodEnd is null => "enter the new period-of-performance end",
            ChangeKind.ChangeClinValue or ChangeKind.Fund when change.Amount is null or 0 => "enter a non-zero amount",
            _ => null,
        };

        if (change.Kind != ChangeKind.ExtendPeriod || !string.IsNullOrWhiteSpace(change.ClinNumber))
        {
            try
            {
                ClinRules.Normalize(change.ClinNumber);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Change {index + 1}: {ex.Message}");
            }
        }

        if (Problem() is { } problem)
        {
            throw new ArgumentException($"Change {index + 1}: {problem}.");
        }
    }

    private static List<ContractModificationChange> ReplaceChanges(
        IWorkbenchDbContext db, ContractModification mod, IReadOnlyList<ModificationChangeDto>? requested, List<ContractModificationChange> existing)
    {
        db.Set<ContractModificationChange>().RemoveRange(existing);
        var rows = (requested ?? []).Select((c, i) => new ContractModificationChange
        {
            ModificationId = mod.Id,
            Sequence = i,
            Kind = (DomainChangeKind)(int)c.Kind,
            ClinNumber = string.IsNullOrWhiteSpace(c.ClinNumber) ? null : ClinRules.Normalize(c.ClinNumber),
            Amount = c.Amount,
            NewPeriodEnd = c.NewPeriodEnd,
            ClinDescription = c.ClinDescription?.Trim(),
            Pricing = c.Pricing is { } p ? (ClinPricing)(int)p : null,
            IsOption = c.IsOption,
            PeriodStart = c.PeriodStart,
            PeriodEnd = c.PeriodEnd,
            FeeRate = c.FeeRate,
            Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim(),
        }).ToList();
        db.Set<ContractModificationChange>().AddRange(rows);
        return rows;
    }

    private static async Task<ContractModificationDto> LoadDtoAsync(IWorkbenchDbContext db, ContractModification mod, CancellationToken ct)
    {
        var changes = await db.Set<ContractModificationChange>().AsNoTracking().Where(c => c.ModificationId == mod.Id).ToListAsync(ct);
        var funding = await db.Set<FundingAction>().AsNoTracking().Where(f => f.ModificationId == mod.Id).ToListAsync(ct);
        return ToDto(mod, changes, funding);
    }

    private static ContractModificationDto ToDto(ContractModification m, IEnumerable<ContractModificationChange> changes, IEnumerable<FundingAction> funding)
    {
        var mine = changes.Where(c => c.ModificationId == m.Id).OrderBy(c => c.Sequence).Select(ToDto).ToList();
        var delta = m.Status == ModificationStatus.Executed
            ? funding.Where(f => f.ModificationId == m.Id).Sum(f => f.Amount)
            : mine.Where(c => c.Kind == ChangeKind.Fund).Sum(c => c.Amount ?? 0m);
        return new ContractModificationDto(m.Id, m.ContractId, m.Number, m.EffectiveDate, m.Description, (ModificationState)(int)m.Status,
            m.ExecutedAtUtc, m.ExecutedBy, mine, delta);
    }

    private static ModificationChangeDto ToDto(ContractModificationChange c) =>
        new((ChangeKind)(int)c.Kind, c.ClinNumber, c.Amount, c.NewPeriodEnd, c.ClinDescription,
            c.Pricing is { } p ? (ClinPricingKind)(int)p : null, c.IsOption, c.PeriodStart, c.PeriodEnd, c.FeeRate, c.Note);

    private static string Describe(DomainChangeKind kind) => kind switch
    {
        DomainChangeKind.AddClin => "add CLIN",
        DomainChangeKind.ExerciseOption => "exercise option",
        DomainChangeKind.ChangeClinValue => "change value",
        DomainChangeKind.ExtendPeriod => "extend period",
        _ => "funding",
    };
}
