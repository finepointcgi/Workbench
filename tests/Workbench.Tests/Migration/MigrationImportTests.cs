using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Imports;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Purchasing;
using Workbench.Tests.Imports;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportKind = Workbench.Contracts.Imports.ImportKind;
using ImportSource = Workbench.Contracts.Imports.ImportSource;

namespace Workbench.Tests.Migration;

/// <summary>The go-live importers: chart of accounts, vendors, GL opening balances and open AR/AP (dry run, import, rollback).</summary>
public sealed class MigrationImportTests
{
    public static readonly DateOnly OpeningDate = new(2026, 10, 31);

    /// <summary>Every sample on the cut-over path, imported in checklist order (cut-over 2026-11-01).</summary>
    internal static async Task ImportCutoverSamplesAsync(ImportHost host)
    {
        await host.Get<IMigrationService>().SaveSettingsAsync(new SaveMigrationSettingsRequest(OpeningDate.AddDays(1), null));
        await Ok(host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv"));
        await host.ImportAllCostpointSamplesAsync();
        await Ok(host.ImportCostpointAsync(ImportKind.Vendors, "vendors.csv"));
        await Ok(host.ImportCostpointAsync(ImportKind.OpenApBills, "open-ap.csv"));
        await Ok(host.ImportCostpointAsync(ImportKind.OpenArInvoices, "open-ar.csv"));
        await Ok(host.ImportCostpointAsync(ImportKind.OpeningBalances, "opening-trial-balance.csv"));
    }

    private static async Task<ImportJobDetailDto> Ok(Task<ImportJobDetailDto> import)
    {
        var job = await import;
        Assert.True(job.Job.Status == ImportJobStatus.Imported, $"{job.Definition.Title}: {job.Job.Status} {job.Job.Message} {string.Join("; ", job.Errors.Select(e => $"{e.Row}: {e.Message}"))}");
        Assert.True(job.Job.Counts.Errors == 0, $"{job.Definition.Title}: {string.Join("; ", job.Errors.Select(e => $"{e.Row}: {e.Message}"))}");
        return job;
    }

    [Fact]
    public async Task Chart_of_accounts_dry_runs_imports_with_parents_and_rolls_back()
    {
        await using var host = await ImportHost.CreateAsync();
        var dry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.Accounts, [Samples.File("samples/import/costpoint/accounts.csv")]);
        Assert.Equal(ImportJobStatus.DryRunDone, dry.Job.Status);
        Assert.Equal(36, dry.Job.Counts.Creates);
        Assert.Empty(await host.Get<IChartOfAccountsService>().ListAccountsAsync());

        var done = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        var accounts = await host.Get<IChartOfAccountsService>().ListAccountsAsync();
        Assert.Equal(36, accounts.Count);
        var labor = accounts.Single(a => a.Number == "5010");
        Assert.Equal((AccountKind.Expense, CostClass.DirectLabor), (labor.Type, labor.CostClass));
        Assert.Equal(accounts.Single(a => a.Number == "5000").Id, labor.ParentId);
        Assert.Equal(AccountKind.Equity, accounts.Single(a => a.Number == "3990").Type);
        Assert.Equal(CostClass.GeneralAndAdministrative, accounts.Single(a => a.Number == "6220").CostClass);

        // Importing again changes nothing; a rename is an update.
        var again = await host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv");
        Assert.Equal((0, 0, 36), (again.Job.Counts.Creates, again.Job.Counts.Updates, again.Job.Counts.Skips));
        var rename = await host.ImportAsync(ImportSource.Costpoint, ImportKind.Accounts,
            [Samples.Csv("accounts.csv", "Account,Account Name,Type\n6230,Office and IT services,Expense\n")]);
        Assert.Equal(1, rename.Job.Counts.Updates);
        await host.Imports.RollBackAsync(rename.Job.Id);
        Assert.Equal("Office and IT", (await host.Get<IChartOfAccountsService>().ListAccountsAsync()).Single(a => a.Number == "6230").Name);

        await host.Imports.RollBackAsync(done.Job.Id);
        Assert.Empty(await host.Get<IChartOfAccountsService>().ListAccountsAsync());
    }

