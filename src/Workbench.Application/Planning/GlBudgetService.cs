using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Planning;

/// <summary>
/// Corporate GL budget versions (Costpoint Budgeting &amp; Planning): an amount per account × org unit × fiscal period,
/// entered in a grid, spread from an annual amount or imported from CSV. Adopted versions are locked. Every change is audited.
/// </summary>
public sealed class GlBudgetService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IGlBudgetService
{
    public const string AuditEntityType = nameof(GlBudgetVersion);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var years = await db.Set<FiscalYear>().AsNoTracking().ToDictionaryAsync(y => y.Id, y => y.Year, cancellationToken);
        var versions = await db.Set<GlBudgetVersion>().AsNoTracking().ToListAsync(cancellationToken);
        if (fiscalYear is { } fy)
        {
            versions = versions.Where(v => years.GetValueOrDefault(v.FiscalYearId) == fy).ToList();
        }

        return await MapAsync(db, versions.OrderByDescending(v => years.GetValueOrDefault(v.FiscalYearId)).ThenBy(v => v.Kind).ThenBy(v => v.CreatedAtUtc).ToList(), cancellationToken);
    }

    public async Task<BudgetVersionDto> CreateVersionAsync(CreateBudgetVersionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var version = await CreateVersionAsync(db, request, audit, UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, [version], cancellationToken))[0];
    }

    /// <summary>Adds a version (and its copied lines) to <paramref name="db"/> without saving. Also used by planning cycles.</summary>
    internal static async Task<GlBudgetVersion> CreateVersionAsync(IWorkbenchDbContext db, CreateBudgetVersionRequest request, AuditRecorder audit, DateTime now, CancellationToken ct)
    {
        var (year, periods) = await BudgetMath.YearAsync(db, request.FiscalYear, ct);
        var name = PlanningMapping.Clean(request.Name, 200, "Name", required: true);
        if (await db.Set<GlBudgetVersion>().AnyAsync(v => v.FiscalYearId == year.Id && v.Name == name, ct))
        {
            throw new ArgumentException($"FY{year.Year} already has a budget version named \"{name}\".");
        }

        var version = new GlBudgetVersion
        {
            FiscalYearId = year.Id, Kind = request.Kind.ToDomain(), Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : PlanningMapping.Clean(request.Description, 1000, "Description"),
            CreatedBy = audit.Actor, CreatedAtUtc = now, SourceVersionId = request.CopyFromVersionId,
        };
        db.Set<GlBudgetVersion>().Add(version);
        var copied = 0;
        if (request.CopyFromVersionId is { } sourceId)
        {
            var source = await db.Set<GlBudgetVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == sourceId, ct)
                ?? throw WorkbenchNotFoundException.For("Budget version", sourceId);
            var map = await PeriodMapAsync(db, source.FiscalYearId, periods, ct);
            foreach (var line in await db.Set<GlBudgetLine>().AsNoTracking().Where(l => l.VersionId == sourceId).ToListAsync(ct))
            {
                if (map.TryGetValue(line.FiscalPeriodId, out var target))
                {
                    db.Set<GlBudgetLine>().Add(new GlBudgetLine
                    {
                        VersionId = version.Id, AccountId = line.AccountId, OrgUnitId = line.OrgUnitId, FiscalPeriodId = target, Amount = line.Amount,
                        UpdatedBy = audit.Actor, UpdatedAtUtc = now,
                    });
                    copied++;
                }
            }
        }

        audit.Record(db, AuditEntityType, version.Id, "Created", after: new { version.Name, Kind = version.Kind.ToString(), FiscalYear = year.Year, CopiedFrom = request.CopyFromVersionId, Lines = copied });
        return version;
    }

    /// <summary>Source period id → the target year's period with the same number.</summary>
    internal static async Task<Dictionary<Guid, Guid>> PeriodMapAsync(IWorkbenchDbContext db, Guid sourceYearId, IReadOnlyList<FiscalPeriod> target, CancellationToken ct)
    {
        var byNumber = target.ToDictionary(p => p.Number, p => p.Id);
        return (await BudgetMath.PeriodsAsync(db, sourceYearId, ct)).Where(p => byNumber.ContainsKey(p.Number)).ToDictionary(p => p.Id, p => byNumber[p.Number]);
    }

    public async Task<BudgetGridDto> GetGridAsync(Guid versionId, Guid? orgUnitId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await GridAsync(db, versionId, orgUnitId, cancellationToken);
    }

    public async Task<BudgetGridDto> SaveCellsAsync(Guid versionId, SaveBudgetCellsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (version, periods) = await EditableAsync(db, versionId, cancellationToken);
        var cells = request.Cells ?? [];
        if (cells.Count > 20_000)
        {
            throw new ArgumentException("Save at most 20,000 cells at a time.");
        }

        var changed = await SetCellsAsync(db, version, periods, cells.Select(c => (c.AccountId, c.OrgUnitId, c.FiscalPeriodId, c.Amount)).ToList(), cancellationToken);
        audit.Record(db, AuditEntityType, version.Id, "LinesChanged", after: new { Cells = changed, Total = cells.Sum(c => c.Amount) });
        await db.SaveChangesAsync(cancellationToken);
        return await GridAsync(db, versionId, null, cancellationToken);
    }

    public async Task<BudgetGridDto> SpreadAsync(Guid versionId, SpreadBudgetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (version, periods) = await EditableAsync(db, versionId, cancellationToken);
        var year = await db.Set<FiscalYear>().AsNoTracking().FirstAsync(y => y.Id == version.FiscalYearId, cancellationToken);
        var prior = request.Method == SpreadMethod.PriorYearPattern
            ? await BudgetMath.PriorYearPatternAsync(db, year, periods.Count, request.AccountId, request.OrgUnitId, cancellationToken)
            : null;
        var amounts = BudgetMath.Spread(request.AnnualAmount, periods.Count, request.Method, prior, request.Profile);
        await SetCellsAsync(db, version, periods, periods.Select((p, i) => (request.AccountId, request.OrgUnitId, p.Id, amounts[i])).ToList(), cancellationToken);
        audit.Record(db, AuditEntityType, version.Id, "Spread", after: new { request.AccountId, request.OrgUnitId, request.AnnualAmount, Method = request.Method.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await GridAsync(db, versionId, null, cancellationToken);
    }

    public async Task<BudgetImportResultDto> ImportCsvAsync(Guid versionId, ImportBudgetCsvRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (version, periods) = await EditableAsync(db, versionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Csv))
        {
            throw new ArgumentException("The file is empty.");
        }

        if (request.Csv.Length > 5_000_000)
        {
            throw new ArgumentException("The file is too large (5 MB at most).");
        }

        var rows = Csv.Parse(request.Csv);
        if (rows.Count < 2)
        {
            throw new ArgumentException("The file needs a header row and at least one row of amounts.");
        }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var (accountCol, orgCol, periodCol, amountCol, annualCol) = (Col("account", "account number"), Col("org", "org unit", "orgunit", "organization"),
            Col("period", "fiscal period"), Col("amount"), Col("annual", "annual amount"));
        if (accountCol < 0 || orgCol < 0 || (amountCol < 0 && annualCol < 0) || (amountCol >= 0 && periodCol < 0))
        {
            throw new ArgumentException("The header must be account,org,period,amount (one amount per period) or account,org,annual (spread evenly).");
        }

        var accounts = (await BudgetMath.BudgetableAccountsAsync(db, cancellationToken)).ToDictionary(a => a.Number, StringComparer.OrdinalIgnoreCase);
        var orgs = (await db.Set<OrgUnit>().AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(o => o.Code, o => o.Id, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var cells = new Dictionary<(Guid, Guid, Guid), decimal>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string Cell(int col) => col >= 0 && col < row.Count ? row[col].Trim() : "";
            var line = $"Row {i + 1}";
            if (!accounts.TryGetValue(Cell(accountCol), out var account))
            {
                errors.Add($"{line}: account \"{Cell(accountCol)}\" isn't an active revenue or expense detail account.");
                continue;
            }

            if (!orgs.TryGetValue(Cell(orgCol), out var org))
            {
                errors.Add($"{line}: org unit \"{Cell(orgCol)}\" doesn't exist.");
                continue;
            }

            if (amountCol >= 0)
            {
                var periodText = Cell(periodCol);
                var period = periods.FirstOrDefault(p => string.Equals(p.Name, periodText, StringComparison.OrdinalIgnoreCase))
                    ?? (int.TryParse(periodText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? periods.FirstOrDefault(p => p.Number == n) : null);
                if (period is null)
                {
                    errors.Add($"{line}: period \"{periodText}\" isn't in this fiscal year (use its name, like {periods.FirstOrDefault()?.Name}, or its number 1–{periods.Count}).");
                    continue;
                }

                if (!TryAmount(Cell(amountCol), out var amount))
                {
                    errors.Add($"{line}: \"{Cell(amountCol)}\" isn't an amount in whole cents.");
                    continue;
                }

                var key = (account.Id, org, period.Id);
                cells[key] = cells.GetValueOrDefault(key) + amount;
            }
            else
            {
                if (!TryAmount(Cell(annualCol), out var annual))
                {
                    errors.Add($"{line}: \"{Cell(annualCol)}\" isn't an amount in whole cents.");
                    continue;
                }

                var spread = BudgetMath.Spread(annual, periods.Count, SpreadMethod.Evenly);
                for (var p = 0; p < periods.Count; p++)
                {
                    var key = (account.Id, org, periods[p].Id);
                    cells[key] = cells.GetValueOrDefault(key) + spread[p];
                }
            }

            if (errors.Count >= 50)
            {
                errors.Add("Stopped after 50 problems.");
                break;
            }
        }

        if (errors.Count > 0)
        {
            return new BudgetImportResultDto(0, rows.Count - 1, errors);
        }

        if (request.Replace)
        {
            db.Set<GlBudgetLine>().RemoveRange(await db.Set<GlBudgetLine>().Where(l => l.VersionId == version.Id).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }

        var set = await SetCellsAsync(db, version, periods, cells.Select(c => (c.Key.Item1, c.Key.Item2, c.Key.Item3, c.Value)).ToList(), cancellationToken);
        audit.Record(db, AuditEntityType, version.Id, "Imported", after: new { Rows = rows.Count - 1, Cells = set, request.Replace });
        await db.SaveChangesAsync(cancellationToken);
        return new BudgetImportResultDto(set, rows.Count - 1, []);
    }

    private static bool TryAmount(string text, out decimal amount)
    {
        var t = text.Replace("$", "").Replace(",", "").Trim();
        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative)
        {
            t = t[1..^1];
        }

        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || decimal.Round(amount, 2) != amount)
        {
            return false;
        }

        amount = negative ? -amount : amount;
        return true;
    }

    public async Task<BudgetVersionDto> AdoptAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var version = await db.Set<GlBudgetVersion>().FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Budget version", versionId);
        version.EnsureEditable();
        if (await db.Set<PlanningCycle>().AnyAsync(c => c.VersionId == versionId && c.Status == PlanningCycleStatus.Open, cancellationToken))
        {
            throw new InvalidOperationException($"{version.Name} belongs to an open planning cycle; adopt it from the cycle once every org unit is approved.");
        }

        Adopt(db, version, audit, UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, [version], cancellationToken))[0];
    }

    internal static void Adopt(IWorkbenchDbContext db, GlBudgetVersion version, AuditRecorder audit, DateTime now)
    {
        version.Status = GlBudgetStatus.Adopted;
        version.AdoptedBy = audit.Actor;
        version.AdoptedAtUtc = now;
        audit.Record(db, AuditEntityType, version.Id, "Adopted", new { Status = nameof(GlBudgetStatus.Draft) }, new { Status = nameof(GlBudgetStatus.Adopted) }, "Adopted and locked");
    }

    // ---- Helpers ----

    private static async Task<(GlBudgetVersion Version, List<FiscalPeriod> Periods)> EditableAsync(IWorkbenchDbContext db, Guid versionId, CancellationToken ct)
    {
        var version = await db.Set<GlBudgetVersion>().FirstOrDefaultAsync(v => v.Id == versionId, ct) ?? throw WorkbenchNotFoundException.For("Budget version", versionId);
        version.EnsureEditable();
        return (version, await BudgetMath.PeriodsAsync(db, version.FiscalYearId, ct));
    }

    /// <summary>Upserts cells (0 deletes) after checking accounts, org units, periods and cents. Returns how many cells were set.</summary>
    internal async Task<int> SetCellsAsync(IWorkbenchDbContext db, GlBudgetVersion version, IReadOnlyList<FiscalPeriod> periods,
        IReadOnlyList<(Guid AccountId, Guid OrgUnitId, Guid PeriodId, decimal Amount)> cells, CancellationToken ct) =>
        await SetLinesAsync(db, version, periods, cells, audit.Actor, UtcNow, ct);

    internal static async Task<int> SetLinesAsync(IWorkbenchDbContext db, GlBudgetVersion version, IReadOnlyList<FiscalPeriod> periods,
        IReadOnlyList<(Guid AccountId, Guid OrgUnitId, Guid PeriodId, decimal Amount)> cells, string actor, DateTime now, CancellationToken ct)
    {
        version.EnsureEditable();
        var budgetable = (await BudgetMath.BudgetableAccountsAsync(db, ct)).ToDictionary(a => a.Id);
        var orgIds = cells.Select(c => c.OrgUnitId).Distinct().ToList();
        var orgs = (await db.Set<OrgUnit>().AsNoTracking().Where(o => orgIds.Contains(o.Id)).Select(o => o.Id).ToListAsync(ct)).ToHashSet();
        var periodIds = periods.Select(p => p.Id).ToHashSet();
        var accountIds = cells.Select(c => c.AccountId).Distinct().ToList();
        var existing = (await db.Set<GlBudgetLine>().Where(l => l.VersionId == version.Id && accountIds.Contains(l.AccountId)).ToListAsync(ct))
            .ToDictionary(l => (l.AccountId, l.OrgUnitId, l.FiscalPeriodId));
        foreach (var (accountId, orgUnitId, periodId, amount) in cells)
        {
            BudgetMath.RequireBudgetable(budgetable, accountId, "A cell");
            if (!orgs.Contains(orgUnitId))
            {
                throw new ArgumentException("Choose an org unit for every budget line.");
            }

            if (!periodIds.Contains(periodId))
            {
                throw new ArgumentException("A budget cell's period isn't in the version's fiscal year.");
            }

            PlanningMapping.RequireCents(amount, $"{budgetable[accountId].Number}");
            var key = (accountId, orgUnitId, periodId);
            if (existing.TryGetValue(key, out var line))
            {
                if (amount == 0)
                {
                    db.Set<GlBudgetLine>().Remove(line);
                    existing.Remove(key);
                }
                else
                {
                    line.Amount = amount;
                    line.UpdatedBy = actor;
                    line.UpdatedAtUtc = now;
                }
            }
            else if (amount != 0)
            {
                line = new GlBudgetLine { VersionId = version.Id, AccountId = accountId, OrgUnitId = orgUnitId, FiscalPeriodId = periodId, Amount = amount, UpdatedBy = actor, UpdatedAtUtc = now };
                db.Set<GlBudgetLine>().Add(line);
                existing[key] = line;
            }
        }

        return cells.Count;
    }

    private async Task<BudgetGridDto> GridAsync(IWorkbenchDbContext db, Guid versionId, Guid? orgUnitId, CancellationToken ct)
    {
        var version = await db.Set<GlBudgetVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct) ?? throw WorkbenchNotFoundException.For("Budget version", versionId);
        var periods = await BudgetMath.PeriodsAsync(db, version.FiscalYearId, ct);
        var lines = await db.Set<GlBudgetLine>().AsNoTracking().Where(l => l.VersionId == versionId && (orgUnitId == null || l.OrgUnitId == orgUnitId)).ToListAsync(ct);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Code, ct);
        var index = periods.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var rows = lines.GroupBy(l => (l.AccountId, l.OrgUnitId)).Select(g =>
            {
                var amounts = new decimal[periods.Count];
                foreach (var l in g)
                {
                    if (index.TryGetValue(l.FiscalPeriodId, out var i))
                    {
                        amounts[i] += l.Amount;
                    }
                }

                var a = accounts[g.Key.AccountId];
                return new BudgetGridRowDto(a.Id, a.Number, a.Name, a.Type.ToContract(), g.Key.OrgUnitId, orgs.GetValueOrDefault(g.Key.OrgUnitId) ?? "?", amounts);
            })
            .OrderBy(r => r.AccountNumber, StringComparer.Ordinal).ThenBy(r => r.OrgUnitCode, StringComparer.Ordinal).ToList();
        return new BudgetGridDto((await MapAsync(db, [version], ct))[0], periods.Select(Period).ToList(), rows);
    }

    internal static BudgetPeriodDto Period(FiscalPeriod p) => new(p.Id, p.Number, p.Name, p.Start, p.End);

    internal static async Task<List<BudgetVersionDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<GlBudgetVersion> versions, CancellationToken ct)
    {
        var ids = versions.Select(v => v.Id).ToList();
        var years = await db.Set<FiscalYear>().AsNoTracking().ToDictionaryAsync(y => y.Id, y => y.Year, ct);
        var types = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Type, ct);
        // Summed in memory: SQLite can't aggregate decimals.
        var totals = (await db.Set<GlBudgetLine>().AsNoTracking().Where(l => ids.Contains(l.VersionId)).Select(l => new { l.VersionId, l.AccountId, l.Amount }).ToListAsync(ct))
            .GroupBy(l => new { l.VersionId, l.AccountId }).Select(g => new { g.Key.VersionId, g.Key.AccountId, Amount = g.Sum(l => l.Amount), Count = g.Count() })
            .ToLookup(t => t.VersionId);
        var sourceIds = versions.Where(v => v.SourceVersionId != null).Select(v => v.SourceVersionId!.Value).ToList();
        var sources = await db.Set<GlBudgetVersion>().AsNoTracking().Where(v => sourceIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var cycles = await db.Set<PlanningCycle>().AsNoTracking().Where(c => ids.Contains(c.VersionId)).ToDictionaryAsync(c => c.VersionId, c => c.Id, ct);
        return versions.Select(v =>
        {
            var t = totals[v.Id].ToList();
            return new BudgetVersionDto(v.Id, years.GetValueOrDefault(v.FiscalYearId), v.Kind.ToContract(), v.Name, v.Description, v.Status.ToContract(), v.SourceVersionId,
                v.SourceVersionId is { } s ? sources.GetValueOrDefault(s) : null,
                t.Where(x => types.GetValueOrDefault(x.AccountId) == AccountType.Revenue).Sum(x => x.Amount),
                t.Where(x => types.GetValueOrDefault(x.AccountId) == AccountType.Expense).Sum(x => x.Amount),
                t.Sum(x => x.Count), v.CreatedBy, v.CreatedAtUtc, v.AdoptedBy, v.AdoptedAtUtc, cycles.TryGetValue(v.Id, out var c) ? c : null);
        }).ToList();
    }
}

/// <summary>A small RFC 4180 reader: commas, quoted fields with doubled quotes, CRLF or LF.</summary>
internal static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        // A UTF-8 byte-order mark from Excel.
        if (rows.Count > 0 && rows[0].Count > 0)
        {
            rows[0][0] = rows[0][0].TrimStart('﻿');
        }

        return rows;
    }
}
