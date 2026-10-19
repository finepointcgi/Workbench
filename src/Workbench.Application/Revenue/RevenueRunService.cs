using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Revenue;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Revenue;

namespace Workbench.Application.Revenue;

/// <summary>
/// The monthly revenue run. Each project with a contract gets at most one posted <see cref="RevenueRun"/> per fiscal
/// period (enforced by a unique key), with one balanced entry (source Revenue, source id = the run) dated the period end.
/// Runs are cumulative, so they go in period order: a period can't be posted for a project that has a later posted run,
/// and only a project's latest run can be reversed. Closed periods are refused.
/// </summary>
public sealed class RevenueRunService(IWorkbenchDbContextFactory dbFactory, RevenueEngine engine, GeneralLedger ledger, AuditRecorder audit) : IRevenueRunService
{
    public async Task<RevenuePreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await RevenueSupport.FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        var posted = (await RevenueSupport.RunDtosAsync(db, db.Set<RevenueRun>().Where(r => r.FiscalPeriodId == period.Id && r.Status == RevenueRunStatus.Posted), cancellationToken))
            .ToDictionary(r => r.ProjectId);
        var names = await RevenueSupport.Names.LoadAsync(db, [], cancellationToken);
        var rows = new List<RevenuePreviewRowDto>();
        foreach (var (projectId, result) in await engine.ComputeManyAsync(dbFactory, await ProjectIdsAsync(db, cancellationToken), period, cancellationToken))
        {
            if (result is not { } computed)
            {
                continue;
            }

            var run = posted.GetValueOrDefault(projectId);
            if (run is null && !computed.HasEntry && computed.RecognizedToDate == 0 && computed.BilledToDate == 0)
            {
                continue; // Nothing earned, billed or posted: leave it off the preview.
            }

            rows.Add(new RevenuePreviewRowDto(await ToDtoAsync(db, computed, names, cancellationToken), run));
        }

