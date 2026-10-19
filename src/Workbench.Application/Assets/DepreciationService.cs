using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Assets;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Assets;

/// <summary>
/// Monthly depreciation per fiscal period. Each in-service, contractor-owned asset gets its scheduled depreciation through
/// the period less what's already posted (so a late addition catches up once), recorded as one <see cref="AssetDepreciation"/>
/// per asset and period; that uniqueness makes runs idempotent. One balanced journal entry per run.
/// </summary>
public sealed class DepreciationService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, GeneralLedger ledger, TimeProvider clock) : IDepreciationService
{
    public async Task<DepreciationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        var due = await DueAsync(db, period, cancellationToken);
        var lines = await PreviewLinesAsync(db, due, cancellationToken);
        var runs = (await RunsAsync(db, cancellationToken)).Where(r => r.FiscalPeriodId == period.Id).ToList();
        return new DepreciationPreviewDto(period.Id, period.Name, period.Status == PeriodStatus.Closed, lines, lines.Sum(l => l.Amount), runs);
    }

    public async Task<DepreciationRunDto?> RunAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        if (period.Status != PeriodStatus.Open)
        {
            throw new ArgumentException($"Fiscal period {period.Name} is closed; depreciation can't be posted into it.");
        }

        var due = await DueAsync(db, period, cancellationToken, track: true);
        if (due.Count == 0)
        {
            return null;
        }

        var run = new AssetDepreciationRun
        {
            FiscalPeriodId = period.Id,
            Total = due.Sum(d => d.Amount),
            AssetCount = due.Count,
            RunBy = audit.Actor,
            RunAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        var entry = new JournalEntry
        {
            Date = period.End,
            Source = JournalSource.Depreciation,
            SourceId = run.Id,
            Description = $"Depreciation for {period.Name} ({due.Count} asset{(due.Count == 1 ? "" : "s")})",
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };
        foreach (var (asset, amount) in due.OrderBy(d => d.Asset.TagNumber, StringComparer.Ordinal))
        {
            var memo = $"{asset.TagNumber} {asset.Description}";
            AssetService.AddLine(entry, asset.ExpenseAccountId, amount, 0, memo, asset.OrgUnitId, asset.ProjectId, asset.WbsElementId);
            AssetService.AddLine(entry, asset.AccumulatedDepreciationAccountId, 0, amount, memo, asset.OrgUnitId);

            // Each asset depreciates in its own company's ledger (a project line posts to the project's company).
            foreach (var line in entry.Lines.TakeLast(2).Where(l => l.ProjectId is null))
            {
                line.CompanyId = asset.CompanyId;
            }
            asset.AccumulatedDepreciation += amount;
            db.Set<AssetDepreciation>().Add(new AssetDepreciation { AssetId = asset.Id, FiscalPeriodId = period.Id, RunId = run.Id, PeriodStart = period.Start, Amount = amount });
        }

        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, cancellationToken);
        run.JournalEntryId = entry.Id;
        db.Set<AssetDepreciationRun>().Add(run);
        audit.Record(db, AssetSupport.RunEntity, run.Id, "Posted", after: new { Period = period.Name, run.Total, run.AssetCount, Entry = entry.Number });
        await db.SaveChangesAsync(cancellationToken);
        return (await RunsAsync(db, cancellationToken)).First(r => r.Id == run.Id);
    }

    public async Task<IReadOnlyList<DepreciationRunDto>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RunsAsync(db, cancellationToken);
    }

    /// <summary>Assets with depreciation still to post for the period (none already posted for it), and the amount.</summary>
    internal static async Task<List<(FixedAsset Asset, decimal Amount)>> DueAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct, bool track = false)
    {
        var done = (await db.Set<AssetDepreciation>().AsNoTracking().Where(d => d.FiscalPeriodId == period.Id).Select(d => d.AssetId).ToListAsync(ct)).ToHashSet();
        var q = track ? db.Set<FixedAsset>() : db.Set<FixedAsset>().AsNoTracking();
        var assets = await q.Where(a => a.Status == AssetStatus.InService && !a.IsGovernmentFurnished && !a.IsContractorAcquired && a.InServiceDate <= period.End)
            .ToListAsync(ct);
        return assets.Where(a => !done.Contains(a.Id)).Select(a => (a, AssetSupport.DueFor(a, period))).Where(x => x.Item2 > 0).ToList();
    }

    private static async Task<IReadOnlyList<DepreciationPreviewLineDto>> PreviewLinesAsync(IWorkbenchDbContext db, List<(FixedAsset Asset, decimal Amount)> due, CancellationToken ct)
    {
        var names = await AssetQueries.Names.LoadAsync(db, due.Select(d => d.Asset).ToList(), ct);
        var accountIds = due.Select(d => d.Asset.ExpenseAccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Number, ct);
        return due.OrderBy(d => d.Asset.TagNumber, StringComparer.Ordinal).Select(d => new DepreciationPreviewLineDto(d.Asset.Id, d.Asset.TagNumber, d.Asset.Description,
            accounts.GetValueOrDefault(d.Asset.ExpenseAccountId) ?? "", names.Org(d.Asset.OrgUnitId), names.Project(d.Asset.ProjectId), d.Amount)).ToList();
    }

    private static async Task<IReadOnlyList<DepreciationRunDto>> RunsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var runs = await db.Set<AssetDepreciationRun>().AsNoTracking().ToListAsync(ct);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var entryIds = runs.Where(r => r.JournalEntryId != null).Select(r => r.JournalEntryId!.Value).ToList();
        var entries = await db.Set<JournalEntry>().AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
        return runs.OrderByDescending(r => periods.GetValueOrDefault(r.FiscalPeriodId)?.Start).ThenByDescending(r => r.RunAtUtc)
            .Select(r => new DepreciationRunDto(r.Id, r.FiscalPeriodId, periods.GetValueOrDefault(r.FiscalPeriodId)?.Name ?? "", r.Total, r.AssetCount,
                r.JournalEntryId, r.JournalEntryId is { } e ? entries.GetValueOrDefault(e) : null, r.RunBy, r.RunAtUtc)).ToList();
    }

    private static async Task<FiscalPeriod> FindPeriodAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Fiscal period", id);
}

/// <summary>Period close: every in-service asset's depreciation for the period is posted.</summary>
public sealed class DepreciationCloseCheck : IPeriodCloseCheck
{
    public async Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken cancellationToken)
    {
        const string title = "Depreciation posted";
        var due = await DepreciationService.DueAsync(db, period, cancellationToken);
        if (due.Count == 0)
        {
            var posted = (await db.Set<AssetDepreciation>().AsNoTracking().Where(d => d.FiscalPeriodId == period.Id).Select(d => d.Amount).ToListAsync(cancellationToken)).Sum();
            return new CloseCheckDto(CloseCheckKind.DepreciationPosted, title, true, 0,
                posted == 0 ? "No depreciation due" : $"Depreciation posted ({posted:N2})", []);
        }

        return new CloseCheckDto(CloseCheckKind.DepreciationPosted, title, false, due.Count,
            $"{due.Count} asset(s) not depreciated for {period.Name} ({due.Sum(d => d.Amount):N2})",
            due.Take(25).Select(d => $"{d.Asset.TagNumber} {d.Asset.Description}: {d.Amount:N2}").Append("Run it under Fixed assets › Depreciation.").ToList());
    }
}
