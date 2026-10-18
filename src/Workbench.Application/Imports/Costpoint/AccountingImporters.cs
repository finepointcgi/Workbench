using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.Imports;
using ImportKind = Workbench.Domain.Imports.ImportKind;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>
/// Costpoint chart of accounts (Manage Accounts / ACCT export) → GL accounts, matched on the account number. Summary
/// accounts are the ones other rows name as their parent; parents are created first.
/// </summary>
public sealed class AccountImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.Accounts;
    public override string Title => "Chart of accounts";
    public override string Description => "GL accounts with their type, parent (summary) account and cost class. Import before opening balances.";
    public override string? SampleFile => "samples/import/costpoint/accounts.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("AccountNumber", "Account", true, "Natural key: the Costpoint account id.", "ACCT_ID", "Account ID", "Account Number", "Acct", "Acct ID", "Account No"),
        new("Name", "Account name", true, "", "ACCT_NAME", "Account Name", "ACCT_DESC", "Description", "Name"),
        new("Type", "Type", false, "Asset, Liability, Equity (Capital), Revenue or Expense, or A/L/C/R/E. Blank: from the first digit (1–5).", "ACCT_TYPE_CD", "Account Type", "Acct Type", "Type"),
        new("Parent", "Parent account", false, "The summary account it rolls up into (in the file or in Workbench).", "PARENT_ACCT_ID", "S_ACCT_ID", "Parent Account", "Summary Account", "Parent"),
        new("CostClass", "Cost class", false, "Expense accounts: Direct Labor, Direct ODC, Indirect Labor, Fringe, Overhead, G&A, Unallowable or Other.", "COST_CLASS", "Cost Class", "Cost Type", "Pool", "Function"),
        new("NormalBalance", "Normal balance", false, "Debit or Credit. Defaults from the type.", "NORMAL_BAL", "Normal Balance", "Balance Side"),
        new("Active", "Active", false, "Y/N. Defaults to Y.", "ACTIVE_FL", "Active Flag", "Active", "Status"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var chart = Service<IChartOfAccountsService>(context);
        var existing = (await chart.ListAccountsAsync(includeInactive: true, cancellationToken)).ToDictionary(a => a.Number, StringComparer.OrdinalIgnoreCase);
        var ids = existing.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
        var parentOf = rows.Where(r => r.Get("AccountNumber") is not null)
            .GroupBy(r => r.Get("AccountNumber")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Get("Parent"), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.OrderBy(r => DepthInFile(r.Get("AccountNumber"), parentOf)))
        {
            var number = row.Required("AccountNumber");
            var name = row.Required("Name");
            var type = ParseType(row, number);
            var costClass = ParseCostClass(row);
            var side = ParseSide(row);
            var active = row.Bool("Active") ?? true;
            if (row.HasErrors || number is null || name is null || type is null)
            {
                continue;
            }

            if (!seen.Add(number))
            {
                row.Error("AccountNumber", $"Account {number} appears more than once in the file.");
                continue;
            }

            Guid? parentId = null;
            if (row.Get("Parent") is { } parent)
            {
                if (string.Equals(parent, number, StringComparison.OrdinalIgnoreCase))
                {
                    row.Error("Parent", "An account can't be its own parent.");
                    continue;
                }

                if (!ids.TryGetValue(parent, out var pid))
                {
                    row.Error("Parent", $"Parent account {parent} isn't in the file or in Workbench.");
                    continue;
                }

                parentId = pid;
            }

            var request = new SaveAccountRequest(number, name, type.Value, parentId, type == AccountKind.Expense ? costClass : CostClass.None, side);
            if (existing.TryGetValue(number, out var account))
            {
                var changed = Changed(("Name", account.Name, name), ("Type", account.Type, type), ("Parent", account.ParentId, parentId),
                    ("Cost class", account.CostClass, request.CostClass), ("Normal balance", account.NormalBalance, side ?? account.NormalBalance),
                    ("Active", account.IsActive, active));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Account", number, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Account), account.Id, "Account", number, UpdateSummary(changed), async () =>
                {
                    await chart.UpdateAccountAsync(account.Id, request, cancellationToken);
                    if (account.IsActive != active)
                    {
                        await chart.SetActiveAsync(account.Id, active, cancellationToken);
                    }
                }, cancellationToken);
                continue;
            }

            var id = await context.Run.CreateAsync(row.FileName, row.Number, typeof(Account), "Account", number, $"{name} ({type})", async () =>
            {
                var created = await chart.CreateAccountAsync(request, cancellationToken);
                if (!active)
                {
                    await chart.SetActiveAsync(created.Id, false, cancellationToken);
                }

                return created.Id;
            }, cancellationToken);
            if (id is { } createdId)
            {
                ids[number] = createdId;
            }
        }
    }

    private static int DepthInFile(string? number, Dictionary<string, string?> parentOf)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (number is not null && parentOf.TryGetValue(number, out var parent) && parent is not null && seen.Add(number) && depth < 50)
        {
            depth++;
            number = parent;
        }

        return depth;
    }

    private static AccountKind? ParseType(MappedRow row, string? number)
    {
        var text = row.Get("Type");
        if (text is null)
        {
            if (number is { Length: > 0 } && char.IsDigit(number[0]))
            {
                var inferred = number[0] switch
                {
                    '1' => AccountKind.Asset,
                    '2' => AccountKind.Liability,
                    '3' => AccountKind.Equity,
                    '4' => AccountKind.Revenue,
                    _ => AccountKind.Expense,
                };
                if (row.IsMapped("Type") || number[0] != '0')
                {
                    row.Warning("Type", $"No type given; {number} is treated as {inferred} from its first digit.");
                }

                return inferred;
            }

            row.Error("Type", "Map an account type column (the account number doesn't start with a digit).");
            return null;
        }

        switch (FieldMatcher.Normalize(text))
        {
            case "a" or "asset" or "assets":
                return AccountKind.Asset;
            case "l" or "liability" or "liabilities":
                return AccountKind.Liability;
            case "c" or "q" or "o" or "equity" or "capital" or "networth" or "fundbalance" or "ownersequity":
                return AccountKind.Equity;
            case "r" or "i" or "revenue" or "income" or "sales":
                return AccountKind.Revenue;
            case "e" or "x" or "expense" or "expenses" or "cost" or "costs":
                return AccountKind.Expense;
            default:
                row.Error("Type", $"Type '{text}' isn't Asset, Liability, Equity, Revenue or Expense.");
                return null;
        }
    }

    private static CostClass ParseCostClass(MappedRow row)
    {
        var text = row.Get("CostClass");
        if (text is null)
        {
            return CostClass.None;
        }

        switch (FieldMatcher.Normalize(text))
        {
            case "dl" or "directlabor" or "labor":
                return CostClass.DirectLabor;
            case "odc" or "directodc" or "otherdirect" or "otherdirectcost" or "otherdirectcosts" or "directmaterial" or "materials" or "subcontract" or "subcontracts":
                return CostClass.DirectOdc;
            case "il" or "indirectlabor":
                return CostClass.IndirectLabor;
            case "fr" or "fringe" or "fringebenefits":
                return CostClass.Fringe;
            case "oh" or "overhead":
                return CostClass.Overhead;
            case "ga" or "gna" or "generalandadministrative" or "generaladministrative":
                return CostClass.GeneralAndAdministrative;
            case "ua" or "unallowable" or "unallow":
                return CostClass.Unallowable;
            case "other":
                return CostClass.Other;
            case "none" or "":
                return CostClass.None;
            default:
                row.Warning("CostClass", $"Cost class '{text}' isn't recognized; set it on the account later.");
                return CostClass.None;
        }
    }

    private static BalanceSide? ParseSide(MappedRow row) => row.Get("NormalBalance") is { } text
        ? FieldMatcher.Normalize(text) switch
        {
            "d" or "dr" or "debit" => BalanceSide.Debit,
            "c" or "cr" or "credit" => BalanceSide.Credit,
            _ => null,
        }
        : null;
}