        return new RevenuePreviewDto(period.Id, period.Name, period.End, period.Status == PeriodStatus.Closed, rows);
    }

    public async Task<PostRevenueRunResultDto> PostAsync(Guid fiscalPeriodId, PostRevenueRunRequest request, CancellationToken cancellationToken = default)
    {
        await using (var setupDb = await dbFactory.CreateAsync(cancellationToken))
        {
            await RevenueSupport.EnsureAccountsAsync(setupDb, ledger.UtcNow, cancellationToken);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await RevenueSupport.FindPeriodAsync(db, fiscalPeriodId, cancellationToken);
        if (period.Status != PeriodStatus.Open)
        {
            throw new ArgumentException($"Fiscal period {period.Name} is closed; revenue can't be posted into it.");
        }

        var projectIds = request.ProjectIds is { Count: > 0 } chosen ? chosen.Distinct().ToList() : await ProjectIdsAsync(db, cancellationToken);
        var posted = new List<Guid>();
        var skipped = new List<string>();
        foreach (var projectId in projectIds)
        {
            var project = await RevenueSupport.FindProjectAsync(db, projectId, cancellationToken);
            if (await db.Set<RevenueRun>().AnyAsync(r => r.PostedKey == RevenueRun.KeyFor(projectId, period.Id), cancellationToken))
            {
                skipped.Add($"{project.Code}: already posted for {period.Name}. Reverse it to run again.");
                continue;
            }

            if (await LaterRunAsync(db, projectId, period, cancellationToken) is { } later)
            {
                skipped.Add($"{project.Code}: has a posted run for {later}; reverse later runs first.");
                continue;
            }

            var computed = await engine.ComputeAsync(db, projectId, period, cancellationToken);
            if (computed is null)
            {
                skipped.Add($"{project.Code}: not on a contract.");
                continue;
            }

            if (!computed.HasEntry)
            {
                if (request.ProjectIds is { Count: > 0 })
                {
                    skipped.Add($"{project.Code}: nothing to post; the ledger already matches.");
                }

                continue;
            }

            posted.Add(await PostOneAsync(db, computed, cancellationToken));
        }

        var runs = await RevenueSupport.RunDtosAsync(db, db.Set<RevenueRun>().Where(r => posted.Contains(r.Id)), cancellationToken);
        return new PostRevenueRunResultDto(runs, skipped);
    }

    private async Task<Guid> PostOneAsync(IWorkbenchDbContext db, ProjectComputation c, CancellationToken ct)
    {
        var run = new RevenueRun
        {
            FiscalPeriodId = c.Period.Id,
            ProjectId = c.Project.Id,
            PostedKey = RevenueRun.KeyFor(c.Project.Id, c.Period.Id),
            RecognizedToDate = c.RecognizedToDate,
            PeriodRevenue = c.PeriodRevenue,
            BilledToDate = c.BilledToDate,
            RevenueAdjustment = c.RevenueAdjustment,
            UnbilledBalance = c.Unbilled,
            BillingsInExcessBalance = c.BillingsInExcess,
            LossProvisionBalance = c.LossProvision,
            PostedBy = ledger.Actor,
            PostedAtUtc = ledger.UtcNow,
        };
        foreach (var u in c.Units)
        {
            run.Lines.Add(new RevenueRunLine
            {
                RunId = run.Id,
                ClinId = u.ClinId,
                Method = u.Method,
                CostToDate = u.CostToDate,
                EstimatedCost = u.EstimatedCost,
                EstimatedRevenue = u.EstimatedRevenue,
                PercentComplete = u.PercentComplete,
                RecognizedToDate = u.Recognized,
                BilledToDate = u.Billed,
                ExpectedLoss = u.ExpectedLoss,
                LossProvision = u.LossProvision,
                Note = u.Note is null ? null : RevenueSupport.Truncate(u.Note, 1000),
            });
        }

        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Number, a => a.Id, ct);
        var entry = new JournalEntry
        {
            Date = c.Period.End,
            Source = JournalSource.Revenue,
            SourceId = run.Id,
            Description = RevenueSupport.Truncate($"Revenue recognition {c.Period.Name} · {c.Project.Code} {c.Project.Name}", 500),
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };
        foreach (var line in c.Entry)
        {
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = entry.Lines.Count + 1,
                AccountId = accounts.TryGetValue(line.Account, out var id)
                    ? id
                    : throw new InvalidOperationException($"Account {line.Account} isn't in the chart of accounts. Install the default chart (Accounting › Chart of accounts) first."),
                ProjectId = c.Project.Id,
                Debit = line.Debit,
                Credit = line.Credit,
                Memo = line.Memo,
            });
        }

        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, ct);
        run.JournalEntryId = entry.Id;
        db.Set<RevenueRun>().Add(run);
        audit.Record(db, RevenueSupport.RunEntity, run.Id, "Posted", after: new
        {
            Period = c.Period.Name,
            Project = c.Project.Code,
            run.RecognizedToDate,
            run.PeriodRevenue,
            run.RevenueAdjustment,
            run.UnbilledBalance,
            run.BillingsInExcessBalance,
            run.LossProvisionBalance,
            Entry = entry.Number,
        });
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    public async Task<RevenueRunDto> ReverseAsync(Guid runId, ReverseRevenueRunRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("A reason is required to reverse a revenue run.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<RevenueRun>().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Revenue run", runId);
        if (run.Status != RevenueRunStatus.Posted)
        {
            throw new InvalidOperationException("This revenue run is already reversed.");
        }

        var period = await RevenueSupport.FindPeriodAsync(db, run.FiscalPeriodId, cancellationToken);
        if (period.Status != PeriodStatus.Open)
        {
            throw new ArgumentException($"Fiscal period {period.Name} is closed; its revenue run can't be reversed.");
        }

        if (await LaterRunAsync(db, run.ProjectId, period, cancellationToken) is { } later)
        {
            throw new InvalidOperationException($"The project has a posted revenue run for {later}; reverse that one first.");
        }

        if (run.JournalEntryId is { } entryId)
        {
            var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstAsync(e => e.Id == entryId, cancellationToken);
            if (entry.Status == JournalStatus.Posted)
            {
                var reversal = await ledger.ReverseAsync(db, entry, period.End, request.Reason.Trim(), cancellationToken);
                run.ReversalEntryId = reversal.Id;
            }
        }

        run.Status = RevenueRunStatus.Reversed;
        run.PostedKey = null;
        run.ReversedBy = ledger.Actor;
        run.ReversedAtUtc = ledger.UtcNow;
        run.ReversalReason = RevenueSupport.Truncate(request.Reason.Trim(), 1000);
        audit.Record(db, RevenueSupport.RunEntity, run.Id, "Reversed", new { Status = nameof(RevenueRunStatus.Posted) }, new { Status = run.Status.ToString(), Period = period.Name }, run.ReversalReason);
        await db.SaveChangesAsync(cancellationToken);
        return (await RevenueSupport.RunDtosAsync(db, db.Set<RevenueRun>().Where(r => r.Id == runId), cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<RevenueRunDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RevenueSupport.RunDtosAsync(db, db.Set<RevenueRun>().Where(r => fiscalPeriodId == null || r.FiscalPeriodId == fiscalPeriodId), cancellationToken);
    }

    public async Task<RevenueRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await RevenueSupport.RunDtosAsync(db, db.Set<RevenueRun>().Where(r => r.Id == runId), cancellationToken)).SingleOrDefault()
            ?? throw WorkbenchNotFoundException.For("Revenue run", runId);
    }

    /// <summary>Projects a revenue run covers: every project on a contract, in code order.</summary>
    internal static async Task<List<Guid>> ProjectIdsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        (await db.Set<Project>().AsNoTracking().Where(p => p.ContractId != null).Select(p => new { p.Id, p.Code }).ToListAsync(ct))
            .OrderBy(p => p.Code, StringComparer.Ordinal).Select(p => p.Id).ToList();

    /// <summary>The name of a later period with a posted run for the project, if any.</summary>
    private static async Task<string?> LaterRunAsync(IWorkbenchDbContext db, Guid projectId, FiscalPeriod period, CancellationToken ct) =>
        await (from r in db.Set<RevenueRun>().AsNoTracking()
               join p in db.Set<FiscalPeriod>().AsNoTracking() on r.FiscalPeriodId equals p.Id
               where r.ProjectId == projectId && r.Status == RevenueRunStatus.Posted && p.Start > period.Start
               orderby p.Start
               select p.Name).FirstOrDefaultAsync(ct);

    internal static async Task<RevenueProjectRunDto> ToDtoAsync(IWorkbenchDbContext db, ProjectComputation c, RevenueSupport.Names names, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Number, a => a.Name, ct);
        return new RevenueProjectRunDto(c.Project.Id, c.Project.Code, c.Project.Name, c.Contract.ContractNumber, c.RecognizedToDate, c.BilledToDate, c.PeriodRevenue,
            c.RevenueAdjustment, c.Unbilled, c.BillingsInExcess, c.LossProvision,
            c.Units.Select(u => u.ToDto(names)).ToList(),
            c.Entry.Select(l => new RevenueEntryLineDto(l.Account, accounts.GetValueOrDefault(l.Account) ?? "", l.Debit, l.Credit, l.Memo)).ToList(),
            c.Notes);
    }
}

