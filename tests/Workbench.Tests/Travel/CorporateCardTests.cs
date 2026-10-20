using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.MyTodo;
using Workbench.Application.Travel;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Travel;
using Workbench.Domain.Accounting;
using Workbench.Tests.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Travel;

/// <summary>Corporate cards through the real registrations: import and de-duplication, attaching to reports, posting, aging.</summary>
public sealed class CorporateCardTests
{
    private const string Statement = """
        Transaction Date,Posted Date,Card Number,Description,Category,Amount
        10/06/2026,10/07/2026,XXXX-4417,UBER *TRIP DCA,4121,45.00
        10/06/2026,10/07/2026,XXXX-4417,STARBUCKS #1,5814,4.50
        10/06/2026,10/07/2026,XXXX-4417,STARBUCKS #1,5814,4.50
        10/07/2026,10/08/2026,XXXX-9999,OFFICE DEPOT,5943,20.00
        08/01/2026,08/02/2026,XXXX-4417,AMAZON MKTPLACE,5942,64.99
        """;

    private static async Task<(ResourcingHost Host, Guid ProgramId)> CardHostAsync(bool companyPaid = true)
    {
        var host = await TravelAuthorizationTests.HostAsync();
        var cards = host.As(Finance).Get<ICardProgramService>();
        await host.Get<IChartOfAccountsService>().InstallDefaultChartAsync();
        var program = await cards.CreateProgramAsync(new SaveCardProgramRequest("Citi", "Citibank", companyPaid ? CardPaymentKind.CompanyPaid : CardPaymentKind.EmployeePaid));
        await cards.CreateCardAsync(new SaveCorporateCardRequest(program.Id, host.AnalystId, "4417"));
        return (host, program.Id);
    }

