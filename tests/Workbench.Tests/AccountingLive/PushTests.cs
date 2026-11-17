using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.AccountingExport;
using Workbench.Application.AccountingLive;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;
using Workbench.Domain.AccountingExport;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.AccountingLive;

/// <summary>Pushes over the real registrations with a fake provider: results per record, idempotency by provider id, partial failure and retry.</summary>
public sealed class PushTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);

    private static async Task<Guid> SeedInvoiceAsync(PurchasingKit kit, string number, decimal amount = 500m)
    {
        await using var db = await kit.Factory.CreateAsync();
        var contract = await db.Set<Contract>().FirstAsync(c => c.Id == kit.Fx.Contract1);
        var invoice = new Invoice
        {
            ContractId = contract.Id, CustomerId = contract.CustomerId, Sequence = await db.Set<Invoice>().CountAsync() + 1, Number = number, Type = InvoiceType.TimeAndMaterials, Status = InvoiceStatus.Sent,
            InvoiceDate = Day, DueDate = Day.AddDays(30), PeriodFrom = Day, PeriodTo = Day, Total = amount, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 1, Kind = InvoiceLineKind.Labor, Description = "Hours", Quantity = 5, Rate = amount / 5, Amount = amount, ProjectId = kit.Fx.Project1 });
        db.Set<Invoice>().Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    private static async Task<Guid> PostJournalAsync(PurchasingKit kit, string description, decimal amount)
    {
        var entry = await kit.Get<IJournalService>().CreateAndPostAsync(new SaveJournalEntryRequest(Day, description,
            [new JournalLineInput(kit.Account("6230"), amount, 0, "Software", kit.OrgId), new JournalLineInput(kit.Account("1010"), 0, amount)]));
        return entry.Entry.Id;
    }

    private static Dictionary<string, string[]> QboNames(IReadOnlyList<AccountingMappingRowDto> rows, params AccountingMapKind[] leaveOut) =>
        rows.Where(r => !leaveOut.Contains(r.Kind)).GroupBy(r => r.Kind switch
        {
            AccountingMapKind.Account => "Account", AccountingMapKind.Customer => "Customer", AccountingMapKind.Vendor => "Vendor", AccountingMapKind.Item => "Item", _ => "Class",
        }).ToDictionary(g => g.Key, g => g.Select(r => r.SourceName).ToArray());

    private static AccountingPushRequest Invoices(AccountingTarget target, bool create = false, bool again = false, string? note = null) =>
        new(target, AccountingExportKind.Invoices, From, To, null, create, again, note);

    // ---- QuickBooks ----

    [Fact]
    public async Task QuickBooks_invoices_are_created_in_one_batch_and_their_ids_are_remembered()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await SeedInvoiceAsync(live.Kit, "INV-A");
        await SeedInvoiceAsync(live.Kit, "INV-B", 300m);
        var rows = await live.MapEverythingAsync(AccountingTarget.QuickBooks);
        _ = new QboScript(live.Provider, QboNames(rows));

        var preview = await live.Push.PreviewAsync(Invoices(AccountingTarget.QuickBooks));
        Assert.True(preview.CanPush);
        Assert.Equal(2, preview.NewRecords);
        Assert.Equal(800m, preview.ControlTotal);
        Assert.DoesNotContain(preview.MissingAtProvider, m => m.Kind != AccountingMapKind.Class);
        Assert.Empty(live.Provider.To("/batch")); // a preview writes nothing

        var run = await live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks));

        Assert.Equal(AccountingPushRunStatus.Completed, run.Status);
        Assert.Equal((2, 0, 0), (run.Created, run.Failed, run.Skipped));
        Assert.All(run.Items, i => Assert.NotNull(i.ProviderId));
        var batch = Assert.Single(live.Provider.To("/batch"));
        Assert.Equal("75", batch.Query("minorversion"));
        Assert.InRange(batch.Query("requestid")!.Length, 8, 36);
        Assert.Equal("Bearer access-1", "Bearer " + batch.Bearer);
        var first = batch.Json!["BatchItemRequest"]![0]!;
        Assert.Equal("create", first["operation"]!.GetValue<string>());
        var invoice = first["Invoice"]!;
        Assert.Equal("INV-A", invoice["DocNumber"]!.GetValue<string>());
        Assert.Equal("2026-10-15", invoice["TxnDate"]!.GetValue<string>());
        Assert.Equal("2026-11-14", invoice["DueDate"]!.GetValue<string>());
        Assert.Equal("SalesItemLineDetail", invoice["Line"]![0]!["DetailType"]!.GetValue<string>());
        Assert.Equal(500m, invoice["Line"]![0]!["Amount"]!.GetValue<decimal>());
        Assert.NotNull(invoice["CustomerRef"]!["value"]);

        await using var db = await live.Kit.Factory.CreateAsync();
        Assert.Equal(2, await db.Set<AccountingProviderLink>().CountAsync(l => l.Target == "QuickBooks" && l.Kind == "Invoices"));
        Assert.Equal(2, await db.Set<AccountingExportedRecord>().CountAsync(r => r.Target == "QuickBooks" && r.Kind == "Invoices"));
        var history = await live.Kit.Get<IAccountingExportService>().ListRunsAsync();
        var h = Assert.Single(history);
        Assert.Equal("qbo-live", h.FormatId);
        Assert.False(h.HasFile);
        Assert.Equal(800m, h.ControlTotal);
        var connection = (await live.Connections.ListAsync()).First(c => c.Target == AccountingTarget.QuickBooks);
        Assert.Equal(AccountingPushRunStatus.Completed, connection.LastSyncStatus);
        Assert.NotNull(connection.LastSyncUtc);
        Assert.True(await db.Set<AuditEvent>().AnyAsync(a => a.EntityType == nameof(AccountingExportRun) && a.Action == "Pushed"));
    }

    [Fact]
    public async Task Pushing_again_never_creates_a_second_copy_and_a_period_with_nothing_new_is_refused()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await SeedInvoiceAsync(live.Kit, "INV-A");
        var rows = await live.MapEverythingAsync(AccountingTarget.QuickBooks);
        _ = new QboScript(live.Provider, QboNames(rows));
        await live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks));
        var batches = live.Provider.To("/batch").Count();

        var preview = await live.Push.PreviewAsync(Invoices(AccountingTarget.QuickBooks));
        Assert.False(preview.CanPush);
        Assert.Contains("already exported", preview.BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks)));
        await Assert.ThrowsAsync<ArgumentException>(() => live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks, again: true)));

        // "Again" with a reason still doesn't create anything: the record has its QuickBooks id.
        var again = await live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks, again: true, note: "Re-run after a support call"));
        Assert.Equal((0, 1), (again.Created, again.Skipped));
        Assert.Contains("Already in QuickBooks", again.Items.Single().Message);
        Assert.Equal(batches, live.Provider.To("/batch").Count());
        await using var db = await live.Kit.Factory.CreateAsync();
        Assert.Equal(1, await db.Set<AccountingProviderLink>().CountAsync());
    }

    [Fact]
    public async Task A_failed_record_is_listed_and_only_it_is_sent_again_with_a_new_idempotency_key()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await SeedInvoiceAsync(live.Kit, "INV-A");
        await SeedInvoiceAsync(live.Kit, "INV-B");
        var rows = await live.MapEverythingAsync(AccountingTarget.QuickBooks);
        var script = new QboScript(live.Provider, QboNames(rows));
        script.FailDocNumbers.Add("INV-B");

        var first = await live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks));

        Assert.Equal(AccountingPushRunStatus.PartlyFailed, first.Status);
        Assert.Equal((1, 1), (first.Created, first.Failed));
        var failed = Assert.Single(first.Items, i => i.Status == AccountingPushItemStatus.Failed);
        Assert.Equal("INV-B", failed.RecordNumber);
        Assert.Contains("Object Not Found", failed.Message);
        Assert.Equal(1, failed.Attempts);
        await using (var db = await live.Kit.Factory.CreateAsync())
        {
            Assert.Equal(1, await db.Set<AccountingExportedRecord>().CountAsync()); // only the created one is marked
            Assert.Equal(1, await db.Set<AccountingProviderLink>().CountAsync());
        }

        // The failure is fixed at the provider; the preview and a plain push now offer only the failed record.
        script.FailDocNumbers.Clear();
        var retry = await live.Push.RetryFailedAsync(first.RunId);

        Assert.Equal(AccountingPushRunStatus.Completed, retry.Status);
        Assert.Equal(first.RunId, retry.RetryOfRunId);
        Assert.Equal((1, 0, 0), (retry.Created, retry.Failed, retry.Skipped));
        var batches = live.Provider.To("/batch").ToList();
        Assert.Equal(2, batches.Count);
        var retried = Assert.Single(batches[1].Json!["BatchItemRequest"]!.AsArray());
        Assert.Equal("INV-B", retried!["Invoice"]!["DocNumber"]!.GetValue<string>());
        Assert.Equal("INV-B", retry.Items.Single().RecordNumber);
        Assert.NotEqual(batches[0].Query("requestid"), batches[1].Query("requestid"));
        await Assert.ThrowsAsync<ArgumentException>(() => live.Push.RetryFailedAsync(retry.RunId)); // nothing failed there

        var runs = await live.Push.ListRunsAsync(AccountingTarget.QuickBooks);
        Assert.Equal(2, runs.Count);
        var loaded = await live.Push.GetRunAsync(first.RunId);
        Assert.Equal(2, loaded.Items.Count);
    }

    [Fact]
    public async Task The_same_records_resent_after_a_lost_answer_carry_the_same_idempotency_key()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await SeedInvoiceAsync(live.Kit, "INV-A");
        var rows = await live.MapEverythingAsync(AccountingTarget.QuickBooks);
        _ = new QboScript(live.Provider, QboNames(rows));
        var served = 0;
        // The first answer is lost on the way back (a 503 after QuickBooks created the invoice); the retry carries the same requestid, so QuickBooks replays it.
        live.Provider.On(c => c.Uri.AbsolutePath.EndsWith("/batch") && ++served == 1, _ => FakeProvider.Json(HttpStatusCode.ServiceUnavailable, "{}"));

        var run = await live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks));

        Assert.Equal(AccountingPushRunStatus.Completed, run.Status);
        var batches = live.Provider.To("/batch").ToList();
        Assert.Equal(2, batches.Count);
        Assert.Equal(batches[0].Query("requestid"), batches[1].Query("requestid"));
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(live.Delay.Waits));
    }

    [Fact]
    public async Task A_customer_that_is_not_in_QuickBooks_blocks_the_push_unless_the_user_lets_it_be_created()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await SeedInvoiceAsync(live.Kit, "INV-A");
        var rows = await live.MapEverythingAsync(AccountingTarget.QuickBooks);
        var script = new QboScript(live.Provider, QboNames(rows, AccountingMapKind.Customer));
        var customer = rows.First(r => r.Kind == AccountingMapKind.Customer).SourceName;

        var blocked = await live.Push.PreviewAsync(Invoices(AccountingTarget.QuickBooks));
        Assert.False(blocked.CanPush);
        Assert.Contains(blocked.MissingAtProvider, m => m.Kind == AccountingMapKind.Customer && m.Name == customer && !m.WillBeCreated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks)));
        Assert.Empty(live.Provider.To("/customer"));

        var allowed = await live.Push.PreviewAsync(Invoices(AccountingTarget.QuickBooks, create: true));
        Assert.True(allowed.CanPush);
        Assert.Equal(1, allowed.CustomersToCreate);

        var run = await live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks, create: true));

        Assert.Equal(1, run.ContactsCreated);
        var created = Assert.Single(live.Provider.To("/customer"));
        Assert.Equal(customer, created.Json!["DisplayName"]!.GetValue<string>());
        Assert.StartsWith("wb-c-", created.Query("requestid"));
        Assert.Equal("new-Customer", live.Provider.To("/batch").Single().Json!["BatchItemRequest"]![0]!["Invoice"]!["CustomerRef"]!["value"]!.GetValue<string>());
        Assert.Equal(AccountingPushRunStatus.Completed, run.Status);
        Assert.Empty(script.FailDocNumbers);
    }

    [Fact]
    public async Task Names_without_a_mapping_stop_a_push_before_anything_is_sent()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await SeedInvoiceAsync(live.Kit, "INV-A");
        _ = new QboScript(live.Provider, []);

        var preview = await live.Push.PreviewAsync(Invoices(AccountingTarget.QuickBooks));

        Assert.False(preview.CanPush);
        Assert.Contains("no mapping", preview.BlockedReason);
        Assert.NotEmpty(preview.Unmapped);
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks)));
        Assert.Empty(live.Provider.To("/batch"));
    }

    [Fact]
    public async Task Without_a_connection_the_preview_says_so_and_a_push_is_refused()
    {
        await using var live = await LiveKit.CreateAsync();
        await SeedInvoiceAsync(live.Kit, "INV-A");
        await live.MapEverythingAsync(AccountingTarget.QuickBooks);

        var preview = await live.Push.PreviewAsync(Invoices(AccountingTarget.QuickBooks));

        Assert.False(preview.CanPush);
        Assert.Contains("isn't connected", preview.BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.Push.PushAsync(Invoices(AccountingTarget.QuickBooks)));
    }

    [Fact]
    public async Task QuickBooks_journal_entries_go_in_as_debit_and_credit_lines()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        await PostJournalAsync(live.Kit, "Software", 120m);
        var rows = await live.MapEverythingAsync(AccountingTarget.QuickBooks);
        _ = new QboScript(live.Provider, QboNames(rows));

        var run = await live.Push.PushAsync(new AccountingPushRequest(AccountingTarget.QuickBooks, AccountingExportKind.JournalEntries, From, To));

        Assert.Equal(1, run.Created);
        var entry = live.Provider.To("/batch").Single().Json!["BatchItemRequest"]![0]!["JournalEntry"]!;
        var lines = entry["Line"]!.AsArray();
        Assert.Equal(["Debit", "Credit"], lines.Select(l => l!["JournalEntryLineDetail"]!["PostingType"]!.GetValue<string>()));
        Assert.All(lines, l => Assert.Equal(120m, l!["Amount"]!.GetValue<decimal>()));
        Assert.StartsWith("JE-", entry["DocNumber"]!.GetValue<string>());
    }

    // ---- Xero ----

    [Fact]
    public async Task Xero_invoices_are_sent_as_drafts_with_an_idempotency_key_and_one_bad_record_does_not_fail_the_batch()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectXeroAsync(("tenant-7", "Demo Company (US)"));
        await SeedInvoiceAsync(live.Kit, "INV-A");
        await SeedInvoiceAsync(live.Kit, "INV-B");
        var rows = await live.MapEverythingAsync(AccountingTarget.Xero);
        var script = new XeroScript(live.Provider, rows.Where(r => r.Kind == AccountingMapKind.Account).Select(r => (r.SourceKey, r.SourceName)).ToArray(),
            rows.Where(r => r.Kind == AccountingMapKind.Customer).Select(r => r.SourceName).ToArray(), rows.Where(r => r.Kind == AccountingMapKind.Class).Select(r => ("Region", r.SourceName)).ToArray());
        script.FailNumbers.Add("INV-B");

        var preview = await live.Push.PreviewAsync(Invoices(AccountingTarget.Xero));
        Assert.True(preview.CanPush);

        var run = await live.Push.PushAsync(Invoices(AccountingTarget.Xero));

        Assert.Equal(AccountingPushRunStatus.PartlyFailed, run.Status);
        Assert.Equal((1, 1), (run.Created, run.Failed));
        Assert.Contains("999", run.Items.Single(i => i.Status == AccountingPushItemStatus.Failed).Message);
        var post = Assert.Single(live.Provider.To("/Invoices").Where(c => c.Method == "POST"));
        Assert.Equal("false", post.Query("summarizeErrors"));
        Assert.Equal("tenant-7", post.Headers["xero-tenant-id"]);
        Assert.InRange(post.Headers["Idempotency-Key"].Length, 8, 128);
        var invoice = post.Json!["Invoices"]![0]!;
        Assert.Equal("ACCREC", invoice["Type"]!.GetValue<string>());
        Assert.Equal("DRAFT", invoice["Status"]!.GetValue<string>());
        Assert.Equal("NoTax", invoice["LineAmountTypes"]!.GetValue<string>());
        Assert.Equal("2026-10-15", invoice["Date"]!.GetValue<string>());
        Assert.StartsWith("contact-", invoice["Contact"]!["ContactID"]!.GetValue<string>());
        Assert.Equal(5m, invoice["LineItems"]![0]!["Quantity"]!.GetValue<decimal>());
        Assert.Equal("Region", invoice["LineItems"]![0]!["Tracking"]![0]!["Name"]!.GetValue<string>());

        script.FailNumbers.Clear();
        var retry = await live.Push.RetryFailedAsync(run.RunId);
        Assert.Equal(AccountingPushRunStatus.Completed, retry.Status);
        var second = live.Provider.To("/Invoices").Where(c => c.Method == "POST").Last();
        Assert.Equal("INV-B", Assert.Single(second.Json!["Invoices"]!.AsArray())!["InvoiceNumber"]!.GetValue<string>());
        Assert.NotEqual(post.Headers["Idempotency-Key"], second.Headers["Idempotency-Key"]);
        await using var db = await live.Kit.Factory.CreateAsync();
        Assert.Equal(2, await db.Set<AccountingProviderLink>().CountAsync(l => l.Target == "Xero"));
    }

    [Fact]
    public async Task Xero_manual_journals_use_signed_amounts_and_can_be_posted_instead_of_drafted()
    {
        await using var live = await LiveKit.CreateAsync(xeroPostAsDraft: false);
        await live.ConnectXeroAsync(("tenant-7", "Demo Company (US)"));
        await PostJournalAsync(live.Kit, "Software", 120m);
        var rows = await live.MapEverythingAsync(AccountingTarget.Xero);
        _ = new XeroScript(live.Provider, rows.Where(r => r.Kind == AccountingMapKind.Account).Select(r => (r.SourceKey, r.SourceName)).ToArray(), [], rows.Where(r => r.Kind == AccountingMapKind.Class).Select(r => ("Region", r.SourceName)).ToArray());

        var run = await live.Push.PushAsync(new AccountingPushRequest(AccountingTarget.Xero, AccountingExportKind.JournalEntries, From, To));

        Assert.Equal(1, run.Created);
        var journal = live.Provider.To("/ManualJournals").Single(c => c.Method == "POST").Json!["ManualJournals"]![0]!;
        Assert.Equal("POSTED", journal["Status"]!.GetValue<string>());
        Assert.StartsWith("JE-", journal["Narration"]!.GetValue<string>());
        Assert.Equal([120m, -120m], journal["JournalLines"]!.AsArray().Select(l => l!["LineAmount"]!.GetValue<decimal>()));
        Assert.Equal(["6230", "1010"], journal["JournalLines"]!.AsArray().Select(l => l!["AccountCode"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Xero_contacts_are_created_only_when_the_user_opts_in()
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectXeroAsync(("tenant-7", "Demo Company (US)"));
        await SeedInvoiceAsync(live.Kit, "INV-A");
        var rows = await live.MapEverythingAsync(AccountingTarget.Xero);
        _ = new XeroScript(live.Provider, rows.Where(r => r.Kind == AccountingMapKind.Account).Select(r => (r.SourceKey, r.SourceName)).ToArray(), [], rows.Where(r => r.Kind == AccountingMapKind.Class).Select(r => ("Region", r.SourceName)).ToArray());

        Assert.False((await live.Push.PreviewAsync(Invoices(AccountingTarget.Xero))).CanPush);
        var run = await live.Push.PushAsync(Invoices(AccountingTarget.Xero, create: true));

        Assert.Equal(1, run.ContactsCreated);
        var contact = live.Provider.To("/Contacts").Single(c => c.Method == "POST");
        Assert.Equal(rows.First(r => r.Kind == AccountingMapKind.Customer).SourceName, contact.Json!["Contacts"]![0]!["Name"]!.GetValue<string>());
        Assert.Equal("contact-new", live.Provider.To("/Invoices").Single(c => c.Method == "POST").Json!["Invoices"]![0]!["Contact"]!["ContactID"]!.GetValue<string>());
    }

    // ---- The documents of every kind, both providers (fixture dataset, no database) ----

    private static readonly Dictionary<string, string[]> FixtureQbo = new()
    {
        ["Account"] = ["Office Expenses", "Checking", "Accounts Payable", "Accounts Receivable", "Services Revenue"],
        ["Customer"] = ["Acme Corp"],
        ["Vendor"] = ["Bolt Consulting"],
        ["Item"] = ["Consulting", "Reimbursable"],
        ["Class"] = ["Headquarters"],
    };

    private static ExportInput Fixture(AccountingExportKind kind) => ExportFixtures(kind);

    private static ExportInput ExportFixtures(AccountingExportKind kind) => Workbench.Tests.AccountingExport.ExportFixtures.Input(kind);

    [Theory]
    [InlineData(AccountingExportKind.JournalEntries)]
    [InlineData(AccountingExportKind.Invoices)]
    [InlineData(AccountingExportKind.Bills)]
    public async Task QuickBooks_builds_the_documents_of_each_kind_and_leaves_credits_unsent(AccountingExportKind kind)
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectQuickBooksAsync();
        _ = new QboScript(live.Provider, FixtureQbo);
        var api = new AccountingApi(AccountingTarget.QuickBooks, live.Kit.Get<IAccountingHttp>(), live.Kit.Get<AccountingTokenService>(), live.Delay);
        var publisher = live.Kit.Get<IEnumerable<IAccountingPublisher>>().Single(p => p.Target == AccountingTarget.QuickBooks);

        var outcome = await publisher.PublishAsync(api, new PublishRequest(Fixture(kind), false, _ => 0), default);

        var sent = live.Provider.To("/batch").Single().Json!["BatchItemRequest"]!.AsArray();
        switch (kind)
        {
            case AccountingExportKind.JournalEntries:
                Assert.Equal(2, outcome.Records.Count(r => r.Status == AccountingPushItemStatus.Created));
                Assert.NotNull(sent[0]!["JournalEntry"]!["Line"]![0]!["JournalEntryLineDetail"]!["ClassRef"]);
                break;
            case AccountingExportKind.Invoices:
                Assert.Equal(1, outcome.Records.Count(r => r.Status == AccountingPushItemStatus.Created));
                Assert.Equal("CM-1002", outcome.Records.Single(r => r.Status == AccountingPushItemStatus.NotSent).Number);
                Assert.Single(sent);
                Assert.Equal(2, sent[0]!["Invoice"]!["Line"]!.AsArray().Count);
                break;
            default:
                Assert.Equal(1, outcome.Records.Count(r => r.Status == AccountingPushItemStatus.Created));
                Assert.Equal("AP-0002", outcome.Records.Single(r => r.Status == AccountingPushItemStatus.NotSent).Number);
                var bill = sent[0]!["Bill"]!;
                Assert.Equal("B-77", bill["DocNumber"]!.GetValue<string>());
                Assert.Equal("AccountBasedExpenseLineDetail", bill["Line"]![0]!["DetailType"]!.GetValue<string>());
                Assert.NotNull(bill["VendorRef"]!["value"]);
                break;
        }
    }

    [Theory]
    [InlineData(AccountingExportKind.JournalEntries)]
    [InlineData(AccountingExportKind.Invoices)]
    [InlineData(AccountingExportKind.Bills)]
    public async Task Xero_builds_the_documents_of_each_kind_and_leaves_credits_unsent(AccountingExportKind kind)
    {
        await using var live = await LiveKit.CreateAsync();
        await live.ConnectXeroAsync(("tenant-7", "Demo"));
        _ = new XeroScript(live.Provider, [("461", "Office Expenses"), ("090", "Checking"), ("800", "Accounts Payable"), ("610", "Accounts Receivable"), ("200", "Services Revenue")], ["Acme Corp", "Bolt Consulting"], [("Region", "Headquarters")]);
        var api = new AccountingApi(AccountingTarget.Xero, live.Kit.Get<IAccountingHttp>(), live.Kit.Get<AccountingTokenService>(), live.Delay);
        var publisher = live.Kit.Get<IEnumerable<IAccountingPublisher>>().Single(p => p.Target == AccountingTarget.Xero);

        var outcome = await publisher.PublishAsync(api, new PublishRequest(Fixture(kind), false, _ => 0), default);

        var post = live.Provider.Calls.Single(c => c.Method == "POST" && c.Url.StartsWith(LiveKit.XeroApi));
        switch (kind)
        {
            case AccountingExportKind.JournalEntries:
                Assert.Equal(2, outcome.Records.Count(r => r.Status == AccountingPushItemStatus.Created));
                var first = post.Json!["ManualJournals"]![0]!;
                Assert.Equal([1500m, -1500m], first["JournalLines"]!.AsArray().Select(l => l!["LineAmount"]!.GetValue<decimal>()));
                Assert.Equal("Headquarters", first["JournalLines"]![0]!["Tracking"]![0]!["Option"]!.GetValue<string>());
                break;
            case AccountingExportKind.Invoices:
                Assert.Equal("CM-1002", outcome.Records.Single(r => r.Status == AccountingPushItemStatus.NotSent).Number);
                Assert.Equal("ACCREC", post.Json!["Invoices"]![0]!["Type"]!.GetValue<string>());
                Assert.Single(post.Json["Invoices"]!.AsArray());
                break;
            default:
                Assert.Equal("AP-0002", outcome.Records.Single(r => r.Status == AccountingPushItemStatus.NotSent).Number);
                var bill = post.Json!["Invoices"]![0]!;
                Assert.Equal("ACCPAY", bill["Type"]!.GetValue<string>());
                Assert.Equal("B-77", bill["InvoiceNumber"]!.GetValue<string>());
                Assert.Equal("AP-0001", bill["Reference"]!.GetValue<string>());
                break;
        }
    }
}