/// <summary>
/// Period close: revenue is recognized for every project on a contract. A project passes when its run for the period is
/// posted, or when there's nothing to post (the ledger already matches its revenue method).
/// </summary>
public sealed class RevenueCloseCheck(RevenueEngine engine, IWorkbenchDbContextFactory? dbFactory = null) : IPeriodCloseCheck
{
    public const string Title = "Revenue recognized";

    public async Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken cancellationToken)
    {
        var posted = (await db.Set<RevenueRun>().AsNoTracking().Where(r => r.FiscalPeriodId == period.Id && r.Status == RevenueRunStatus.Posted)
            .Select(r => r.ProjectId).ToListAsync(cancellationToken)).ToHashSet();
        var missing = new List<string>();
        var stale = new List<string>();
        var projectIds = await RevenueRunService.ProjectIdsAsync(db, cancellationToken);
        var computed = dbFactory is not null
            ? await engine.ComputeManyAsync(dbFactory, projectIds, period, cancellationToken)
            : await SequentialAsync();
        foreach (var (projectId, result) in computed)
        {
            if (result is not { HasEntry: true } c)
            {
                continue;
            }

            (posted.Contains(projectId) ? stale : missing).Add($"{c.Project.Code} {c.Project.Name}: {c.RevenueAdjustment:N2} revenue to post");
        }

        async Task<IReadOnlyList<(Guid, ProjectComputation?)>> SequentialAsync()
        {
            var list = new List<(Guid, ProjectComputation?)>();
            foreach (var id in projectIds)
            {
                list.Add((id, await engine.ComputeAsync(db, id, period, cancellationToken)));
            }

            return list;
        }

        if (missing.Count == 0)
        {
            var summary = posted.Count == 0 ? "No revenue to recognize" : $"Revenue posted for {posted.Count} project(s)";
            var details = stale.Select(s => $"{s} (changed since the run; reverse and run again to catch up)").ToList();
            return new CloseCheckDto(CloseCheckKind.RevenueRecognized, Title, true, 0, summary, details);
        }

        return new CloseCheckDto(CloseCheckKind.RevenueRecognized, Title, false, missing.Count,
            $"{missing.Count} project(s) without a revenue run for {period.Name}",
            missing.Take(25).Append("Run it under Accounting › Revenue.").ToList());
    }
}