    [Fact]
    public async Task Account_rows_report_bad_types_unknown_parents_and_infer_types_from_the_number()
    {
        await using var host = await ImportHost.CreateAsync();
        var dry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.Accounts, [Samples.Csv("coa.csv", """
            Account,Account Name,Type,Parent
            1000,Assets,,
            1010,Cash,,1000
            7777,Mystery,Gizmo,
            8000,Orphan,E,9999
            """)]);
        Assert.Equal(2, dry.Job.Counts.Creates);
        Assert.Contains(dry.Errors, e => e.Row == 4 && e.Message.Contains("isn't Asset"));
        Assert.Contains(dry.Errors, e => e.Row == 5 && e.Message.Contains("9999"));
        Assert.Contains(dry.Errors, e => e.IsWarning && e.Message.Contains("treated as Asset"));
    }

    [Fact]
    public async Task Opening_balances_post_one_balanced_entry_the_trial_balance_shows_and_roll_back()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.Get<IMigrationService>().SaveSettingsAsync(new SaveMigrationSettingsRequest(OpeningDate.AddDays(1), null));
        await host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv");
        await host.ImportAllCostpointSamplesAsync();

        // The as-of date comes from the checklist's cut-over date when the options don't give one.
        var dry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpeningBalances, [Samples.File("samples/import/costpoint/opening-trial-balance.csv")]);
        Assert.Empty(dry.Errors);
        Assert.Equal(1, dry.Job.Counts.Creates);
        Assert.Contains("Opening balances 2026-10-31", dry.Plan.Single(p => p.Action == ImportRowAction.Create).Key);
        Assert.Empty(await host.Get<IJournalService>().ListAsync(new JournalQuery()));

        var done = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        var entry = Assert.Single(await host.Get<IJournalService>().ListAsync(new JournalQuery()));
        Assert.Equal((JournalEntryState.Posted, OpeningDate, 828_303.26m), (entry.Status, entry.Date, entry.TotalDebit));
        var lines = (await host.Get<IJournalService>().GetAsync(entry.Id)).Lines;
        Assert.Equal(23, lines.Count);
        var dev = lines.Single(l => l.AccountNumber == "5010" && l.WbsCode == "2001.02.02");
        Assert.Equal(("2001", "1.02.20", 58_240m), (dev.ProjectCode, dev.OrgUnitCode, dev.Debit));

        var period = (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).Single(p => p.Start <= OpeningDate && OpeningDate <= p.End);
        var tb = await host.Get<IAccountingReportService>().GetTrialBalanceAsync(period.Id);
        Assert.True(tb.IsBalanced);
        Assert.Equal(-291_480.84m, tb.Rows.Single(r => r.AccountNumber == "3200").Ending);

        // A second file while the first stands would double the GL.
        var second = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpeningBalances, [Samples.File("samples/import/costpoint/opening-trial-balance.csv")]);
        Assert.Contains(second.Errors, e => e.Message.Contains("already imported"));

        var check = await host.Imports.CheckRollbackAsync(done.Job.Id);
        Assert.True(check.CanRollBack, string.Join(" ", check.Blockers));
        await host.Imports.RollBackAsync(done.Job.Id);
        Assert.Empty(await host.Get<IJournalService>().ListAsync(new JournalQuery()));
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.Equal(0, await db.Set<JournalLine>().CountAsync());
    }

    [Fact]
    public async Task Opening_balances_refuse_files_that_dont_balance_or_name_unknown_segments()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv");
        await host.ImportCostpointAsync(ImportKind.OrgUnits, "organizations.csv");
        var options = new ImportOptionsDto(AsOfDate: OpeningDate);

        var unbalanced = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpeningBalances,
            [Samples.Csv("tb.csv", "ACCT_ID,ORG_ID,DR_AMT,CR_AMT\n1010,1,100.00,\n3100,1,,99.99\n")], options);
        Assert.Contains(unbalanced.Errors, e => e.Row == 0 && e.Message.Contains("doesn't balance") && e.Message.Contains("0.01"));
        Assert.Equal(0, unbalanced.Job.Counts.Creates);

        var bad = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpeningBalances,
            [Samples.Csv("tb.csv", "ACCT_ID,ORG_ID,PROJ_ID,END_BAL\n1010,1,,100\n1000,1,,0\n9999,1,,5\n3100,7.7,,-100\n3100,1,4444,0\n")], options);
        Assert.Contains(bad.Errors, e => e.Row == 3 && e.Message.Contains("summary account"));
        Assert.Contains(bad.Errors, e => e.Row == 4 && e.Message.Contains("9999"));
        Assert.Contains(bad.Errors, e => e.Row == 5 && e.Message.Contains("Org 7.7"));
        Assert.Contains(bad.Errors, e => e.Row == 6 && e.Message.Contains("Project 4444"));
        Assert.Equal(0, bad.Job.Counts.Creates);

        // Without an as-of date (no option, no column, no cut-over date) nothing can post.
        var undated = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpeningBalances, [Samples.Csv("tb.csv", "ACCT_ID,END_BAL\n1010,5\n3100,-5\n")]);
        Assert.Contains(undated.Errors, e => e.Message.Contains("as-of date"));
    }

    [Fact]
    public async Task Long_trial_balances_split_into_parts_that_net_to_zero_on_the_offset_account()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv");
        var rows = string.Join("\n", Enumerable.Range(0, 600).Select(i => i % 2 == 0 ? "1010,10.00," : "3100,,10.00"));
        var file = Samples.Csv("tb.csv", "ACCT_ID,DR_AMT,CR_AMT\n" + rows + "\n1010,0.01,\n2100,,0.01\n");

        var refused = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpeningBalances, [file], new ImportOptionsDto(AsOfDate: OpeningDate));
        Assert.Contains(refused.Errors, e => e.Message.Contains("offset"));

        var done = await host.ImportAsync(ImportSource.Costpoint, ImportKind.OpeningBalances, [file], new ImportOptionsDto(AsOfDate: OpeningDate, OffsetAccount: "3990"));
        Assert.Equal((ImportJobStatus.Imported, 2), (done.Job.Status, done.Job.Counts.Creates));
        var period = (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).Single(p => p.Start <= OpeningDate && OpeningDate <= p.End);
        var tb = await host.Get<IAccountingReportService>().GetTrialBalanceAsync(period.Id);
        Assert.Equal(3000.01m, tb.Rows.Single(r => r.AccountNumber == "1010").Ending);
        Assert.Equal(0m, tb.Rows.SingleOrDefault(r => r.AccountNumber == "3990")?.Ending ?? 0m);
    }

    [Fact]
    public async Task Open_ar_and_ap_carry_open_balances_without_posting_and_tie_to_the_opening_balances()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.Get<IMigrationService>().SaveSettingsAsync(new SaveMigrationSettingsRequest(OpeningDate.AddDays(1), null));
        await host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv");
        await host.ImportAllCostpointSamplesAsync();
        await host.ImportCostpointAsync(ImportKind.Vendors, "vendors.csv");

        var arDry = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpenArInvoices, [Samples.File("samples/import/costpoint/open-ar.csv")]);
        Assert.Empty(arDry.Errors);
        Assert.Equal((3, 1), (arDry.Job.Counts.Creates, arDry.Job.Counts.Skips));
        Assert.Contains(arDry.Plan, p => p.Key == "2001-0006" && p.Summary.Contains("Paid in full"));
        var ar = await host.Imports.ImportAsync(arDry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, ar.Job.Status);

        var aging = await host.Get<IArReportService>().GetAgingAsync(OpeningDate);
        Assert.Equal((143_540.15m, 42_880.40m), (aging.Totals.Current, aging.Totals.Days1To30));
        var eight = await host.Get<IInvoiceService>().GetAsync(aging.Invoices.Single(i => i.Number == "2001-0008").Id);
        Assert.Equal((InvoiceState.Sent, 100_540.15m, 2), (eight.Summary.Status, eight.Summary.Total, eight.Lines.Count));
        Assert.Null(eight.JournalEntryId);
        var seven = aging.Invoices.Single(i => i.Number == "2001-0007");
        Assert.Equal((InvoiceState.PartiallyPaid, 100_000m, 42_880.40m), (seven.Status, seven.AmountPaid, seven.Balance));

        var ap = await host.ImportCostpointAsync(ImportKind.OpenApBills, "open-ap.csv");
        Assert.Equal((ImportJobStatus.Imported, 4, 1), (ap.Job.Status, ap.Job.Counts.Creates, ap.Job.Counts.Skips));
        var apAging = await host.Get<IApReportService>().GetAgingAsync(OpeningDate);
        Assert.Equal(38_617.42m, apAging.Total);
        Assert.Equal(2_500m, apAging.Rows.Single(r => r.VendorCode == "KESTREL").Retainage);
        Assert.Equal(304.54m, apAging.Rows.Single(r => r.VendorCode == "OFFICEPRO").Days1To30);
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.All(await db.Set<ApBill>().ToListAsync(), b => Assert.Null(b.JournalEntryId));
            Assert.Equal(0, await db.Set<JournalEntry>().CountAsync());
            Assert.Equal("004520", (await db.Set<ApBill>().SingleAsync(b => b.InvoiceNumber == "KA-2026-10")).Number);
        }

        // Before the opening balances, the subledgers don't tie to the GL; after, they do to the penny.
        var before = await host.Get<IMigrationService>().GetChecklistAsync();
        Assert.Contains(before.Steps.Single(s => s.Key == "open-items").Checks, c => c.Title == "Open AR ties to the GL" && !c.Passed);
        await host.ImportCostpointAsync(ImportKind.OpeningBalances, "opening-trial-balance.csv");
        var after = await host.Get<IMigrationService>().GetChecklistAsync();
        Assert.All(after.Steps.Single(s => s.Key == "open-items").Checks, c => Assert.True(c.Passed, $"{c.Title}: {c.Detail}"));

        // Re-importing skips what's there; rolling back removes the invoices and bills.
        var again = await host.ImportCostpointAsync(ImportKind.OpenArInvoices, "open-ar.csv");
        Assert.Equal((0, 4), (again.Job.Counts.Creates, again.Job.Counts.Skips));
        await host.Imports.RollBackAsync(ar.Job.Id);
        await host.Imports.RollBackAsync(ap.Job.Id);
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.Equal(0, await db.Set<Invoice>().CountAsync());
            Assert.Equal(0, await db.Set<InvoiceLine>().CountAsync());
            Assert.Equal(0, await db.Set<ApBill>().CountAsync());
            Assert.Equal(0, await db.Set<ApBillLine>().CountAsync());
        }
    }

    [Fact]
    public async Task Open_item_rows_report_unknown_references_and_credit_balances()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportAllCostpointSamplesAsync();
        var ar = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpenArInvoices, [Samples.Csv("ar.csv", """
            INVC_ID,PRIME_CONTR_ID,CUST_ID,INVC_DT,INVC_AMT,OPEN_AMT,CLIN_ID
            X-1,NOPE,NIWCPAC,10/01/2026,10,10,
            X-2,N66001-27-C-2001,DHS-CISA,10/01/2026,10,10,
            X-3,N66001-27-C-2001,NIWCPAC,10/01/2026,10,15,
            X-4,N66001-27-C-2001,NIWCPAC,10/01/2026,10,10,0099
            X-5,N66001-27-C-2001,NIWCPAC,10/01/2026,10,10,0001
            """)]);
        Assert.True(ar.Errors.Count >= 4, string.Join(" | ", ar.Errors.Select(e => $"{e.Row}: {e.Message}")));
        Assert.Contains(ar.Errors, e => e.Row == 2 && e.Message.Contains("NOPE"));
        Assert.Contains(ar.Errors, e => e.Row == 3 && e.Message.Contains("DHS-CISA"));
        Assert.Contains(ar.Errors, e => e.Row == 4 && e.Message.Contains("overpayments"));
        Assert.Contains(ar.Errors, e => e.Row == 5 && e.Message.Contains("0099"));
        Assert.Equal(1, ar.Job.Counts.Creates);

        var ap = await host.PrepareAsync(ImportSource.Costpoint, ImportKind.OpenApBills, [Samples.Csv("ap.csv", "VEND_ID,INVC_ID,INVC_DT,OPEN_AMT\nGHOST,1,10/01/2026,5\n")]);
        Assert.Contains(ap.Errors, e => e.Message.Contains("GHOST"));
    }

    [Fact]
    public async Task Vendors_import_update_and_roll_back()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.ImportCostpointAsync(ImportKind.Vendors, "vendors.csv");
        Assert.Equal((ImportJobStatus.Imported, 4), (job.Job.Status, job.Job.Counts.Creates));
        var vendors = await host.Get<IVendorService>().ListAsync();
        var kestrel = vendors.Single(v => v.Code == "KESTREL");
        Assert.Equal((true, 15, "Small", "KST9ANL4YTC5"), (kestrel.Is1099, kestrel.PaymentTermsDays, kestrel.BusinessSize, kestrel.Uei));

        var hold = await host.ImportAsync(ImportSource.Costpoint, ImportKind.Vendors, [Samples.Csv("v.csv", "VEND_ID,VEND_NAME,STATUS\nKESTREL,Kestrel Analytics LLC,Hold\n")]);
        Assert.Equal(1, hold.Job.Counts.Updates);
        Assert.Equal(VendorState.OnHold, (await host.Get<IVendorService>().ListAsync()).Single(v => v.Code == "KESTREL").Status);
        await host.Imports.RollBackAsync(hold.Job.Id);
        await host.Imports.RollBackAsync(job.Job.Id);
        Assert.Empty(await host.Get<IVendorService>().ListAsync());
    }
}