    private static Task<CardImportResultDto> ImportAsync(ResourcingHost host, Guid programId, string content, string name = "statement.csv") =>
        host.As(Finance).Get<ICardProgramService>().ImportStatementAsync(programId, name, new MemoryStream(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task Importing_matches_cards_and_skips_rows_already_imported()
    {
        var (host, programId) = await CardHostAsync();
        await using var _ = host;

        var first = await ImportAsync(host, programId, Statement);
        Assert.Equal(("CSV", 5, 5, 0, 1), (first.Format, first.Rows, first.Imported, first.Duplicates, first.Unmatched)); // two identical coffees both import

        var again = await ImportAsync(host, programId, Statement);
        Assert.Equal((0, 5), (again.Imported, again.Duplicates));

        var overlap = await ImportAsync(host, programId, Statement + "\n10/08/2026,10/09/2026,XXXX-4417,PANERA BREAD,5814,12.10");
        Assert.Equal((1, 5), (overlap.Imported, overlap.Duplicates));

        // OFX: the bank's FITID is the key, so re-sent transactions are skipped even when their text changed.
        const string ofx = """
            OFXHEADER:100
            <OFX><CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><CCACCTFROM><ACCTID>4111111111114417</CCACCTFROM>
            <BANKTRANLIST><STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261009<TRNAMT>-210.00<FITID>F-1<NAME>HILTON ARLINGTON</STMTTRN>
            <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20261010<TRNAMT>15.00<FITID>F-2<NAME>REFUND</STMTTRN></BANKTRANLIST></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>
            """;
        var fromOfx = await ImportAsync(host, programId, ofx, "statement.ofx");
        Assert.Equal(("OFX", 2, 0), (fromOfx.Format, fromOfx.Imported, fromOfx.Unmatched));
        var resent = await ImportAsync(host, programId, ofx.Replace("HILTON ARLINGTON", "HILTON ARLINGTON VA"), "statement.ofx");
        Assert.Equal((0, 2), (resent.Imported, resent.Duplicates));

        var mine = await host.As(Analyst).Get<ICardChargeService>().ListMyChargesAsync();
        Assert.Contains(mine, c => c.Merchant == "HILTON ARLINGTON" && c.Amount == 210m && c.SuggestedCategory == TravelCategory.Lodging);
        Assert.Contains(mine, c => c.Merchant == "REFUND" && c.Amount == -15m);
        Assert.DoesNotContain(mine, c => c.Merchant == "OFFICE DEPOT"); // ••9999 isn't analyst's card

        // Adding the card later claims its earlier unmatched charges.
        await host.As(Finance).Get<ICardProgramService>().CreateCardAsync(new SaveCorporateCardRequest(programId, host.DevId, "9999"));
        Assert.Contains(await host.As(Dev).Get<ICardChargeService>().ListMyChargesAsync(), c => c.Merchant == "OFFICE DEPOT");
    }

    [Fact]
    public async Task A_company_paid_charge_posts_to_the_card_liability_not_to_reimbursement()
    {
        var (host, programId) = await CardHostAsync();
        await using var _ = host;
        await ImportAsync(host, programId, Statement);
        var cards = host.As(Analyst).Get<ICardChargeService>();
        var reports = host.Get<IExpenseReportService>();
        var report = await reports.CreateReportAsync(new SaveExpenseReportRequest("DC trip", new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7)));
        var taxi = (await cards.ListMyChargesAsync()).Single(c => c.Merchant == "UBER *TRIP DCA");

        var cardLine = await cards.AttachToReportAsync(taxi.Id, new AttachCardChargeRequest(report.Id, host.Design, Description: "Taxi DCA to hotel"));
        Assert.Equal((45m, new DateOnly(2026, 10, 6), "Taxi DCA to hotel"), (cardLine.Amount, cardLine.IncurredOn, cardLine.Description));
        Assert.DoesNotContain(await cards.ListMyChargesAsync(), c => c.Id == taxi.Id);
        Assert.Equal(CardChargeState.OnDraftReport, (await cards.ListMyChargesAsync(includeAssigned: true)).Single(c => c.Id == taxi.Id).State);
        var cashLine = await reports.AddLineAsync(report.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, new DateOnly(2026, 10, 7), 20m, "Parking", "SP+"));
        Assert.Equal([cardLine.Id], (await cards.ListReportCardLinesAsync(report.Id)).Select(l => l.OdcEntryId));

        await reports.SubmitAsync(report.Id);
        await host.As(Pm).Get<IOdcApprovalService>().ApproveReportAsync(report.Id);

        await using var db = await host.Db.CreateAsync();
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Number);
        async Task<string> CreditAccountAsync(Guid odcId)
        {
            var entry = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.Source == JournalSource.Odc && e.SourceId == odcId);
            return accounts[entry.Lines.Single(l => l.Credit > 0).AccountId];
        }

        Assert.Equal(CardSupport.LiabilityAccountNumber, await CreditAccountAsync(cardLine.Id)); // 2030 Corporate card payable
        Assert.Equal("2110", await CreditAccountAsync(cashLine.Id)); // Accrued employee reimbursements
        Assert.Equal(CardChargeState.Approved, (await host.As(Finance).Get<ICardProgramService>().ListTransactionsAsync()).Single(t => t.Id == taxi.Id).State);
    }

    [Fact]
    public async Task An_employee_paid_charge_is_reimbursed_like_any_expense()
    {
        var (host, programId) = await CardHostAsync(companyPaid: false);
        await using var _ = host;
        await ImportAsync(host, programId, Statement);
        var cards = host.As(Analyst).Get<ICardChargeService>();
        var reports = host.Get<IExpenseReportService>();
        var report = await reports.CreateReportAsync(new SaveExpenseReportRequest("DC trip"));
        var taxi = (await cards.ListMyChargesAsync()).Single(c => c.Merchant == "UBER *TRIP DCA");
        var line = await cards.AttachToReportAsync(taxi.Id, new AttachCardChargeRequest(report.Id, host.Design));
        await reports.SubmitAsync(report.Id);
        await host.As(Pm).Get<IOdcApprovalService>().ApproveReportAsync(report.Id);

        await using var db = await host.Db.CreateAsync();
        var entry = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.Source == JournalSource.Odc && e.SourceId == line.Id);
        var creditAccount = entry.Lines.Single(l => l.Credit > 0).AccountId;
        var credit = await db.Set<Account>().AsNoTracking().SingleAsync(a => a.Id == creditAccount);
        Assert.Equal("2110", credit.Number);
    }

    [Fact]
    public async Task A_card_lines_amount_is_locked_and_removing_it_returns_the_charge()
    {
        var (host, programId) = await CardHostAsync();
        await using var _ = host;
        await ImportAsync(host, programId, Statement);
        var cards = host.As(Analyst).Get<ICardChargeService>();
        var reports = host.Get<IExpenseReportService>();
        var report = await reports.CreateReportAsync(new SaveExpenseReportRequest("DC trip"));
        var taxi = (await cards.ListMyChargesAsync()).Single(c => c.Merchant == "UBER *TRIP DCA");
        var line = await cards.AttachToReportAsync(taxi.Id, new AttachCardChargeRequest(report.Id, host.Design));

        var locked = await Assert.ThrowsAsync<ArgumentException>(() =>
            reports.UpdateLineAsync(report.Id, line.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, line.IncurredOn, 50m, "Taxi")));
        Assert.Contains("locked", locked.Message, StringComparison.Ordinal);
        var described = await reports.UpdateLineAsync(report.Id, line.Id, new SaveExpenseLineRequest(host.Design, OdcCostType.Travel, line.IncurredOn, 45m, "Taxi to the customer site"));
        Assert.Equal("Taxi to the customer site", described.Description);
        await reports.AttachReceiptAsync(report.Id, line.Id, "uber.pdf", new MemoryStream([1, 2, 3])); // receipts upload as for any line

        await Assert.ThrowsAsync<ArgumentException>(() => cards.AttachToReportAsync(taxi.Id, new AttachCardChargeRequest(report.Id, host.Design)));
        await reports.RemoveLineAsync(report.Id, line.Id);
        Assert.Contains(await cards.ListMyChargesAsync(), c => c.Id == taxi.Id);

        await cards.AttachToReportAsync(taxi.Id, new AttachCardChargeRequest(report.Id, host.Design));
        await cards.DetachAsync(taxi.Id);
        Assert.Empty((await reports.GetReportAsync(report.Id)).Lines);
        Assert.Contains(await cards.ListMyChargesAsync(), c => c.Id == taxi.Id);

        // Credits don't go on reports.
        await ImportAsync(host, programId, "Date,Card,Merchant,Amount\n10/09/2026,4417,UBER REFUND,-5.00");
        host.As(Analyst);
        var refund = (await cards.ListMyChargesAsync()).Single(c => c.Amount < 0);
        await Assert.ThrowsAsync<ArgumentException>(() => cards.AttachToReportAsync(refund.Id, new AttachCardChargeRequest(report.Id, host.Design)));
    }

    [Fact]
    public async Task Card_setup_and_everyones_charges_are_for_Finance_and_charges_are_private_to_the_cardholder()
    {
        var (host, programId) = await CardHostAsync();
        await using var _ = host;
        await ImportAsync(host, programId, Statement);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Analyst).Get<ICardProgramService>().ListProgramsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Pm).Get<ICardProgramService>().ListTransactionsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.As(Analyst).Get<ICardProgramService>().ImportStatementAsync(programId, "s.csv", new MemoryStream(Encoding.UTF8.GetBytes(Statement))));

        var analystCharge = (await host.As(Analyst).Get<ICardChargeService>().ListMyChargesAsync()).First();
        var devReport = await host.As(Dev).Get<IExpenseReportService>().CreateReportAsync(new SaveExpenseReportRequest("Mine"));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            host.As(Dev).Get<ICardChargeService>().AttachToReportAsync(analystCharge.Id, new AttachCardChargeRequest(devReport.Id, host.Design)));
        Assert.Empty(await host.As(Dev).Get<ICardChargeService>().ListMyChargesAsync());
    }

    [Fact]
    public async Task Old_unsubmitted_charges_are_overdue_on_the_aging_and_the_home_page()
    {
        var (host, programId) = await CardHostAsync();
        await using var _ = host;
        await ImportAsync(host, programId, Statement);

        // Today is 2026-10-14; the Aug 1 purchase is 74 days old (the program allows 30).
        var aging = await host.As(Finance).Get<ICardProgramService>().GetAgingAsync();
        var analyst = aging.Rows.Single(r => r.EmployeeId == host.AnalystId);
        Assert.Equal((4, 54m, 64.99m, 1), (analyst.Count, analyst.Days0To30, analyst.Days61To90, analyst.Overdue));
        Assert.Contains(aging.Rows, r => r.EmployeeId == null && r.Cardholder == "Unmatched card");

        var summary = await host.As(Analyst).Get<ICardChargeService>().GetMySummaryAsync();
        Assert.Equal((4, 1, 30), (summary.Unassigned, summary.Overdue, summary.AlertDays));
        var todos = await host.Get<IEnumerable<IMyTodoProvider>>().OfType<CardChargeTodoProvider>().Single()
            .GetAsync(new MyTodoContext(await host.Get<Workbench.Contracts.Identity.ICurrentUserContext>().GetAsync(), Today, host.Get<IServiceProvider>(), host.Db), default);
        Assert.Equal(["cards:overdue", "cards:unassigned"], todos.Select(t => t.Key));

        var reconciliation = Assert.Single(await host.As(Finance).Get<ICardProgramService>().GetReconciliationAsync());
        Assert.Equal((5, 1, 1), (reconciliation.Count, reconciliation.Unmatched, reconciliation.Overdue));
        var excluded = await host.Get<ICardProgramService>().ExcludeTransactionAsync(
            (await host.Get<ICardProgramService>().ListTransactionsAsync(new CardTransactionQuery(UnmatchedOnly: true))).Single().Id, new ExcludeCardTransactionRequest("Not ours"));
        Assert.Equal(CardChargeState.Excluded, excluded.State);
    }
}