/// <summary>
/// A Costpoint trial balance as of the cut-over date (account × org × project, ending balances) → a posted opening
/// balance journal entry dated <see cref="Workbench.Contracts.Imports.ImportOptionsDto.AsOfDate"/>. The file must balance
/// to the penny. A file longer than one entry (500 lines) is split into parts that each balance through the offset
/// (clearing) account, which nets to zero.
/// </summary>
public sealed class OpeningBalanceImporter : CostpointImporter
{
    private const int MaxLinesPerEntry = 500;

    public override ImportKind Kind => ImportKind.OpeningBalances;
    public override string Title => "GL opening balances";
    public override string Description => "The trial balance at cut-over by account, org and project, posted as opening balance journal entries. Must balance.";
    public override string? SampleFile => "samples/import/costpoint/opening-trial-balance.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Account", "Account", true, "Account number (detail account).", "ACCT_ID", "Account ID", "Account Number", "Acct", "Account"),
        new("Org", "Org", false, "Org id of an existing org unit.", "ORG_ID", "Org ID", "Organization", "Org"),
        new("Project", "Project / WBS", false, "Project id; a dotted id (2001.02.01) posts to that WBS element.", "PROJ_ID", "Project ID", "Project", "WBS", "Project Code"),
        new("Balance", "Balance", false, "Ending balance, debit positive. Or map debit and credit.", "END_BAL", "Ending Balance", "Balance", "Net Balance", "YTD Balance", "Amount", "CUR_BAL"),
        new("Debit", "Debit", false, "Debit balance (positive).", "DR_AMT", "Debit Amount", "Debit", "Debits", "Ending Debit"),
        new("Credit", "Credit", false, "Credit balance (positive).", "CR_AMT", "Credit Amount", "Credit", "Credits", "Ending Credit"),
        new("Date", "As-of date", false, "Used when no as-of date is chosen; every row must have the same date.", "FY_END_DT", "As Of Date", "Period End", "Date"),
        new("Memo", "Memo", false, "Line memo.", "DESCRIPTION", "Memo", "Description", "ACCT_NAME", "Account Name"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var file = rows[0].FileName;
        if (!rows[0].IsMapped("Balance") && !rows[0].IsMapped("Debit") && !rows[0].IsMapped("Credit"))
        {
            context.Run.Error(file, 0, "Balance", "Map a balance column, or debit and credit columns.");
            return;
        }

        var chart = Service<IChartOfAccountsService>(context);
        var accounts = (await chart.ListAccountsAsync(includeInactive: true, cancellationToken)).ToDictionary(a => a.Number, StringComparer.OrdinalIgnoreCase);
        var orgs = (await Service<IPeopleService>(context).ListOrgUnitsAsync(cancellationToken)).ToDictionary(o => o.Code, o => o.Id, StringComparer.OrdinalIgnoreCase);
        var projectsService = Service<IProjectService>(context);
        var projects = (await projectsService.ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Code, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var wbsByProject = new Dictionary<Guid, IReadOnlyList<WbsElementDto>>();

        await RefuseSecondImportAsync(context, file, cancellationToken);

        var lines = new List<(MappedRow Row, JournalLineInput Line, decimal Net)>();
        var dates = new HashSet<DateOnly>();
        foreach (var row in rows)
        {
            var number = row.Required("Account");
            var balance = row.Decimal("Balance");
            var debit = row.Decimal("Debit");
            var credit = row.Decimal("Credit");
            var date = row.Date("Date");
            if (row.HasErrors || number is null)
            {
                continue;
            }

            if (date is { } d)
            {
                dates.Add(d);
            }

            var net = Math.Round(balance ?? (debit ?? 0m) - (credit ?? 0m), 2, MidpointRounding.AwayFromZero);
            if (net != (balance ?? (debit ?? 0m) - (credit ?? 0m)))
            {
                row.Error("Balance", "Amounts must be in whole cents.");
                continue;
            }

            if (!accounts.TryGetValue(number, out var account))
            {
                row.Error("Account", $"Account {number} isn't in Workbench; import the chart of accounts first.");
                continue;
            }

            if (!account.IsActive || account.HasChildren)
            {
                row.Error("Account", $"Account {number} is {(account.HasChildren ? "a summary account" : "inactive")}; opening balances go on active detail accounts.");
                continue;
            }

            Guid? orgId = null;
            if (row.Get("Org") is { } org)
            {
                if (!orgs.TryGetValue(org, out var oid))
                {
                    row.Error("Org", $"Org {org} isn't in Workbench.");
                    continue;
                }

                orgId = oid;
            }

            Guid? projectId = null, wbsId = null;
            if (row.Get("Project") is { } code)
            {
                var top = code.Split('.')[0];
                if (!projects.TryGetValue(code, out var pid) && !projects.TryGetValue(top, out pid))
                {
                    row.Error("Project", $"Project {code} isn't in Workbench.");
                    continue;
                }

                projectId = pid;
                if (!projects.ContainsKey(code))
                {
                    if (!wbsByProject.TryGetValue(pid, out var wbs))
                    {
                        wbsByProject[pid] = wbs = await projectsService.ListWbsAsync(pid, cancellationToken);
                    }

                    var element = wbs.FirstOrDefault(w => string.Equals(w.Code, code, StringComparison.OrdinalIgnoreCase));
                    if (element is null)
                    {
                        row.Error("Project", $"WBS {code} isn't in project {top}.");
                        continue;
                    }

                    wbsId = element.Id;
                }
            }

            if (net == 0)
            {
                context.Run.Skip(row.FileName, row.Number, "Opening balance", number, "Zero balance");
                continue;
            }

            var memo = row.Get("Memo") is { } m ? (m.Length > 200 ? m[..200] : m) : null;
            lines.Add((row, new JournalLineInput(account.Id, net > 0 ? net : 0, net < 0 ? -net : 0, memo, orgId, projectId, wbsId), net));
        }

        if (dates.Count > 1)
        {
            context.Run.Error(file, 0, "Date", $"The file has {dates.Count} different dates; choose one as-of date.");
            return;
        }

        var asOf = context.Options.AsOfDate ?? dates.Cast<DateOnly?>().FirstOrDefault() ?? await CutoverOpeningDateAsync(context, cancellationToken);
        if (asOf is null)
        {
            context.Run.Error(file, 0, "Date", "Choose the as-of date (the day before cut-over), map a date column or set the cut-over date on the go-live checklist.");
            return;
        }

        var periods = await Service<IFiscalCalendarService>(context).ListPeriodsAsync(null, cancellationToken);
        var period = periods.FirstOrDefault(p => p.Start <= asOf && asOf <= p.End);
        if (period is null)
        {
            context.Run.Error(file, 0, "Date", $"No fiscal period covers {asOf:yyyy-MM-dd}; set up that fiscal year first.");
            return;
        }

        if (period.Status == FiscalPeriodState.Closed)
        {
            context.Run.Error(file, 0, "Date", $"Fiscal period {period.Name} is closed; reopen it to post opening balances.");
            return;
        }

        if (lines.Count == 0 || context.Run.ErrorRows > 0)
        {
            if (context.Run.ErrorRows > 0)
            {
                context.Run.Error(file, 0, null, "Opening balances post as one balanced set: fix the rows with errors and run again.");
            }

            return;
        }

        var total = lines.Sum(l => l.Net);
        if (total != 0)
        {
            context.Run.Error(file, 0, "Balance",
                $"The trial balance doesn't balance: debits {lines.Where(l => l.Net > 0).Sum(l => l.Net):N2}, credits {-lines.Where(l => l.Net < 0).Sum(l => l.Net):N2} (difference {total:N2}).");
            return;
        }

        // Split long files into parts that each balance through the offset account.
        var parts = new List<List<(MappedRow Row, JournalLineInput Line, decimal Net)>>();
        if (lines.Count <= MaxLinesPerEntry)
        {
            parts.Add(lines);
        }
        else
        {
            if (context.Options.OffsetAccount is not { Length: > 0 } offsetNumber || !accounts.TryGetValue(offsetNumber, out var offset) || !offset.IsActive || offset.HasChildren)
            {
                context.Run.Error(file, 0, null,
                    $"{lines.Count} lines is more than one journal entry holds ({MaxLinesPerEntry}). Choose an active detail offset (clearing) account to split it; the parts net to zero on it.");
                return;
            }

            parts.AddRange(lines.Chunk(MaxLinesPerEntry - 1).Select(c => c.ToList()));
        }

        var journal = Service<IJournalService>(context);
        var reference = context.Job.Id.ToString()[..8];
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var inputs = part.Select(p => p.Line).ToList();
            var plug = -part.Sum(p => p.Net);
            if (plug != 0)
            {
                var offset = accounts[context.Options.OffsetAccount!];
                inputs.Add(new JournalLineInput(offset.Id, plug > 0 ? plug : 0, plug < 0 ? -plug : 0, "Opening balance clearing (nets to zero across the parts)"));
            }

            var label = parts.Count == 1 ? $"Opening balances {asOf:yyyy-MM-dd}" : $"Opening balances {asOf:yyyy-MM-dd} (part {i + 1} of {parts.Count})";
            var description = $"{label} from Costpoint (import job {reference})";
            var debits = inputs.Sum(l => l.Debit);
            var first = part[0].Row;
            var entryId = await context.Run.CreateAsync(first.FileName, first.Number, typeof(JournalEntry), "Journal entry", label,
                $"{inputs.Count} lines, {debits:N2} debits and credits in {period.Name}",
                async () => (await journal.CreateAndPostAsync(new SaveJournalEntryRequest(asOf.Value, description, inputs), cancellationToken)).Entry.Id, cancellationToken);
            if (entryId is { } id && context.Run.Applying)
            {
                // Lines are recorded after their entry, so rollback deletes them first.
                foreach (var line in (await journal.GetAsync(id, cancellationToken)).Lines)
                {
                    await context.Run.RecordCreatedAsync(typeof(JournalLine), line.Id, $"{label} line {line.LineNumber} ({line.AccountNumber})", cancellationToken);
                }
            }
        }
    }

    /// <summary>The day before the cut-over date on the go-live checklist, when one is set.</summary>
    private static async Task<DateOnly?> CutoverOpeningDateAsync(ImportContext context, CancellationToken cancellationToken)
    {
        await using var db = await Service<IWorkbenchDbContextFactory>(context).CreateAsync(cancellationToken);
        var cutover = await db.Set<Workbench.Domain.Migration.MigrationSettings>().AsNoTracking().Select(s => s.CutoverDate).FirstOrDefaultAsync(cancellationToken);
        return cutover?.AddDays(-1);
    }

    /// <summary>Opening balances go in once: a second file while another import of them stands would double the GL.</summary>
    private static async Task RefuseSecondImportAsync(ImportContext context, string file, CancellationToken cancellationToken)
    {
        await using var db = await Service<IWorkbenchDbContextFactory>(context).CreateAsync(cancellationToken);
        var other = await db.Set<ImportJob>().AsNoTracking()
            .Where(j => j.Id != context.Job.Id && j.Kind == ImportKind.OpeningBalances && (j.Status == ImportJobStatus.Imported || j.Status == ImportJobStatus.Failed))
            .OrderByDescending(j => j.CreatedAtUtc).Select(j => (Guid?)j.Id).FirstOrDefaultAsync(cancellationToken);
        if (other is { } id)
        {
            context.Run.Error(file, 0, null, $"Opening balances were already imported by job {id.ToString()[..8]}. Roll that job back before importing them again.");
        }
    }
}
