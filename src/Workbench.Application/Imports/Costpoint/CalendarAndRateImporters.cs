using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using ImportKind = Workbench.Domain.Imports.ImportKind;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>
/// Costpoint's fiscal calendar (Fiscal Year / Accounting Periods export) → fiscal years and their periods. A year with
/// the usual twelve calendar months is created exactly as Setup › Fiscal calendar would; years with a thirteenth
/// adjusting period or other period lengths are taken as the file has them. Periods arrive open: close them from
/// Setup › Fiscal calendar once the opening balances are in. A year Workbench already has is left alone when the file
/// agrees with it and reported when it doesn't (the dates of a year that has postings are not changed by an import).
/// </summary>
public sealed class FiscalCalendarImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.FiscalCalendar;
    public override string Title => "Fiscal calendar";
    public override string Description => "Fiscal years and their accounting periods (start and end dates). Import before the opening balances, which post into a period.";
    public override string? SampleFile => "samples/import/costpoint/fiscal-calendar.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("FiscalYear", "Fiscal year", true, "The year number, e.g. 2027.", "FY_CD", "FISCAL_YEAR", "Fiscal Year", "FY", "Year"),
        new("Period", "Period number", true, "1–12, or 13 for an adjusting period.", "PD_NO", "PER_NO", "SUB_PD_NO", "Period", "Period No", "Period Number", "Pd"),
        new("Start", "Period start", true, "First day of the period.", "PD_START_DT", "START_DT", "Start Date", "Period Start", "From", "Begin Date"),
        new("End", "Period end", true, "Last day of the period.", "PD_END_DT", "END_DT", "End Date", "Period End", "To"),
        new("Name", "Period name", false, "Defaults to the year and month of the start date (2026-10), the way Workbench names its own periods.", "PD_NAME", "Period Name", "Description", "Name"),
    ];

    private sealed record PeriodRow(MappedRow Row, int Year, int Number, DateOnly Start, DateOnly End, string? Name);

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var dbFactory = Service<IWorkbenchDbContextFactory>(context);
        var audit = Service<AuditRecorder>(context);
        var calendar = Service<IFiscalCalendarService>(context);
        var parsed = new List<PeriodRow>();
        foreach (var row in rows)
        {
            var yearText = row.Required("FiscalYear");
            var numberText = row.Required("Period");
            var start = row.Date("Start");
            var end = row.Date("End");
            if (row.HasErrors)
            {
                continue;
            }

            if (!int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) || year is < 1990 or > 2200)
            {
                row.Error("FiscalYear", $"'{yearText}' isn't a fiscal year (use a number such as 2027).");
                continue;
            }

            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 14)
            {
                row.Error("Period", $"'{numberText}' isn't a period number (1–12, or 13 for the adjusting period).");
                continue;
            }

            if (start is null || end is null)
            {
                row.Error(start is null ? "Start" : "End", "Give the period's start and end dates.");
                continue;
            }

            if (end < start)
            {
                row.Error("End", $"Period {number} ends ({end:yyyy-MM-dd}) before it starts ({start:yyyy-MM-dd}).");
                continue;
            }

            parsed.Add(new PeriodRow(row, year, number, start.Value, end.Value, row.Get("Name")));
        }

        var existing = (await calendar.ListPeriodsAsync(null, cancellationToken)).ToList();
        foreach (var yearGroup in parsed.GroupBy(p => p.Year).OrderBy(g => g.Key))
        {
            await ImportYearAsync(context, yearGroup.Key, yearGroup.OrderBy(p => p.Number).ToList(), existing, dbFactory, audit, cancellationToken);
        }
    }

    private async Task ImportYearAsync(ImportContext context, int year, List<PeriodRow> periods, List<FiscalPeriodDto> existing,
        IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, CancellationToken cancellationToken)
    {
        var first = periods[0].Row;
        void Reject(string message)
        {
            foreach (var p in periods)
            {
                p.Row.Error(null, message);
            }
        }

        if (periods.GroupBy(p => p.Number).FirstOrDefault(g => g.Count() > 1) is { } dup)
        {
            Reject($"Fiscal year {year} lists period {dup.Key} more than once.");
            return;
        }

        for (var i = 1; i < periods.Count; i++)
        {
            var gap = periods[i].Start.DayNumber - periods[i - 1].End.DayNumber;
            if (periods[i].Number == periods[i - 1].Number + 1 && gap != 1)
            {
                Reject(gap > 1
                    ? $"Fiscal year {year} has a gap between period {periods[i - 1].Number} (ends {periods[i - 1].End:yyyy-MM-dd}) and period {periods[i].Number} (starts {periods[i].Start:yyyy-MM-dd}); every day must belong to a period."
                    : $"Periods {periods[i - 1].Number} and {periods[i].Number} of fiscal year {year} overlap.");
                return;
            }
        }

        var yearStart = periods.Min(p => p.Start);
        var yearEnd = periods.Max(p => p.End);
        var sameYear = existing.Where(p => p.FiscalYear == year).OrderBy(p => p.Start).ToList();
        if (sameYear.Count > 0)
        {
            var same = sameYear.Count == periods.Count
                && sameYear.Zip(periods).All(z => z.First.Start == z.Second.Start && z.First.End == z.Second.End);
            if (same)
            {
                context.Run.Skip(first.FileName, first.Number, "Fiscal year", year.ToString(CultureInfo.InvariantCulture), "Already in Workbench with the same periods");
                return;
            }

            Reject($"Fiscal year {year} is already in Workbench with different periods ({sameYear.Count} periods, {sameYear[0].Start:yyyy-MM-dd} to {sameYear[^1].End:yyyy-MM-dd}). "
                + "Change it in Setup › Fiscal calendar; an import doesn't move the dates of a year that may already have postings.");
            return;
        }

        if (existing.FirstOrDefault(p => p.Start <= yearEnd && p.End >= yearStart) is { } clash)
        {
            Reject($"Fiscal year {year} ({yearStart:yyyy-MM-dd} to {yearEnd:yyyy-MM-dd}) overlaps period {clash.Name} of fiscal year {clash.FiscalYear}.");
            return;
        }

        var months = new HashSet<string>();
        var named = periods.Select(p =>
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? p.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture) : p.Name!.Trim();
            // A thirteenth period shares a month with period 12: keep its name distinct.
            if (!months.Add(name))
            {
                name = $"{name}-P{p.Number}";
                months.Add(name);
            }

            return (p, name);
        }).ToList();

        var summary = $"{periods.Count} periods, {yearStart:yyyy-MM-dd} to {yearEnd:yyyy-MM-dd}";
        if (periods.Count != 12)
        {
            first.Warning(null, $"Fiscal year {year} has {periods.Count} periods; Workbench's own calendar tool creates 12 monthly ones. Check that month-end reports suit your periods.");
        }

        var yearId = await context.Run.CreateAsync(first.FileName, first.Number, typeof(FiscalYear), "Fiscal year", year.ToString(CultureInfo.InvariantCulture), summary, async () =>
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var entity = new FiscalYear { Year = year, Start = yearStart, End = yearEnd };
            db.Set<FiscalYear>().Add(entity);
            audit.Record(db, nameof(FiscalYear), entity.Id, "Created", after: new { entity.Year, entity.Start, entity.End }, reason: Reason(context));
            await db.SaveChangesAsync(cancellationToken);
            return entity.Id;
        }, cancellationToken);
        if (yearId is not { } id)
        {
            return;
        }

        foreach (var (p, name) in named)
        {
            await context.Run.CreateAsync(p.Row.FileName, p.Row.Number, typeof(FiscalPeriod), "Fiscal period", name, $"Period {p.Number}: {p.Start:yyyy-MM-dd} to {p.End:yyyy-MM-dd}", async () =>
            {
                await using var db = await dbFactory.CreateAsync(cancellationToken);
                var period = new FiscalPeriod { FiscalYearId = id, Number = p.Number, Name = name, Start = p.Start, End = p.End };
                db.Set<FiscalPeriod>().Add(period);
                await db.SaveChangesAsync(cancellationToken);
                return period.Id;
            }, cancellationToken);
        }
    }
}

