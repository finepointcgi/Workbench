using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Contracts;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ContractDetail;

public sealed class ClinService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit) : IClinService
{
    public async Task<IReadOnlyList<ClinDto>> ListClinsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(cancellationToken);
        return ClinRules.InTreeOrder(clins).Select(c => c.ToDto(clins)).ToList();
    }

    public async Task<ClinDto?> GetClinAsync(Guid clinId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var clin = await db.Set<Clin>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == clinId, cancellationToken);
        if (clin is null)
        {
            return null;
        }

        var siblings = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == clin.ContractId).ToListAsync(cancellationToken);
        return clin.ToDto(siblings);
    }

    public async Task<ClinDto> CreateClinAsync(Guid contractId, SaveClinRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contract = await db.Set<Contract>().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        var clins = await ClinRules.LoadTrackedAsync(db, contractId, cancellationToken);
        var clin = await AddClinAsync(db, contract, clins, request, cancellationToken);
        audit.Record(db, nameof(Clin), clin.Id, "Created", after: clin.ToDto(clins));
        ClinRules.SyncTotals(db, audit, contract, clins, $"CLIN {clin.Number} added");
        await db.SaveChangesAsync(cancellationToken);
        return clin.ToDto(clins);
    }

    public async Task<ClinDto> UpdateClinAsync(Guid clinId, SaveClinRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var clin = await db.Set<Clin>().FirstOrDefaultAsync(c => c.Id == clinId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("CLIN", clinId);
        var contract = await db.Set<Contract>().FirstAsync(c => c.Id == clin.ContractId, cancellationToken);
        var clins = await ClinRules.LoadTrackedAsync(db, clin.ContractId, cancellationToken);
        clin = clins.First(c => c.Id == clinId);
        var before = clin.ToDto(clins);

        if (ClinRules.Normalize(request.Number) != clin.Number)
        {
            throw new ArgumentException("A CLIN's number can't change; add a new CLIN instead.");
        }

        ValidateFields(request);
        var hasSlins = ClinRules.HasSlins(clin, clins);
        var familyIds = clins.Where(c => c.Id == clin.Id || ClinRules.ParentOf(c.Number) == clin.Number).Select(c => c.Id).ToList();
        var funded = await db.Set<FundingAction>().AnyAsync(f => familyIds.Contains(f.ClinId), cancellationToken);
        if (!hasSlins && request.CeilingValue != clin.CeilingValue)
        {
            if (funded)
            {
                throw new ArgumentException($"CLIN {clin.Number} is funded; change its value through a modification.");
            }

            clin.CeilingValue = request.CeilingValue;
        }

        if (request.IsOption != clin.IsOption)
        {
            if (funded || ClinRules.ParentOf(clin.Number) is not null)
            {
                throw new ArgumentException($"CLIN {clin.Number} can't change between option and base once funded, or as a SLIN.");
            }

            clin.IsOption = request.IsOption;
            foreach (var c in clins.Where(c => familyIds.Contains(c.Id)))
            {
                (c.IsOption, c.IsExercised) = (request.IsOption, !request.IsOption);
            }
        }

        clin.Description = request.Description.Required("Description");
        clin.Pricing = (ClinPricing)(int)request.Pricing;
        clin.FeeRate = request.FeeRate;
        clin.PeriodStart = request.PeriodStart;
        clin.PeriodEnd = request.PeriodEnd;
        audit.Record(db, nameof(Clin), clin.Id, "Updated", before, clin.ToDto(clins));
        ClinRules.SyncTotals(db, audit, contract, clins, $"CLIN {clin.Number} edited");
        await db.SaveChangesAsync(cancellationToken);
        return clin.ToDto(clins);
    }

    public async Task<IReadOnlyList<ClinDto>> ListClinsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        if (project.ContractId is not { } contractId)
        {
            return [];
        }

        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(cancellationToken);
        return ClinRules.InTreeOrder(clins).Where(c => !ClinRules.HasSlins(c, clins)).Select(c => c.ToDto(clins)).ToList();
    }

    public async Task<IReadOnlyList<WbsClinMappingDto>> ListWbsMappingsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        return await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId)
            .Select(w => new WbsClinMappingDto(w.Id, w.ClinId)).ToListAsync(cancellationToken);
    }

    public async Task<WbsClinMappingDto> SetWbsClinAsync(Guid wbsElementId, Guid? clinId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var element = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Id == wbsElementId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("WBS element", wbsElementId);
        if (clinId is { } id)
        {
            var project = await db.Set<Project>().AsNoTracking().FirstAsync(p => p.Id == element.ProjectId, cancellationToken);
            var clin = await db.Set<Clin>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("CLIN", id);
            if (project.ContractId != clin.ContractId)
            {
                throw new ArgumentException($"CLIN {clin.Number} isn't on project {project.Code}'s contract.");
            }

            var siblings = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == clin.ContractId).ToListAsync(cancellationToken);
            if (ClinRules.HasSlins(clin, siblings))
            {
                throw new ArgumentException($"CLIN {clin.Number} has SLINs; map the WBS element to one of them.");
            }
        }

        if (element.ClinId != clinId)
        {
            var before = new WbsClinMappingDto(element.Id, element.ClinId);
            element.ClinId = clinId;
            audit.Record(db, nameof(WbsElement), element.Id, "ClinMapped", before, new WbsClinMappingDto(element.Id, clinId));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new WbsClinMappingDto(element.Id, element.ClinId);
    }

    /// <summary>Validates and adds a CLIN or SLIN to <paramref name="clins"/> (tracked); shared with modification execution.</summary>
    internal static async Task<Clin> AddClinAsync(IWorkbenchDbContext db, Contract contract, List<Clin> clins, SaveClinRequest request, CancellationToken ct)
    {
        var number = ClinRules.Normalize(request.Number);
        ValidateFields(request);
        if (clins.Any(c => c.Number == number))
        {
            throw new ArgumentException($"CLIN {number} already exists on {contract.ContractNumber}.");
        }

        var isOption = request.IsOption;
        if (ClinRules.ParentOf(number) is { } parentNumber)
        {
            var parent = clins.FirstOrDefault(c => c.Number == parentNumber)
                ?? throw new ArgumentException($"SLIN {number} needs CLIN {parentNumber} first.");
            if (!ClinRules.HasSlins(parent, clins))
            {
                var parentFunded = parent.FundedValue != 0 || await db.Set<FundingAction>().AnyAsync(f => f.ClinId == parent.Id, ct);
                var parentMapped = await db.Set<WbsElement>().AnyAsync(w => w.ClinId == parent.Id, ct);
                if (parentFunded || parentMapped)
                {
                    throw new ArgumentException($"CLIN {parentNumber} already has funding or WBS mapped to it, so it can't be split into SLINs.");
                }
            }

            // A SLIN of an option is part of that option.
            isOption |= parent.IsOption;
        }

        var clin = new Clin
        {
            ContractId = contract.Id,
            Number = number,
            Description = request.Description.Required("Description"),
            Pricing = (ClinPricing)(int)request.Pricing,
            IsOption = isOption,
            IsExercised = !isOption,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            CeilingValue = request.CeilingValue,
            FeeRate = request.FeeRate,
            Order = clins.Count,
        };
        db.Set<Clin>().Add(clin);
        clins.Add(clin);
        return clin;
    }

    private static void ValidateFields(SaveClinRequest request)
    {
        if (request.PeriodEnd < request.PeriodStart)
        {
            throw new ArgumentException("A CLIN's period of performance can't end before it starts.");
        }

        if (request.CeilingValue < 0)
        {
            throw new ArgumentException("A CLIN's value can't be negative.");
        }

        if (request.FeeRate is { } fee && (fee < 0 || fee >= 1))
        {
            throw new ArgumentException("Fee rate is a fraction between 0 and 1 (0.07 = 7%).");
        }
    }
}
