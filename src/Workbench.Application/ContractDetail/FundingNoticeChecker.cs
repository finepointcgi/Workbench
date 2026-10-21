using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ContractDetail;

/// <summary>
/// Limitation of Funds / Limitation of Cost notices: when incurred plus committed cost on a CLIN reaches a threshold of
/// its funded value, notify the managers of the projects charging it and Finance, once per CLIN, threshold and funded value.
/// </summary>
public sealed class FundingNoticeChecker(
    IWorkbenchDbContextFactory dbFactory, ClinFundingCalculator calculator, INotificationPublisher notifications, TimeProvider clock)
{
    public const string NotificationKind = "funding.limitation";

    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public async Task<IReadOnlyList<FundingNoticeDto>> CheckAsync(Guid contractId, CancellationToken ct) =>
        (await CheckWithStatusAsync(contractId, ct)).Sent;

    /// <summary>The notices sent, and whether the check used burdened cost (it falls back to direct cost when it can't).</summary>
    internal async Task<(IReadOnlyList<FundingNoticeDto> Sent, bool IsBurdened)> CheckWithStatusAsync(Guid contractId, CancellationToken ct)
    {
        var status = await calculator.ComputeAsync(contractId, ct);
        return (await SendDueAsync(contractId, status, ct), status.IsBurdened);
    }

    private async Task<IReadOnlyList<FundingNoticeDto>> SendDueAsync(Guid contractId, ContractFundingStatusDto status, CancellationToken ct)
    {
        var thresholds = new[] { calculator.Options.WarningThreshold, calculator.Options.CriticalThreshold }
            .Where(t => t > 0).Distinct().Order().ToList();

        await using var db = await dbFactory.CreateAsync(ct);
        var contract = await db.Set<Contract>().AsNoTracking().FirstAsync(c => c.Id == contractId, ct);
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(ct);
        var leaves = clins.Where(c => !ClinRules.HasSlins(c, clins)).Select(c => c.Id).ToHashSet();
        var sent = await db.Set<ClinFundingNotice>().AsNoTracking().Where(n => n.ContractId == contractId).ToListAsync(ct);

        var created = new List<(ClinFundingNotice Notice, string Body, List<string> To)>();
        foreach (var row in status.Clins.Where(r => leaves.Contains(r.ClinId) && r.Level != FundingLevel.NotActive && r.FundedValue > 0))
        {
            var exposure = row.Incurred + row.Committed;
            var clin = clins.First(c => c.Id == row.ClinId);
            foreach (var threshold in thresholds.Where(t => exposure >= row.FundedValue * t))
            {
                var percent = (int)Math.Round(threshold * 100);
                if (sent.Any(n => n.ClinId == row.ClinId && n.ThresholdPercent == percent && n.FundedValue == row.FundedValue)
                    || created.Any(c => c.Notice.ClinId == row.ClinId && c.Notice.ThresholdPercent == percent))
                {
                    continue;
                }

                var recipients = await RecipientsAsync(db, contractId, row.ClinId, ct);
                var title = $"{NoticeName(clin)}: {contract.ContractNumber} CLIN {row.Number} at {percent}% of funding";
                var body = string.Create(Us,
                    $"{exposure:C0} incurred and committed of {row.FundedValue:C0} funded ({exposure / row.FundedValue:P1}). " +
                    $"Notify the contracting officer and request funding before work continues past the funded amount.");
                created.Add((new ClinFundingNotice
                {
                    ContractId = contractId,
                    ClinId = row.ClinId,
                    ThresholdPercent = percent,
                    FundedValue = row.FundedValue,
                    IncurredAndCommitted = exposure,
                    Title = title,
                    Recipients = string.Join(",", recipients),
                    SentAtUtc = clock.GetUtcNow().UtcDateTime,
                }, body, recipients));
            }
        }

        if (created.Count == 0)
        {
            return [];
        }

        db.Set<ClinFundingNotice>().AddRange(created.Select(c => c.Notice));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another check recorded the same notice first (unique per CLIN, threshold and funded value).
            return [];
        }

        foreach (var (notice, body, to) in created)
        {
            await notifications.PublishAsync(to, NotificationKind, notice.Title, body, link: $"setup/contracts/{contractId}?tab=funding");
        }

        return created.Select(c => ToDto(c.Notice, clins)).ToList();
    }

    /// <summary>Limitation of Cost for fully funded cost-type CLINs, Limitation of Funds for incrementally funded ones.</summary>
    public static string NoticeName(Clin clin) => clin.Pricing switch
    {
        ClinPricing.CostPlusFixedFee or ClinPricing.CostPlusAwardFee or ClinPricing.CostPlusIncentiveFee or ClinPricing.CostNoFee =>
            clin.FundedValue >= clin.CeilingValue ? "Limitation of Cost" : "Limitation of Funds",
        _ => "Funding threshold",
    };

    public static FundingNoticeDto ToDto(ClinFundingNotice n, IEnumerable<Clin> clins) =>
        new(n.Id, n.ContractId, n.ClinId, clins.FirstOrDefault(c => c.Id == n.ClinId)?.Number ?? "?", n.Title, n.ThresholdPercent,
            n.FundedValue, n.IncurredAndCommitted, n.SentAtUtc, n.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Managers of the projects whose WBS charges the CLIN (every project on the contract when none do), plus Finance.</summary>
    private static async Task<List<string>> RecipientsAsync(IWorkbenchDbContext db, Guid contractId, Guid clinId, CancellationToken ct)
    {
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contractId).ToListAsync(ct);
        var projectIds = projects.Select(p => p.Id).ToList();
        var charging = await db.Set<WbsElement>().AsNoTracking()
            .Where(w => w.ClinId == clinId && projectIds.Contains(w.ProjectId)).Select(w => w.ProjectId).Distinct().ToListAsync(ct);
        var managerIds = projects.Where(p => charging.Count == 0 || charging.Contains(p.Id))
            .Select(p => p.ProjectManagerId).OfType<Guid>().Distinct().ToList();
        var managers = await db.Set<Employee>().AsNoTracking().Where(e => managerIds.Contains(e.Id)).Select(e => e.Email).ToListAsync(ct);
        var finance = await Identity.RoleStore.EmailsWithPermissionAsync(db, Permission.SetupContracts, ct, includeAdmins: false);
        return managers.Concat(finance).Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// Re-checks every active contract with CLINs hourly, so newly approved cost raises notices without a page visit. Runs as
/// Workbench System (<see cref="SystemExecution"/>), once per tenant, which may read burdened cost, so notices use the
/// same burdened numbers the funding tab shows.
/// </summary>
public sealed class FundingNoticeBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<FundingNoticeBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = SystemExecution.CreateScope(scopes);
                    var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(ct);
                    List<Guid> contracts;
                    await using (db)
                    {
                        contracts = await db.Set<Clin>().AsNoTracking()
                            .Join(db.Set<Contract>().AsNoTracking().Where(c => c.Status == ContractStatus.Active), c => c.ContractId, k => k.Id, (c, _) => c.ContractId)
                            .Distinct().ToListAsync(ct);
                    }

                    var checker = scope.ServiceProvider.GetRequiredService<FundingNoticeChecker>();
                    var burdened = 0;
                    foreach (var id in contracts)
                    {
                        var (sent, isBurdened) = await checker.CheckWithStatusAsync(id, ct);
                        burdened += isBurdened ? 1 : 0;
                        if (sent.Count > 0)
                        {
                            logger.LogInformation("Sent {Count} funding notices for contract {ContractId}", sent.Count, id);
                        }
                    }

                    logger.LogInformation("Funding notice check as {Actor}: {Contracts} contracts, {Burdened} on burdened cost, {Direct} on direct cost",
                        SystemExecutionContext.Actor, contracts.Count, burdened, contracts.Count - burdened);
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Funding notice check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
