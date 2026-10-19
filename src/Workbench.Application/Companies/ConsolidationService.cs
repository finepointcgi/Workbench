using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Companies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;
using PostedLine = Workbench.Application.Accounting.AccountingReportService.PostedLine;
using ReportLedger = Workbench.Application.Accounting.AccountingReportService.Ledger;

namespace Workbench.Application.Companies;

/// <summary>
/// Consolidated statements: each company's statement, the eliminations and the total, computed with the same rules as the
/// single-company reports (<see cref="AccountingReportService"/>). Eliminations remove every line tagged with a
/// counterparty when both companies are in the group, which takes out the due-to/due-from balances the ledger made (and
/// any intercompany revenue and cost entered with a counterparty).
/// </summary>
public sealed class ConsolidationService(IWorkbenchDbContextFactory dbFactory, CompanyScope scope) : IConsolidationService
{
    private sealed record Sum(Guid Company, Guid? Counterparty, Guid AccountId, Guid PeriodId, decimal Debit, decimal Credit);

    public async Task<ConsolidationDto> GetAsync(ConsolidatedReport report, ConsolidationRequest request, CancellationToken cancellationToken = default)
    {
        var view = await scope.GetAsync(cancellationToken);
        var wanted = request.CompanyIds is { Count: > 0 } ids ? ids.ToHashSet() : null;
        if (wanted is not null && wanted.Any(id => !view.Companies.Any(c => c.Id == id) || !view.CanUse(id)))
        {
            throw new ArgumentException("Choose companies you can use.");
        }

        var companies = view.Companies.Where(c => wanted?.Contains(c.Id) ?? (view.CanUse(c.Id) && (c.IsActive || c.IsDefault))).ToList();
        if (companies.Count == 0)
        {
            // An unmigrated database has no company rows yet: everything is in the default company.
            companies = [new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main company" }];
        }

        var group = companies.Select(c => c.Id).ToHashSet();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var lookups = await LedgerLookups.LoadAsync(db, cancellationToken);
        if (!lookups.Periods.TryGetValue(request.FiscalPeriodId, out var period))
        {
            throw WorkbenchNotFoundException.For("Fiscal period", request.FiscalPeriodId);
        }

        var years = await db.Set<FiscalYear>().AsNoTracking().ToDictionaryAsync(y => y.Id, cancellationToken);
        var groupIds = group.ToList();
        var sums = (await (from l in db.Set<JournalLine>().AsNoTracking()
                    join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                    where e.Status != JournalStatus.Draft && groupIds.Contains(l.CompanyId)
                    group l by new { l.CompanyId, l.IntercompanyCompanyId, l.AccountId, e.FiscalPeriodId } into g
                    select new { g.Key.CompanyId, g.Key.IntercompanyCompanyId, g.Key.AccountId, g.Key.FiscalPeriodId, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit) })
                .ToListAsync(cancellationToken))
            .Select(x => new Sum(x.CompanyId, x.IntercompanyCompanyId, x.AccountId, x.FiscalPeriodId, x.Debit, x.Credit))
            .ToList();

        ReportLedger Ledger(IEnumerable<Sum> lines, bool negate = false) => new(lookups,
            lines.Select(s => new PostedLine(Guid.Empty, "", 0, default, s.PeriodId, default, "", 0, s.AccountId, null, null, null,
                negate ? s.Credit : s.Debit, negate ? s.Debit : s.Credit, null)).ToList(), years);

        var eliminated = sums.Where(s => s.Counterparty is { } cp && group.Contains(cp)).ToList();
        var ledgers = companies.Select(c => Ledger(sums.Where(s => s.Company == c.Id))).ToList();
        var elimination = Ledger(eliminated, negate: true);
        var total = Ledger(sums.Concat(eliminated.Select(s => s with { Debit = s.Credit, Credit = s.Debit })));

        var (title, label, rows) = report switch
        {
            ConsolidatedReport.TrialBalance => ("Consolidated trial balance", "Ending balance",
                Merge(TrialBalanceRows(total, period.Id), ledgers.Select(l => TrialBalanceRows(l, period.Id)).ToList(), TrialBalanceRows(elimination, period.Id), withTrialTotals: true)),
            ConsolidatedReport.IncomeStatement => ("Consolidated income statement", "Year to date",
                Merge(StatementRows(AccountingReportService.IncomeStatement(total, period.Id)),
                    ledgers.Select(l => StatementRows(AccountingReportService.IncomeStatement(l, period.Id))).ToList(),
                    StatementRows(AccountingReportService.IncomeStatement(elimination, period.Id)))),
            ConsolidatedReport.BalanceSheet => ("Consolidated balance sheet", "Balance at period end",
                Merge(StatementRows(AccountingReportService.BalanceSheet(total, period.Id)),
                    ledgers.Select(l => StatementRows(AccountingReportService.BalanceSheet(l, period.Id))).ToList(),
                    StatementRows(AccountingReportService.BalanceSheet(elimination, period.Id)))),
            _ => throw new ArgumentException("Choose a report."),
        };

        var codes = view.Companies.ToDictionary(c => c.Id, c => c.Code);
        string Code(Guid id) => codes.GetValueOrDefault(id, "MAIN");
        var yearStart = years.TryGetValue(period.FiscalYearId, out var year) ? year.Start : period.Start;
        var eliminations = eliminated
            .Where(s => lookups.Periods.TryGetValue(s.PeriodId, out var p) && p.Start <= period.Start
                && (!IsIncomeStatement(lookups.Accounts[s.AccountId]) || p.Start >= yearStart))
            .GroupBy(s => (s.Company, Counterparty: s.Counterparty!.Value, s.AccountId))
            .Select(g => (g.Key, Amount: g.Sum(s => s.Credit - s.Debit)))
            .Where(x => x.Amount != 0)
            .Select(x => new EliminationDto(Code(x.Key.Company), Code(x.Key.Counterparty), lookups.Accounts[x.Key.AccountId].Number, lookups.Accounts[x.Key.AccountId].Name, x.Amount))
            .OrderBy(e => e.CompanyCode, StringComparer.Ordinal).ThenBy(e => e.CounterpartyCode, StringComparer.Ordinal).ThenBy(e => e.AccountNumber, StringComparer.Ordinal)
            .ToList();

        var notes = new List<string>();
        var balances = await IntercompanyService.IntercompanyBalancesAsync(db, period.End, groupIds, cancellationToken);
        foreach (var pair in balances.Keys.Where(k => group.Contains(k.Counterparty) && Code(k.Company).CompareTo(Code(k.Counterparty)) < 0))
        {
            var difference = balances[pair] + balances.GetValueOrDefault((pair.Counterparty, pair.Company));
            if (difference != 0)
            {
                notes.Add($"{Code(pair.Company)} and {Code(pair.Counterparty)} disagree about what they owe each other by {difference:N2}. "
                    + "The difference stays in the consolidated balance sheet until the entries are corrected.");
            }
        }

        var outside = balances.Where(kv => !group.Contains(kv.Key.Counterparty) && kv.Value != 0).Select(kv => Code(kv.Key.Counterparty)).Distinct().Order(StringComparer.Ordinal).ToList();
        if (outside.Count > 0)
        {
            notes.Add($"Balances with {string.Join(", ", outside)} aren't eliminated because {(outside.Count == 1 ? "that company isn't" : "those companies aren't")} in this consolidation.");
        }

        return new ConsolidationDto(report, title, period.Id, period.Name, label,
            companies.Select(c => new ConsolidationCompanyDto(c.Id, c.Code, c.LegalName)).ToList(), rows, eliminations, notes);
    }

