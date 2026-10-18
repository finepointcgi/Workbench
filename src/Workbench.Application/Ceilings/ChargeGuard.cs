using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Contracts.Ceilings;
using Workbench.Domain.Ceilings;
using Workbench.Domain.Closeout;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Ceilings;

/// <summary>
/// The checks the enforcement decorators share: closed contracts and projects refuse charges, blocking ceilings refuse
/// increases past them, and threshold alerts go out after a change is saved.
/// </summary>
public sealed class ChargeGuard(
    IWorkbenchDbContextFactory dbFactory,
    INotificationPublisher notifications,
    AccessControl access,
    TimeProvider clock,
    ILogger<ChargeGuard> logger)
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>Throws <see cref="ChargingClosedException"/> when any WBS element belongs to a closed project or contract.</summary>
    internal async Task EnsureOpenAsync(IWorkbenchDbContext db, IEnumerable<Guid> wbsIds, string what, CancellationToken ct)
    {
        var ids = wbsIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var projects = await (
                from w in db.Set<WbsElement>().AsNoTracking()
                join p in db.Set<Project>().AsNoTracking() on w.ProjectId equals p.Id
                where ids.Contains(w.Id)
                select new { p.Id, p.Code, p.ContractId })
            .Distinct().ToListAsync(ct);
        var projectIds = projects.Select(p => p.Id).ToList();
        var contractIds = projects.Where(p => p.ContractId != null).Select(p => p.ContractId!.Value).ToList();
        var closed = await db.Set<CloseoutRecord>().AsNoTracking()
            .Where(c => c.Status == CloseoutStatus.Closed
                && ((c.ProjectId != null && projectIds.Contains(c.ProjectId.Value)) || (c.ContractId != null && contractIds.Contains(c.ContractId.Value))))
            .ToListAsync(ct);
        if (closed.FirstOrDefault(c => c.ContractId != null) is { } contract)
        {
            var number = await db.Set<Contract>().AsNoTracking().Where(c => c.Id == contract.ContractId).Select(c => c.ContractNumber).FirstAsync(ct);
            throw new ChargingClosedException($"Contract {number} is closed, so {what} can't be charged to it. Finance can reopen the contract's closeout.");
        }

        if (closed.FirstOrDefault() is { } project)
        {
            var code = projects.First(p => p.Id == project.ProjectId).Code;
            throw new ChargingClosedException($"Project {code} is closed, so {what} can't be charged to it. Finance can reopen the project's closeout.");
        }
    }

    internal async Task EnsureOpenAsync(IEnumerable<Guid> wbsIds, string what, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        await EnsureOpenAsync(db, wbsIds, what, ct);
    }

    /// <summary>Checks the closed state and then the ceilings; returns warnings, throws for a closed charge or a blocking ceiling.</summary>
    internal async Task<List<string>> CheckChargesAsync(IReadOnlyCollection<ChargeDelta> deltas, string what, CancellationToken ct)
    {
        var increases = deltas.Where(d => d.Hours > 0 || d.Cost > 0).ToList();
        if (increases.Count == 0)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(ct);
        await EnsureOpenAsync(db, increases.Select(d => d.WbsElementId), what, ct);
        var states = await CeilingEngine.ForWbsAsync(db, increases.Select(d => d.WbsElementId).Distinct().ToList(), UtcNow, ct);
        return CeilingEngine.CheckCharges(states, deltas, what);
    }

    internal async Task<List<string>> CheckBillingAsync(Guid contractId, IReadOnlyCollection<BillingDelta> deltas, string what, CancellationToken ct)
    {
        if (deltas.Sum(d => d.Amount) <= 0 && deltas.All(d => d.Amount <= 0))
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var states = await CeilingEngine.ForBillingAsync(db, contractId, UtcNow, ct);
        return CeilingEngine.CheckBilling(states, deltas, what);
    }

    /// <summary>After a change was saved: alerts for the ceilings covering these WBS elements. Never throws.</summary>
    internal async Task AlertForWbsAsync(IEnumerable<Guid> wbsIds, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var states = await CeilingEngine.ForWbsAsync(db, wbsIds.Distinct().ToList(), UtcNow, ct);
            await AlertAsync(db, states, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ceiling alert check failed");
        }
    }

    internal async Task AlertForContractAsync(Guid contractId, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateAsync(ct);
            await AlertAsync(db, await CeilingEngine.ForContractAsync(db, contractId, false, UtcNow, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ceiling alert check failed");
        }
    }

    /// <summary>
    /// Sends one alert per ceiling, measure, threshold and ceiling amount, the first time consumption reaches it, to Finance and
    /// administrators and the managers of the projects the ceiling covers (not to whoever made the change).
    /// </summary>
    internal async Task<int> AlertAsync(IWorkbenchDbContext db, IReadOnlyList<CeilingState> states, CancellationToken ct)
    {
        var ids = states.Select(s => s.Ceiling.Id).ToList();
        var sent = await db.Set<CeilingAlert>().AsNoTracking().Where(a => ids.Contains(a.CeilingId)).ToListAsync(ct);
        var pending = new List<(CeilingState State, CeilingMeasure Measure, int Threshold, decimal Limit, decimal Consumed)>();
        foreach (var s in states.Where(s => s.Ceiling.IsActive))
        {
            var thresholds = CeilingEngine.Thresholds(s.Ceiling.AlertThresholds);
            foreach (var m in CeilingEngine.Measures)
            {
                if (s.Limit(m) is not { } limit || limit <= 0)
                {
                    continue;
                }

                var consumed = s.Consumed(m);
                var percent = consumed / limit * 100m;
                var crossed = thresholds.Where(t => percent >= t && !sent.Any(a => a.CeilingId == s.Ceiling.Id && a.Measure == m && a.ThresholdPercent == t && a.Limit == limit)).ToList();
                if (crossed.Count == 0)
                {
                    continue;
                }

                foreach (var t in crossed)
                {
                    db.Set<CeilingAlert>().Add(new CeilingAlert { CeilingId = s.Ceiling.Id, Measure = m, ThresholdPercent = t, Limit = limit, Consumed = consumed, SentAtUtc = UtcNow });
                }

                pending.Add((s, m, crossed.Max(), limit, consumed));
            }
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        await db.SaveChangesAsync(ct);
        var finance = await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive && (u.IsFinance || u.IsAdmin)).Select(u => u.Email).ToListAsync(ct);
        foreach (var (s, m, threshold, limit, consumed) in pending)
        {
            var projectIds = s.Projects.ToList();
            var managers = await (
                    from p in db.Set<Project>().AsNoTracking()
                    join e in db.Set<Employee>().AsNoTracking() on p.ProjectManagerId equals e.Id
                    where projectIds.Contains(p.Id)
                    select e.Email)
                .ToListAsync(ct);
            var recipients = finance.Concat(managers).Where(e => !string.Equals(AccessControl.Normalize(e), access.Email, StringComparison.Ordinal)).ToList();
            var link = s.Ceiling.ContractId is { } c ? $"setup/contracts/{c}?tab=ceilings" : $"projects/{s.Ceiling.ProjectId}/financials";
            await notifications.PublishAsync(recipients, "ceiling.threshold",
                $"{s.Label} is at {threshold}% of its {CeilingEngine.Noun(m)} ceiling",
                $"{CeilingEngine.Format(m, consumed)} of {CeilingEngine.Format(m, limit)} ({(s.Ceiling.Enforcement == CeilingEnforcement.Block ? "blocking" : "warning")} ceiling)",
                link: link);
        }

        return pending.Count;
    }
}
