using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.Revenue;
using Workbench.Contracts.Time;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.LoadData;

/// <summary>
/// The phases that run through the real (permission-checked) services: indirect allocations, revenue setup and runs, and
/// last week's timesheets entered and submitted by each employee (which starts their approvals). Plus the funding pass
/// and the closing checks.
/// </summary>
internal sealed partial class LoadGenerator
{
    private string Finance => $"finance@{Domain}";

    private async Task<T> AsAsync<T>(string email, Func<IServiceProvider, Task<T>> action)
    {
        user.Email = email;
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private async Task IndirectSetupAsync()
    {
        await AsAsync(Finance, sp => sp.GetRequiredService<IIndirectRateService>().InstallDefaultPoolsAsync());
        var now = DateTime.UtcNow;
        await using var db = await factory.CreateDbContextAsync();
        foreach (var year in _years)
        {
            foreach (var (pool, rate) in new[] { (IndirectPool.Fringe, Fringe), (IndirectPool.Overhead, Overhead), (IndirectPool.GeneralAndAdministrative, Ga) })
            {
                db.Add(new IndirectRate
                {
                    Pool = pool, FiscalYear = year.Year, Basis = IndirectRateBasis.Provisional, Rate = rate, EffectiveFrom = year.Start,
                    Note = "Provisional billing rates", ApprovedBy = Finance, ApprovedAtUtc = Utc(year.Start, 15), CreatedBy = Finance, CreatedAtUtc = Utc(year.Start, 14),
                });
                db.Add(new IndirectRate
                {
                    Pool = pool, FiscalYear = year.Year, Basis = IndirectRateBasis.Target, Rate = rate - 0.01m, EffectiveFrom = year.Start,
                    Note = "Budget target", CreatedBy = Finance, CreatedAtUtc = Utc(year.Start, 14),
                });
                if (year.End < options.Today.AddYears(-1))
                {
                    db.Add(new IndirectRate
                    {
                        Pool = pool, FiscalYear = year.Year, Basis = IndirectRateBasis.Actual, Rate = Math.Round(rate * Between(0.95m, 1.05m), 4), EffectiveFrom = year.Start,
                        Note = "Actual rates from the year-end close", ApprovedBy = Finance, ApprovedAtUtc = Utc(year.End.AddMonths(4), 15), CreatedBy = Finance, CreatedAtUtc = Utc(year.End.AddMonths(4), 14),
                    });
                }
            }
        }

        await db.SaveChangesAsync();
        _ = now;
    }

    /// <summary>
    /// CLIN ceilings and funding from what the history billed: each fiscal year an executed modification funds the
    /// year's billings plus 15%; ceilings are 40% above the lifetime billings. Contract totals follow the CLINs.
    /// </summary>
    private async Task FundingAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var billedByClinYear = (await db.Set<InvoiceLine>().AsNoTracking()
                .Join(db.Set<Invoice>(), l => l.InvoiceId, i => i.Id, (l, i) => new { l.ClinId, i.InvoiceDate, l.Amount })
                .ToListAsync())
            .Where(x => x.ClinId != null)
            .GroupBy(x => (Clin: x.ClinId!.Value, Fy: FiscalYearOf(x.InvoiceDate)))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var clins = await db.Set<Clin>().ToListAsync();
        var contracts = await db.Set<Contract>().ToDictionaryAsync(c => c.Id);
        var mods = new Dictionary<Guid, int>();
        foreach (var clin in clins.OrderBy(c => c.ContractId).ThenBy(c => c.Number))
        {
            var lifetime = billedByClinYear.Where(kv => kv.Key.Clin == clin.Id).Sum(kv => kv.Value);
            clin.CeilingValue = Math.Round(Math.Max(lifetime * 1.4m, 250_000m) / 1000m) * 1000m;
            foreach (var year in _years.Where(y => y.Start <= clin.PeriodEnd && y.End >= clin.PeriodStart))
            {
                var amount = Math.Round(Math.Max(billedByClinYear.GetValueOrDefault((clin.Id, year.Year)) * 1.15m, year.Year == _years[^1].Year ? 150_000m : 25_000m) / 100m) * 100m;
                if (clin.FundedValue + amount > clin.CeilingValue)
                {
                    amount = clin.CeilingValue - clin.FundedValue;
                }

                if (amount <= 0)
                {
                    continue;
                }

                var number = mods[clin.ContractId] = mods.GetValueOrDefault(clin.ContractId) + 1;
                var effective = year.Start < clin.PeriodStart ? clin.PeriodStart : year.Start;
                var mod = new ContractModification
                {
                    ContractId = clin.ContractId, Number = $"P{number:D5}", EffectiveDate = effective, Description = $"Incremental funding FY{year.Year}, CLIN {clin.Number}",
                    Status = ModificationStatus.Executed, ExecutedAtUtc = Utc(effective, 15), ExecutedBy = Finance,
                };
                db.Add(mod);
                db.Add(new FundingAction
                {
                    ContractId = clin.ContractId, ClinId = clin.Id, ModificationId = mod.Id, EffectiveDate = effective, Amount = amount,
                    Description = mod.Description, CreatedAtUtc = mod.ExecutedAtUtc!.Value, CreatedBy = Finance,
                });
                db.Add(new AuditEvent { EntityType = "FundingAction", EntityId = mod.Id, Action = "Executed", Actor = Finance, OccurredAtUtc = mod.ExecutedAtUtc!.Value, AfterJson = $"{{\"amount\":{amount}}}" });
                clin.FundedValue += amount;
            }
        }

        foreach (var contract in contracts.Values)
        {
            var mine = clins.Where(c => c.ContractId == contract.Id).ToList();
            contract.CeilingValue = mine.Sum(c => c.CeilingValue);
            contract.FundedValue = mine.Sum(c => c.FundedValue);
        }

        await db.SaveChangesAsync();
        var paid = _paid.ToList();
        for (var i = 0; i < paid.Count; i += 500)
        {
            var chunk = paid.Skip(i).Take(500).ToList();
            await db.Set<Invoice>().Where(x => chunk.Contains(x.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AmountPaid, x => x.Total).SetProperty(x => x.Status, InvoiceStatus.Paid));
        }

        Console.WriteLine($"  {mods.Values.Sum()} funding modifications; {paid.Count:N0} invoices paid, {_openInvoices.Count} open");
    }