    private static bool IsIncomeStatement(Account a) => a.Type is AccountType.Revenue or AccountType.Expense;

    private sealed record Row(string Section, string? AccountNumber, string Label, decimal Amount, bool IsTotal);

    private static List<Row> TrialBalanceRows(ReportLedger ledger, Guid periodId) =>
        AccountingReportService.TrialBalance(ledger, periodId).Rows
            .Select(r => new Row(SectionOf(r.Type), r.AccountNumber.Length == 0 ? null : r.AccountNumber, r.AccountName, r.Ending, false))
            .ToList();

    private static string SectionOf(AccountKind kind) => kind switch
    {
        AccountKind.Asset => "Assets",
        AccountKind.Liability => "Liabilities",
        AccountKind.Equity => "Equity",
        AccountKind.Revenue => "Revenue",
        _ => "Expenses",
    };

    private static List<Row> StatementRows(FinancialStatementDto statement) =>
        statement.Lines.Select(l => new Row(l.Section, l.AccountNumber, l.Label, l.YearToDate, l.IsTotal)).ToList();

    /// <summary>
    /// Lines the reports up by section and account. Sections keep the order they first appear in; inside one, accounts by
    /// number, then unnumbered lines, then totals, each in first-seen order. A line missing from a report is zero there.
    /// </summary>
    private static List<ConsolidationRowDto> Merge(List<Row> total, IReadOnlyList<List<Row>> companies, List<Row> eliminations, bool withTrialTotals = false)
    {
        static (string, string, string, bool) Key(Row r) => (r.Section, r.AccountNumber ?? "", r.Label, r.IsTotal);
        var all = total.Concat(companies.SelectMany(c => c)).Concat(eliminations).ToList();
        var sections = all.Select(r => r.Section).Distinct().ToList();
        var ordered = new List<Row>();
        foreach (var section in sections)
        {
            var rows = all.Where(r => r.Section == section).DistinctBy(Key).ToList();
            ordered.AddRange(rows.Where(r => !r.IsTotal && r.AccountNumber is not null).OrderBy(r => r.AccountNumber, StringComparer.Ordinal));
            ordered.AddRange(rows.Where(r => !r.IsTotal && r.AccountNumber is null));
            ordered.AddRange(rows.Where(r => r.IsTotal));
        }

        static decimal Amount(List<Row> rows, Row key) => rows.Where(r => Key(r) == Key(key)).Sum(r => r.Amount);
        var result = ordered
            .Select(r => new ConsolidationRowDto(r.Section, r.AccountNumber, r.Label, companies.Select(c => Amount(c, r)).ToList(),
                Amount(eliminations, r), Amount(total, r), r.IsTotal))
            .Where(r => r.IsTotal || r.Total != 0 || r.Eliminations != 0 || r.Companies.Any(a => a != 0))
            .ToList();
        if (withTrialTotals)
        {
            ConsolidationRowDto Totals(string label, Func<decimal, bool> side) => new("Totals", null, label,
                companies.Select((_, i) => result.Sum(r => side(r.Companies[i]) ? Math.Abs(r.Companies[i]) : 0)).ToList(),
                result.Sum(r => side(r.Eliminations) ? Math.Abs(r.Eliminations) : 0),
                result.Sum(r => side(r.Total) ? Math.Abs(r.Total) : 0), true);
            result.Add(Totals("Total debits", a => a > 0));
            result.Add(Totals("Total credits", a => a < 0));
        }

        return result;
    }
}
