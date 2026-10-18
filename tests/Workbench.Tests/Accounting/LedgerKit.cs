using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Accounting;

namespace Workbench.Tests.Accounting;

/// <summary>The general-ledger services over an existing test database, with the default chart installed.</summary>
internal sealed class LedgerKit
{
    private LedgerKit(IWorkbenchDbContextFactory factory, ICurrentUser user, TimeProvider clock)
    {
        Factory = factory;
        var audit = new AuditRecorder(user, clock);
        Ledger = new GeneralLedger(audit, clock);
        Engine = new PostingEngine(factory, Ledger, NullLogger<PostingEngine>.Instance);
        Chart = new ChartOfAccountsService(factory, audit, clock);
        Journal = new JournalService(factory, Ledger, audit);
        Posting = new PostingService(factory, Engine, audit, clock);
        Close = new PeriodCloseService(factory, Engine, audit);
        Reports = new AccountingReportService(factory);
    }

    public IWorkbenchDbContextFactory Factory { get; }
    public GeneralLedger Ledger { get; }
    public PostingEngine Engine { get; }
    public ChartOfAccountsService Chart { get; }
    public JournalService Journal { get; }
    public PostingService Posting { get; }
    public PeriodCloseService Close { get; }
    public AccountingReportService Reports { get; }
    public IReadOnlyDictionary<string, Guid> Accounts { get; private set; } = new Dictionary<string, Guid>();

    public static async Task<LedgerKit> CreateAsync(IWorkbenchDbContextFactory factory, ICurrentUser user, TimeProvider clock)
    {
        var kit = new LedgerKit(factory, user, clock);
        await kit.Chart.InstallDefaultChartAsync();
        kit.Accounts = (await kit.Chart.ListAccountsAsync()).ToDictionary(a => a.Number, a => a.Id);
        return kit;
    }

    public Guid this[string number] => Accounts[number];

    public async Task<List<JournalEntry>> EntriesAsync(JournalSource? source = null)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => source == null || e.Source == source)
            .OrderBy(e => e.Sequence).ToListAsync();
    }

    /// <summary>Net debit-positive balance of an account over all posted lines.</summary>
    public async Task<decimal> BalanceAsync(string number)
    {
        var lines = (await EntriesAsync()).Where(e => e.Status != JournalStatus.Draft).SelectMany(e => e.Lines).Where(l => l.AccountId == this[number]);
        return lines.Sum(l => l.Debit - l.Credit);
    }

    public SaveJournalEntryRequest Entry(DateOnly date, params (string Account, decimal Debit, decimal Credit)[] lines) =>
        new(date, "Test entry", lines.Select(l => new JournalLineInput(this[l.Account], l.Debit, l.Credit)).ToList());
}