    private async Task AllocationsAsync()
    {
        var timer = Stopwatch.StartNew();
        var times = new List<double>();
        foreach (var period in _periods.Where(p => p.Start < LastWeek))
        {
            var one = Stopwatch.StartNew();
            await AsAsync(Finance, async sp =>
            {
                var rates = sp.GetRequiredService<IIndirectRateService>();
                var preview = await rates.PreviewAllocationAsync(period.Id);
                return preview.Total == 0 ? preview : await rates.PostAllocationAsync(period.Id);
            });
            times.Add(one.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"  {times.Count} allocation runs in {timer.Elapsed.TotalSeconds:0.0}s (median {Median(times):0} ms, max {times.Max():0} ms per period)");
    }

    private async Task RevenueAsync()
    {
        foreach (var proj in _projects)
        {
            var method = proj.Contract.C.Type switch
            {
                ContractType.CostPlusFixedFee => RevenueMethodKind.CostPlusFee,
                ContractType.FirmFixedPrice => RevenueMethodKind.FixedPriceMilestones,
                ContractType.LaborHour => RevenueMethodKind.TimeAndMaterialsBilled,
                _ => RevenueMethodKind.TimeAndMaterialsIncurred,
            };
            await AsAsync(Finance, sp => sp.GetRequiredService<IRevenueSetupService>().SaveSetupAsync(new SaveRevenueSetupRequest(proj.P.Id, method, Notes: "Load data")));
        }

        var times = new List<double>();
        var periods = _periods.Where(p => p.End < LastWeek).TakeLast(12).ToList();
        foreach (var period in periods)
        {
            var one = Stopwatch.StartNew();
            var result = await AsAsync(Finance, sp => sp.GetRequiredService<IRevenueRunService>().PostAsync(period.Id, new PostRevenueRunRequest()));
            times.Add(one.Elapsed.TotalMilliseconds);
            Console.WriteLine($"  revenue run {period.Name}: {one.Elapsed.TotalSeconds:0.0}s");
            _ = result;
        }

        Console.WriteLine($"  {times.Count} revenue runs (median {Median(times):0} ms)");
    }

    private async Task ClosePeriodsAsync()
    {
        // Everything before last month is closed; last month (being closed now) and the current month stay open.
        var lastMonth = new DateOnly(options.Today.Year, options.Today.Month, 1).AddMonths(-1);
        await using var db = await factory.CreateDbContextAsync();
        var closed = await db.Set<FiscalPeriod>().Where(p => p.End < lastMonth).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PeriodStatus.Closed));
        Console.WriteLine($"  closed {closed} periods");
    }

    /// <summary>
    /// Last week through the real services, as each employee: open the timesheet, save the week's hours, certify and
    /// submit. That starts each timesheet's approval, so the 50 approvers have real inboxes for the load test.
    /// </summary>
    private async Task SubmitLastWeekAsync()
    {
        var weekIndex = LastWeek.DayNumber / 7;
        var people = _people.Where(p => p.E.IsActiveOn(LastWeek) && p.E.TerminationDate is null).ToList();
        var timer = Stopwatch.StartNew();
        var times = new System.Collections.Concurrent.ConcurrentBag<double>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        // Work out the hours first: the generator's random numbers aren't thread safe.
        var plans = people.ToDictionary(p => p, p => Enumerable.Range(0, 5).Select(i => LastWeek.AddDays(i))
            .SelectMany(d => Day(p, d, weekIndex).Select(c => new TimeEntryInput(c.Wbs.Id, d, c.Hours))).ToList());
        await Parallel.ForEachAsync(people, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (person, ct) =>
        {
            var one = Stopwatch.StartNew();
            try
            {
                await AsAsync(person.Email, async sp =>
                {
                    var timesheets = sp.GetRequiredService<ITimesheetService>();
                    var sheet = await timesheets.GetOrCreateMyTimesheetAsync(LastWeek, ct);
                    await timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest(plans[person]), ct);
                    return await timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true), ct);
                });
                times.Add(one.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                failures.Add($"{person.Email}: {ex.Message}");
            }
        });

        Console.WriteLine($"  {times.Count} submitted in {timer.Elapsed.TotalSeconds:0.0}s (median {Median([.. times]):0} ms per timesheet, 8 at a time); {failures.Count} refused");
        foreach (var failure in failures.Take(10))
        {
            Console.WriteLine($"    {failure}");
        }
    }

    private async Task VerifyAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var totals = await db.Set<JournalLine>().AsNoTracking()
            .Join(db.Set<JournalEntry>().Where(e => e.Status != JournalStatus.Draft), l => l.JournalEntryId, e => e.Id, (l, e) => l)
            .GroupBy(_ => 1).Select(g => new { Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit), Lines = g.Count() }).SingleAsync();
        var unbalanced = await db.Set<JournalLine>().AsNoTracking().GroupBy(l => l.JournalEntryId)
            .Where(g => g.Sum(l => l.Debit) != g.Sum(l => l.Credit)).CountAsync();
        Console.WriteLine($"  ledger: {totals.Lines:N0} lines, debits {totals.Debit:N2}, credits {totals.Credit:N2}, {unbalanced} unbalanced entries");

        var current = _periods.First(p => p.Start <= options.Today && options.Today <= p.End);
        var timer = Stopwatch.StartNew();
        var trial = await AsAsync(Finance, sp => sp.GetRequiredService<IAccountingReportService>().GetTrialBalanceAsync(current.Id));
        Console.WriteLine($"  trial balance {trial.FiscalPeriodName}: {(trial.IsBalanced ? "balanced" : "NOT BALANCED")} " +
                          $"(ending debits {trial.TotalEndingDebit:N2}, credits {trial.TotalEndingCredit:N2}) in {timer.ElapsedMilliseconds} ms");
        if (!trial.IsBalanced || totals.Debit != totals.Credit || unbalanced > 0)
        {
            throw new InvalidOperationException("The generated ledger doesn't balance.");
        }

        foreach (var (name, count) in new[]
                 {
                     ("employees", await db.Set<Employee>().CountAsync()), ("org units", await db.Set<OrgUnit>().CountAsync()),
                     ("contracts", await db.Set<Contract>().CountAsync()), ("CLINs", await db.Set<Clin>().CountAsync()),
                     ("projects", await db.Set<Project>().CountAsync()), ("WBS elements", await db.Set<WbsElement>().CountAsync()),
                     ("sheets", await db.Set<Workbench.Domain.Sheet>().CountAsync()), ("rows", await db.Set<Workbench.Domain.Item>().CountAsync()),
                     ("timesheets", await db.Set<Timesheet>().CountAsync()), ("time entries", await db.Set<TimeEntry>().CountAsync()),
                     ("ODCs and expense lines", await db.Set<OdcEntry>().CountAsync()), ("AP bills", await db.Set<Workbench.Domain.Purchasing.ApBill>().CountAsync()),
                     ("invoices", await db.Set<Invoice>().CountAsync()), ("payments", await db.Set<Payment>().CountAsync()),
                     ("journal entries", await db.Set<JournalEntry>().CountAsync()), ("approval instances", await db.Set<Workbench.Domain.Approvals.ApprovalInstance>().CountAsync()),
                     ("revenue runs", await db.Set<Workbench.Domain.Revenue.RevenueRun>().CountAsync()), ("budget lines", await db.Set<BudgetLine>().CountAsync()),
                 })
        {
            Console.WriteLine($"  {name,-24} {count,12:N0}");
        }
    }

    private static double Median(IReadOnlyList<double> values) =>
        values.Count == 0 ? 0 : values.Order().ElementAt(values.Count / 2);
}
