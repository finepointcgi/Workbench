using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Contracts;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ContractDetail;

public sealed class FundingService(IWorkbenchDbContextFactory dbFactory, ClinFundingCalculator calculator, FundingNoticeChecker notices) : IFundingService
{
    public async Task<IReadOnlyList<FundingActionDto>> ListFundingHistoryAsync(Guid contractId, Guid? clinId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToDictionaryAsync(c => c.Id, c => c.Number, cancellationToken);
        var mods = await db.Set<ContractModification>().AsNoTracking().Where(m => m.ContractId == contractId).ToDictionaryAsync(m => m.Id, m => m.Number, cancellationToken);
        var actions = await db.Set<FundingAction>().AsNoTracking().Where(f => f.ContractId == contractId).ToListAsync(cancellationToken);

        var running = new Dictionary<Guid, decimal>();
        var rows = new List<FundingActionDto>();
        foreach (var f in actions.OrderBy(f => f.EffectiveDate).ThenBy(f => f.CreatedAtUtc).ThenBy(f => f.Id))
        {
            var total = running[f.ClinId] = running.GetValueOrDefault(f.ClinId) + f.Amount;
            if (clinId is null || clinId == f.ClinId)
            {
                rows.Add(new FundingActionDto(f.Id, f.ContractId, f.ClinId, clins.GetValueOrDefault(f.ClinId, "?"), f.ModificationId,
                    f.ModificationId is { } m ? mods.GetValueOrDefault(m) : null, f.EffectiveDate, f.Amount, total, f.Description, f.CreatedAtUtc, f.CreatedBy));
            }
        }

        return rows;
    }

    public async Task<ContractFundingStatusDto> GetFundingStatusAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        var status = await calculator.ComputeAsync(contractId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(cancellationToken);
        var sent = await db.Set<ClinFundingNotice>().AsNoTracking().Where(n => n.ContractId == contractId).ToListAsync(cancellationToken);
        return status with { Notices = sent.OrderByDescending(n => n.SentAtUtc).Select(n => FundingNoticeChecker.ToDto(n, clins)).ToList() };
    }

    public async Task<ContractFundingStatusDto?> GetProjectFundingStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        Guid contractId;
        HashSet<Guid> mapped;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Project", projectId);
            if (project.ContractId is not { } id)
            {
                return null;
            }

            contractId = id;
            mapped = (await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId && w.ClinId != null)
                .Select(w => w.ClinId!.Value).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        }

        var status = await GetFundingStatusAsync(contractId, cancellationToken);
        return status with
        {
            Clins = status.Clins.Where(c => mapped.Contains(c.ClinId)).ToList(),
            Notices = status.Notices.Where(n => mapped.Contains(n.ClinId)).ToList(),
        };
    }

    public Task<IReadOnlyList<FundingNoticeDto>> CheckNoticesAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        notices.CheckAsync(contractId, cancellationToken);
}