/// <summary>Reads Costpoint's pool names (FRINGE, OVHD, G&amp;A ...) as Workbench's three pools.</summary>
internal static class CostpointPools
{
    public static CostPool? Parse(string? text)
    {
        var t = new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (t.Length == 0)
        {
            return null;
        }

        if (t.StartsWith("FRING", StringComparison.Ordinal) || t == "FR" || t == "FB")
        {
            return CostPool.Fringe;
        }

        if (t.StartsWith("OVERHEAD", StringComparison.Ordinal) || t is "OH" or "OVHD" or "OVH" || t.StartsWith("OVHD", StringComparison.Ordinal))
        {
            return CostPool.Overhead;
        }

        if (t is "GA" or "GNA" || t.StartsWith("GENERAL", StringComparison.Ordinal) || t.StartsWith("GANDA", StringComparison.Ordinal))
        {
            return CostPool.GeneralAndAdministrative;
        }

        return null;
    }

    public static string Label(CostPool pool) => pool switch
    {
        CostPool.GeneralAndAdministrative => "G&A",
        _ => pool.ToString(),
    };
}

/// <summary>
/// Costpoint's indirect rate schedule (provisional billing, actual, budget/target and forward-pricing rates by pool and
/// fiscal year) → Workbench's rate history. A changed rate supersedes the old one, as it does in Workbench, so the
/// history stays; rolling the import back withdraws the new rows and puts the old ones back in force.
/// </summary>
public sealed class IndirectRateImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.IndirectRates;
    public override string Title => "Indirect rates";
    public override string Description => "Rates by pool (fringe, overhead, G&A), fiscal year and kind (provisional, actual, target, forward pricing).";
    public override string? SampleFile => "samples/import/costpoint/indirect-rates.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Pool", "Pool", true, "Fringe, Overhead or G&A. Costpoint's names (FRINGE BENEFITS, OVHD, G & A) are recognized.", "POOL_NO", "POOL_ID", "Pool", "Pool Name", "Pool Description", "Indirect Pool"),
        new("FiscalYear", "Fiscal year", true, "", "FY_CD", "FISCAL_YEAR", "Fiscal Year", "FY", "Year"),
        new("Rate", "Rate", true, "A fraction (0.3215), a percentage with a % sign (32.15%) or, in a column whose title says % or percent, 32.15.", "RATE", "RATE_PCT", "Rate", "Rate %", "Rate (%)", "Pool Rate", "Percent"),
        new("Basis", "Rate kind", false, "Provisional (billing), Actual, Target (budget) or Forward pricing. Defaults to Provisional.", "RATE_TYPE", "RATE_TYPE_CD", "Rate Type", "Rate Kind", "Type", "Rate Basis", "Basis"),
        new("EffectiveFrom", "Effective from", false, "Defaults to the first day of the fiscal year.", "EFFECT_DT", "EFFECTIVE_DT", "Effective Date", "Effective From", "Start Date"),
        new("Note", "Note", false, "", "NOTES", "Note", "Notes", "Comment"),
        new("Approved", "Approved", false, "Y/N. Approved rates are the ones billing uses without further review. Defaults to N.", "APPROVED_FL", "Approved", "Approved Flag"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var service = Service<IIndirectRateService>(context);
        var existing = (await service.ListRatesAsync(null, false, cancellationToken)).ToList();
        var ratePercentHeader = LooksLikePercentColumn(context);
        var seen = new HashSet<(CostPool, int, RateBasis)>();

        foreach (var row in rows)
        {
            var poolText = row.Required("Pool");
            var yearText = row.Required("FiscalYear");
            var rateText = row.Required("Rate");
            var basis = ParseBasis(row);
            var effective = row.Date("EffectiveFrom");
            var approved = row.Bool("Approved") ?? false;
            if (row.HasErrors)
            {
                continue;
            }

            if (CostpointPools.Parse(poolText) is not { } pool)
            {
                row.Error("Pool", $"'{poolText}' isn't one of Workbench's pools (Fringe, Overhead, G&A).");
                continue;
            }

            if (!int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) || year is < 2000 or > 2200)
            {
                row.Error("FiscalYear", $"'{yearText}' isn't a fiscal year.");
                continue;
            }

            if (ParseRate(rateText!, ratePercentHeader) is not { } rate)
            {
                row.Error("Rate", $"'{rateText}' isn't a rate. Use a fraction (0.3215), or a percentage with a % sign (32.15%).");
                continue;
            }

            if (rate is < 0 or > 5)
            {
                row.Error("Rate", $"{rate:0.####} is outside 0–500%. If the column holds percentages, title it 'Rate %' or add a % sign to each value.");
                continue;
            }

            if (basis is null || !seen.Add((pool, year, basis.Value)))
            {
                if (basis is not null)
                {
                    row.Error("Rate", $"{CostpointPools.Label(pool)} FY{year} {basis.ToString()!.ToLowerInvariant()} appears more than once in the file.");
                }

                continue;
            }

            var label = $"{CostpointPools.Label(pool)} FY{year} {basis}";
            var current = existing.FirstOrDefault(r => r.Pool == pool && r.FiscalYear == year && r.Basis == basis);
            var request = new SaveIndirectRateRequest(pool, year, basis.Value, rate, effective, row.Get("Note"), approved, current is null ? null : Reason(context));
            if (current is not null)
            {
                if (current.Rate == decimal.Round(rate, 6) && (effective is null || current.EffectiveFrom == effective))
                {
                    context.Run.Skip(row.FileName, row.Number, "Indirect rate", label, "Unchanged");
                    continue;
                }

                var oldId = current.Id;
                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(IndirectRate), oldId, "Indirect rate", label,
                    $"Rate {current.Rate:P4} → {rate:P4}; the old rate stays in the history", async () =>
                    {
                        var saved = await service.SaveRateAsync(request, cancellationToken);
                        await context.Run.RecordCreatedAsync(typeof(IndirectRate), saved.Id, $"Indirect rate {label}", cancellationToken);
                    }, cancellationToken);
                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(IndirectRate), "Indirect rate", label, $"{rate:P4}{(approved ? ", approved" : "")}",
                async () => (await service.SaveRateAsync(request, cancellationToken)).Id, cancellationToken);
        }
    }

    private bool LooksLikePercentColumn(ImportContext context)
    {
        if (!context.Mapping.Fields.TryGetValue("Rate", out var header) || header is null)
        {
            return false;
        }

        var h = header.ToUpperInvariant();
        return h.Contains('%', StringComparison.Ordinal) || h.Contains("PCT", StringComparison.Ordinal) || h.Contains("PERCENT", StringComparison.Ordinal);
    }

    internal static decimal? ParseRate(string text, bool percentColumn)
    {
        var t = text.Trim();
        var percent = t.EndsWith('%');
        if (percent)
        {
            t = t[..^1];
        }

        if (!decimal.TryParse(t.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return percent || percentColumn ? value / 100m : value;
    }

    private static RateBasis? ParseBasis(MappedRow row)
    {
        var text = row.Get("Basis");
        if (text is null)
        {
            return RateBasis.Provisional;
        }

        var t = text.ToUpperInvariant();
        if (t.Contains("PROV", StringComparison.Ordinal) || t.Contains("BILL", StringComparison.Ordinal))
        {
            return RateBasis.Provisional;
        }

        if (t.Contains("ACT", StringComparison.Ordinal))
        {
            return RateBasis.Actual;
        }

        if (t.Contains("TARGET", StringComparison.Ordinal) || t.Contains("BUDGET", StringComparison.Ordinal) || t.Contains("PLAN", StringComparison.Ordinal))
        {
            return RateBasis.Target;
        }

        if (t.Contains("FORWARD", StringComparison.Ordinal) || t.Contains("FPRP", StringComparison.Ordinal) || t.Contains("FPRA", StringComparison.Ordinal) || t.Contains("PRICING", StringComparison.Ordinal))
        {
            return RateBasis.ForwardPricing;
        }

        row.Error("Basis", $"'{text}' isn't a rate kind. Use Provisional, Actual, Target or Forward pricing.");
        return null;
    }
}

/// <summary>
/// Costpoint's pool structure (which GL accounts feed each pool, what the pool is spread over) → Workbench's pool setups.
/// One row per pool and account. The accounts listed become the pool's explicit accounts; accounts that reach the pool
/// through a cost class are unaffected. A pool Workbench doesn't have yet needs its two burden accounts (applied and
/// contra) in the file, because Costpoint's own are in a different part of its setup.
/// </summary>
public sealed class IndirectPoolImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.IndirectPools;
    public override string Title => "Indirect pools and bases";
    public override string Description => "Which expense accounts feed the fringe, overhead and G&A pools, and the base each pool is spread over.";
    public override string? SampleFile => "samples/import/costpoint/indirect-pools.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Pool", "Pool", true, "Fringe, Overhead or G&A.", "POOL_NO", "POOL_ID", "Pool", "Pool Name", "Indirect Pool"),
        new("Account", "Account", true, "An expense account of the chart (import the chart of accounts first). One row per account.", "ACCT_ID", "Account", "Account ID", "Account Number", "Acct"),
        new("Base", "Allocation base", false, "Direct labor, Direct labor + fringe, Total cost input or Value added. Defaults to the pool's current base (fringe: direct labor; overhead: direct labor + fringe; G&A: total cost input).", "ALLOC_BASE", "BASE_TYPE", "Allocation Base", "Base", "Base Type"),
        new("CascadeOrder", "Order", false, "1, 2, 3...: pools are applied in this order and a pool's base can include the burden of the pools before it.", "ORDER_NO", "Cascade Order", "Order", "Sequence"),
        new("AppliedAccount", "Applied-burden account", false, "Only for a pool Workbench doesn't have yet.", "APPLIED_ACCT_ID", "Applied Account", "Applied Burden Account"),
        new("AbsorbedAccount", "Contra account", false, "Only for a pool Workbench doesn't have yet.", "ABSORBED_ACCT_ID", "Contra Account", "Absorbed Account", "Burden Contra Account"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var service = Service<IIndirectRateService>(context);
        var dbFactory = Service<IWorkbenchDbContextFactory>(context);
        var accounts = (await Service<IChartOfAccountsService>(context).ListAccountsAsync(includeInactive: true, cancellationToken))
            .ToDictionary(a => a.Number, StringComparer.OrdinalIgnoreCase);
        var setups = (await service.ListPoolSetupsAsync(cancellationToken)).ToDictionary(s => s.Pool);
        var byPool = new Dictionary<CostPool, List<(MappedRow Row, Guid AccountId)>>();

        foreach (var row in rows)
        {
            var poolText = row.Required("Pool");
            var number = row.Required("Account");
            if (row.HasErrors)
            {
                continue;
            }

            if (CostpointPools.Parse(poolText) is not { } pool)
            {
                row.Error("Pool", $"'{poolText}' isn't one of Workbench's pools (Fringe, Overhead, G&A).");
                continue;
            }

            if (!accounts.TryGetValue(number!, out var account))
            {
                row.Error("Account", $"Account {number} isn't in Workbench's chart of accounts. Import the chart of accounts first.");
                continue;
            }

            if (account.Type != AccountKind.Expense)
            {
                row.Error("Account", $"Account {number} ({account.Name}) isn't an expense account; only expense accounts can feed a pool.");
                continue;
            }

            if (!byPool.TryGetValue(pool, out var list))
            {
                byPool[pool] = list = [];
            }

            if (list.Any(l => l.AccountId == account.Id))
            {
                row.Warning("Account", $"Account {number} is listed twice for {CostpointPools.Label(pool)}; counted once.");
                continue;
            }

            list.Add((row, account.Id));
        }

        foreach (var (pool, list) in byPool.OrderBy(p => p.Key))
        {
            var first = list[0].Row;
            var baseKind = ParseBase(first, pool, setups.GetValueOrDefault(pool));
            var order = int.TryParse(first.Get("CascadeOrder"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : (int?)null;
            if (first.HasErrors || baseKind is null)
            {
                continue;
            }

            var label = CostpointPools.Label(pool);
            var accountIds = list.Select(l => l.AccountId).ToList();
            if (setups.TryGetValue(pool, out var setup))
            {
                var request = new SavePoolSetupRequest(setup.Name, baseKind.Value, order ?? setup.CascadeOrder, setup.IsActive, setup.CostClasses,
                    setup.IncludeMappedIndirectLabor, accountIds, setup.AppliedAccountId, setup.AbsorbedAccountId);
                var changed = Changed(("Base", setup.Base, baseKind), ("Order", setup.CascadeOrder, order ?? setup.CascadeOrder));
                var sameAccounts = setup.AccountIds.ToHashSet().SetEquals(accountIds);
                if (!sameAccounts)
                {
                    changed.Add($"accounts ({setup.AccountIds.Count} → {accountIds.Count})");
                }

                if (changed.Count == 0)
                {
                    context.Run.Skip(first.FileName, first.Number, "Indirect pool", label, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(first.FileName, first.Number, typeof(IndirectPoolSetup), setup.Id, "Indirect pool", label, UpdateSummary(changed),
                    () => SaveTrackedAsync(context, dbFactory, service, pool, request, setup.Id, cancellationToken), cancellationToken);
                continue;
            }

            var applied = ResolveBurdenAccount(first, "AppliedAccount", accounts);
            var absorbed = ResolveBurdenAccount(first, "AbsorbedAccount", accounts);
            if (applied is null || absorbed is null)
            {
                if (!first.HasErrors)
                {
                    first.Error("AppliedAccount", $"{label} isn't set up in Workbench yet. Give its applied-burden and contra accounts in the file, or use 'Install default pools' in Indirect › Rates first.");
                }

                continue;
            }

            var name = pool switch { CostPool.Fringe => "Fringe", CostPool.Overhead => "Overhead", _ => "G&A" };
            var create = new SavePoolSetupRequest(name, baseKind.Value, order ?? (int)pool + 1, true, [], true, accountIds, applied.Value, absorbed.Value);
            await context.Run.CreateAsync(first.FileName, first.Number, typeof(IndirectPoolSetup), "Indirect pool", label, $"{accountIds.Count} accounts, base {baseKind}",
                async () =>
                {
                    var saved = await service.SavePoolSetupAsync(pool, create, cancellationToken);
                    await using var db = await dbFactory.CreateAsync(cancellationToken);
                    foreach (var link in await db.Set<IndirectPoolAccount>().AsNoTracking().Where(a => a.PoolSetupId == saved.Id).Select(a => a.Id).ToListAsync(cancellationToken))
                    {
                        await context.Run.RecordCreatedAsync(typeof(IndirectPoolAccount), link, $"Pool account {label}", cancellationToken);
                    }

                    return saved.Id;
                }, cancellationToken);
        }
    }

    /// <summary>Saves a pool and records the explicit account rows it removed and added, so rollback can restore them.</summary>
    private static async Task SaveTrackedAsync(ImportContext context, IWorkbenchDbContextFactory dbFactory, IIndirectRateService service, CostPool pool,
        SavePoolSetupRequest request, Guid setupId, CancellationToken cancellationToken)
    {
        async Task<Dictionary<Guid, string?>> LinksAsync()
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var ids = await db.Set<IndirectPoolAccount>().AsNoTracking().Where(a => a.PoolSetupId == setupId).Select(a => a.Id).ToListAsync(cancellationToken);
            var result = new Dictionary<Guid, string?>();
            foreach (var id in ids)
            {
                result[id] = await EntitySnapshots.CaptureAsync(db, typeof(IndirectPoolAccount), id, cancellationToken);
            }

            return result;
        }

        var before = await LinksAsync();
        await service.SavePoolSetupAsync(pool, request, cancellationToken);
        var after = await LinksAsync();
        var label = $"Pool account {CostpointPools.Label(pool)}";
        foreach (var (id, snapshot) in before.Where(b => !after.ContainsKey(b.Key)))
        {
            await context.Run.RecordDeletedAsync(typeof(IndirectPoolAccount), id, label, snapshot!, cancellationToken);
        }

        foreach (var id in after.Keys.Where(a => !before.ContainsKey(a)))
        {
            await context.Run.RecordCreatedAsync(typeof(IndirectPoolAccount), id, label, cancellationToken);
        }
    }

    private static Guid? ResolveBurdenAccount(MappedRow row, string field, Dictionary<string, AccountDto> accounts)
    {
        var number = row.Get(field);
        if (number is null)
        {
            return null;
        }

        if (!accounts.TryGetValue(number, out var account))
        {
            row.Error(field, $"Account {number} isn't in Workbench's chart of accounts.");
            return null;
        }

        return account.Id;
    }

    private static AllocationBaseKind? ParseBase(MappedRow row, CostPool pool, PoolSetupDto? setup)
    {
        var text = row.Get("Base");
        if (text is null)
        {
            return setup?.Base ?? pool switch
            {
                CostPool.Fringe => AllocationBaseKind.DirectLabor,
                CostPool.Overhead => AllocationBaseKind.DirectLaborPlusFringe,
                _ => AllocationBaseKind.TotalCostInput,
            };
        }

        var t = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (t.Contains("VALUEADDED", StringComparison.Ordinal) || t == "VA")
        {
            return AllocationBaseKind.ValueAdded;
        }

        if (t.Contains("TOTALCOST", StringComparison.Ordinal) || t == "TCI")
        {
            return AllocationBaseKind.TotalCostInput;
        }

        if (t.Contains("FRINGE", StringComparison.Ordinal))
        {
            return AllocationBaseKind.DirectLaborPlusFringe;
        }

        if (t.Contains("LABOR", StringComparison.Ordinal) || t == "DL" || t == "DLD")
        {
            return AllocationBaseKind.DirectLabor;
        }

        row.Error("Base", $"'{text}' isn't an allocation base. Use Direct labor, Direct labor + fringe, Total cost input or Value added.");
        return null;
    }
}
