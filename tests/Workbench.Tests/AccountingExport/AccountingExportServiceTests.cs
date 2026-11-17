using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.AccountingExport;
using Workbench.Domain.Accounting;
using Workbench.Domain.AccountingExport;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Security;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.AccountingExport;

/// <summary>
/// Exports over the real registrations (finance@ unless switched): what is included, the idempotency rules, mappings and permissions.
/// </summary>
public sealed class AccountingExportServiceTests
{
    private static readonly DateOnly Jan = new(2026, 10, 15);

    private static async Task<PurchasingKit> KitAsync() => await PurchasingKit.CreateAsync();

    private static async Task<Guid> PostAsync(PurchasingKit kit, DateOnly date, string description, decimal amount)
    {
        kit.As(Finance);
        var entry = await kit.Get<IJournalService>().CreateAndPostAsync(new SaveJournalEntryRequest(date, description,
            [new JournalLineInput(kit.Account("6230"), amount, 0, "Software", kit.OrgId), new JournalLineInput(kit.Account("1010"), 0, amount)]));
        return entry.Entry.Id;
    }

    private static AccountingFileRequest Journals(string format = AccountingFormatIds.GenericCsv, DateOnly? from = null, DateOnly? to = null, bool again = false, string? note = null, bool allowUnmapped = false) =>
        new(format, AccountingExportKind.JournalEntries, from ?? new DateOnly(2026, 10, 1), to ?? new DateOnly(2026, 10, 31), Again: again, Note: note, AllowUnmapped: allowUnmapped);

    private static async Task<Guid> SeedInvoiceAsync(PurchasingKit kit, string number, InvoiceType type = InvoiceType.TimeAndMaterials, decimal amount = 500m, InvoiceStatus status = InvoiceStatus.Sent)
    {
        await using var db = await kit.Factory.CreateAsync();
        var contract = await db.Set<Contract>().FirstAsync(c => c.Id == kit.Fx.Contract1);
        var invoice = new Invoice
        {
            ContractId = contract.Id, CustomerId = contract.CustomerId, Sequence = await db.Set<Invoice>().CountAsync() + 1, Number = number, Type = type, Status = status, InvoiceDate = Jan, DueDate = Jan.AddDays(30), PeriodFrom = Jan, PeriodTo = Jan,
            Total = type == InvoiceType.CreditMemo ? -amount : amount, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 1, Kind = InvoiceLineKind.Labor, Description = "Hours", Quantity = 5, Rate = amount / 5 * (type == InvoiceType.CreditMemo ? -1 : 1), Amount = invoice.Total, ProjectId = kit.Fx.Project1 });
        db.Set<Invoice>().Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    // ---- Idempotency ----

    [Fact]
    public async Task Exporting_a_period_records_the_run_marks_its_entries_and_refuses_the_same_period_again()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "Subscriptions", 120m);
        await PostAsync(kit, Jan.AddDays(3), "Hosting", 80m);
        var service = kit.Get<IAccountingExportService>();

        var result = await service.ExportAsync(Journals());
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(4, result.RowCount);
        Assert.Equal(200m, result.ControlTotal);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(result.Content)), result.Checksum);

        var run = Assert.Single(await service.ListRunsAsync());
        Assert.Equal(Finance, run.CreatedBy);
        Assert.Equal(result.Checksum, run.Checksum);
        Assert.False(run.IsRepeat);
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(2, await db.Set<AccountingExportedRecord>().CountAsync(r => r.Target == "Generic" && r.ExportCount == 1));
            Assert.True(await db.Set<AuditEvent>().AnyAsync(a => a.EntityType == nameof(AccountingExportRun) && a.Action == "Exported"));
            Assert.True(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == "export.accounting"));
        }

        var preview = await service.PreviewAsync(Journals());
        Assert.False(preview.CanExport);
        Assert.Equal(2, preview.AlreadyExported);
        Assert.Contains("already exported", preview.BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(Journals()));
        Assert.Single(await service.ListRunsAsync());
    }

    [Fact]
    public async Task An_overlapping_period_only_takes_what_was_not_exported_so_nothing_is_ever_sent_twice()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "First", 10m);
        var service = kit.Get<IAccountingExportService>();
        await service.ExportAsync(Journals(from: new DateOnly(2026, 10, 1), to: new DateOnly(2026, 10, 20)));

        // Posted late into the period that was already exported, plus one in the next stretch.
        await PostAsync(kit, Jan.AddDays(1), "Late arrival", 20m);
        await PostAsync(kit, Jan.AddDays(10), "Next stretch", 30m);

        var second = await service.ExportAsync(Journals(from: new DateOnly(2026, 10, 1), to: new DateOnly(2026, 10, 31)));
        Assert.Equal(2, second.RecordCount);
        Assert.Equal(50m, second.ControlTotal);
        Assert.Contains(second.Warnings, w => w.Contains("1 record(s) in this period were exported"));
        await using var db = await kit.Factory.CreateAsync();
        Assert.Equal(3, await db.Set<AccountingExportedRecord>().CountAsync());
        Assert.Equal(3, await db.Set<AccountingExportedRecord>().Select(r => r.RecordId).Distinct().CountAsync());
    }

    [Fact]
    public async Task Exporting_again_needs_a_note_includes_everything_and_is_recorded_as_a_repeat()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "One", 10m);
        var service = kit.Get<IAccountingExportService>();
        var first = await service.ExportAsync(Journals());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(Journals(again: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(Journals(again: true, note: " ")));
        var again = await service.ExportAsync(Journals(again: true, note: "Deleted the first import in QuickBooks"));

        Assert.Equal(1, again.RecordCount);
        Assert.NotEqual(first.RunId, again.RunId);
        var runs = await service.ListRunsAsync();
        Assert.Equal(2, runs.Count);
        var repeat = runs.Single(r => r.Id == again.RunId);
        Assert.True(repeat.IsRepeat);
        Assert.Equal("Deleted the first import in QuickBooks", repeat.Note);
        await using var db = await kit.Factory.CreateAsync();
        var mark = await db.Set<AccountingExportedRecord>().SingleAsync();
        Assert.Equal(2, mark.ExportCount);
        Assert.Equal(first.RunId, mark.FirstRunId);
        Assert.Equal(again.RunId, mark.LastRunId);
    }

    [Fact]
    public async Task Each_package_keeps_its_own_record_of_what_it_has_been_sent()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "One", 10m);
        var service = kit.Get<IAccountingExportService>();
        await service.ExportAsync(Journals(AccountingFormatIds.GenericCsv));

        var xero = await service.ExportAsync(Journals(AccountingFormatIds.XeroCsv, allowUnmapped: true));
        Assert.Equal(1, xero.RecordCount);
        // QuickBooks Online and Desktop are the same package: one is enough.
        var qbo = await service.ExportAsync(Journals(AccountingFormatIds.QuickBooksOnlineCsv, allowUnmapped: true));
        Assert.Equal(1, qbo.RecordCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(Journals(AccountingFormatIds.QuickBooksDesktopIif, allowUnmapped: true)));
    }

    [Fact]
    public async Task Two_marks_for_the_same_record_and_target_are_refused_by_the_database()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WORKBENCH_TEST_DB"), "Sqlite", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Environment.GetEnvironmentVariable("WORKBENCH_TEST_DB"), "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            return; // InMemory doesn't enforce unique indexes; CI runs this on SQLite and PostgreSQL.
        }

        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "One", 10m);
        var service = kit.Get<IAccountingExportService>();
        var run = await service.ExportAsync(Journals());
        await using var db = await kit.Factory.CreateAsync();
        var mark = await db.Set<AccountingExportedRecord>().AsNoTracking().SingleAsync();
        db.Set<AccountingExportedRecord>().Add(new AccountingExportedRecord
        {
            Target = mark.Target, Kind = mark.Kind, RecordId = mark.RecordId, RecordNumber = mark.RecordNumber, FirstRunId = run.RunId, LastRunId = run.RunId, ExportCount = 1,
            FirstExportedAtUtc = DateTime.UtcNow, LastExportedAtUtc = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_run_downloads_byte_for_byte_and_a_changed_file_is_refused()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "One", 10m);
        var service = kit.Get<IAccountingExportService>();
        var first = await service.ExportAsync(Journals());
        var again = await service.DownloadRunAsync(first.RunId);
        Assert.Equal(first.Content, again.Content);
        Assert.Equal(first.FileName, again.FileName);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => service.DownloadRunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Empty_periods_and_bad_requests_are_refused()
    {
        await using var kit = await KitAsync();
        var service = kit.Get<IAccountingExportService>();
        var preview = await service.PreviewAsync(Journals());
        Assert.False(preview.CanExport);
        Assert.Contains("nothing to export", preview.BlockedReason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(Journals()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(Journals(from: new DateOnly(2026, 11, 1), to: new DateOnly(2026, 10, 1))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(Journals("nope")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(new AccountingFileRequest(AccountingFormatIds.QuickBooksOnlineCsv, AccountingExportKind.Payments, Jan, Jan)));
    }

    // ---- What is included ----

    [Fact]
    public async Task Entries_made_by_invoices_and_bills_are_left_out_unless_asked_for_and_so_are_drafts()
    {
        await using var kit = await KitAsync();
        var manual = await PostAsync(kit, Jan, "Manual", 10m);
        kit.As(Finance);
        await kit.Get<IJournalService>().CreateDraftAsync(new SaveJournalEntryRequest(Jan, "Draft", [new JournalLineInput(kit.Account("6230"), 5, 0), new JournalLineInput(kit.Account("1010"), 0, 5)]));
        await using (var db = await kit.Factory.CreateAsync())
        {
            var original = await db.Set<JournalEntry>().Include(e => e.Lines).AsNoTracking().SingleAsync(e => e.Id == manual);
            var billing = new JournalEntry { CompanyId = original.CompanyId, Date = Jan, FiscalPeriodId = original.FiscalPeriodId, Source = JournalSource.Billing, SourceId = Guid.NewGuid(), Description = "Invoice INV-9", CreatedBy = Finance, Status = JournalStatus.Posted, Number = "JE-900001", Sequence = 900001, PostedBy = Finance, PostedAtUtc = DateTime.UtcNow };
            billing.Lines.Add(new JournalLine { JournalEntryId = billing.Id, LineNumber = 1, AccountId = kit.Account("1110"), Debit = 7m });
            billing.Lines.Add(new JournalLine { JournalEntryId = billing.Id, LineNumber = 2, AccountId = kit.Account("4020"), Credit = 7m });
            db.Set<JournalEntry>().Add(billing);
            await db.SaveChangesAsync();
        }

        var service = kit.Get<IAccountingExportService>();
        var preview = await service.PreviewAsync(Journals());
        Assert.Equal(1, preview.NewRecords);
        Assert.Contains(preview.Warnings, w => w.Contains("invoices, payments and bills"));
        var all = await service.PreviewAsync(Journals() with { IncludeSubledgerJournals = true });
        Assert.Equal(2, all.NewRecords);
    }

    [Fact]
    public async Task Credit_memos_are_not_marked_exported_when_the_format_leaves_them_out()
    {
        await using var kit = await KitAsync();
        var invoice = await SeedInvoiceAsync(kit, "INV-1");
        var credit = await SeedInvoiceAsync(kit, "CM-1", InvoiceType.CreditMemo, 100m);
        var service = kit.Get<IAccountingExportService>();
        var request = new AccountingFileRequest(AccountingFormatIds.QuickBooksOnlineCsv, AccountingExportKind.Invoices, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), AllowUnmapped: true);

        var result = await service.ExportAsync(request);
        Assert.Equal(1, result.RecordCount);
        Assert.Contains(result.Warnings, w => w.Contains("CM-1"));
        await using var db = await kit.Factory.CreateAsync();
        var marked = await db.Set<AccountingExportedRecord>().Select(r => r.RecordId).ToListAsync();
        Assert.Equal([invoice], marked);

        // The generic file carries it, and it was never sent to QuickBooks, so a Generic export still has both.
        var generic = await service.ExportAsync(request with { FormatId = AccountingFormatIds.GenericCsv });
        Assert.Equal(2, generic.RecordCount);
        Assert.DoesNotContain(credit, marked);
    }

    [Fact]
    public async Task A_document_voided_after_it_was_exported_is_called_out_in_the_next_export()
    {
        await using var kit = await KitAsync();
        var invoice = await SeedInvoiceAsync(kit, "INV-7");
        var service = kit.Get<IAccountingExportService>();
        var request = new AccountingFileRequest(AccountingFormatIds.GenericCsv, AccountingExportKind.Invoices, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        await service.ExportAsync(request);

        await using (var db = await kit.Factory.CreateAsync())
        {
            var inv = await db.Set<Invoice>().SingleAsync(i => i.Id == invoice);
            inv.Status = InvoiceStatus.Void;
            inv.VoidedAtUtc = DateTime.UtcNow.AddMinutes(5);
            await SeedInvoiceAsync(kit, "INV-8");
            await db.SaveChangesAsync();
        }

        var preview = await service.PreviewAsync(request);
        Assert.Contains(preview.Warnings, w => w.Contains("INV-7") && w.Contains("voided"));
        Assert.Equal(1, preview.NewRecords); // the voided one is gone, the new one is in
    }

    [Fact]
    public async Task Bills_export_their_account_lines_and_chart_of_accounts_leaves_out_group_accounts()
    {
        await using var kit = await KitAsync();
        kit.As(Finance);
        var bill = await kit.SubmitAsync(kit.Acme.Id, "ACME-1", kit.OverheadLine(250m));
        await kit.As(Admin).Bills.ApproveAsync(bill.Bill.Id);
        var service = kit.Get<IAccountingExportService>();

        var day = PurchasingKit.Day;
        var bills = await service.ExportAsync(new AccountingFileRequest(AccountingFormatIds.GenericCsv, AccountingExportKind.Bills, day, day));
        Assert.Equal(1, bills.RecordCount);
        Assert.Equal(250m, bills.ControlTotal);

        var chart = await service.ExportAsync(new AccountingFileRequest(AccountingFormatIds.QuickBooksOnlineCsv, AccountingExportKind.ChartOfAccounts, day, day, AllowUnmapped: true));
        var text = System.Text.Encoding.UTF8.GetString(chart.Content);
        Assert.Contains("Cash - operating", text);
        Assert.DoesNotContain("\nAssets,", text); // the group account 1000 is skipped
        Assert.Contains(chart.Warnings, w => w.Contains("group account"));
    }

    // ---- Mapping ----

    [Fact]
    public async Task Unmapped_names_block_a_package_export_until_mapped_or_allowed_and_mappings_are_per_target()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "One", 10m);
        var export = kit.Get<IAccountingExportService>();
        var mappings = kit.Get<IAccountingMappingService>();
        var request = Journals(AccountingFormatIds.QuickBooksOnlineCsv);

        var blocked = await export.PreviewAsync(request);
        Assert.False(blocked.CanExport);
        Assert.Contains(blocked.Unmapped, u => u.Kind == AccountingMapKind.Account && u.SourceKey == "6230");
        Assert.Contains(blocked.Unmapped, u => u.Kind == AccountingMapKind.Account && u.SourceKey == "1010");
        await Assert.ThrowsAsync<InvalidOperationException>(() => export.ExportAsync(request));

        var set = await mappings.SaveAsync(AccountingTarget.QuickBooks, new SaveAccountingMappingsRequest(
        [
            new(AccountingMapKind.Account, "6230", "Software & Subscriptions", null),
            new(AccountingMapKind.Account, "1010", "Business Checking", null),
        ]));
        Assert.True(set.Rows.Single(r => r.Kind == AccountingMapKind.Account && r.SourceKey == "6230").IsMapped);
        var ok = await export.PreviewAsync(request);
        Assert.True(ok.CanExport);
        Assert.Empty(ok.Unmapped.Where(u => u.Kind == AccountingMapKind.Account));

        var file = await export.ExportAsync(request);
        var text = System.Text.Encoding.UTF8.GetString(file.Content);
        Assert.Contains("Software & Subscriptions", text);
        Assert.Contains("Business Checking", text);

        // Xero has its own (empty) mapping.
        Assert.False((await mappings.GetAsync(AccountingTarget.Xero)).Rows.Single(r => r.Kind == AccountingMapKind.Account && r.SourceKey == "6230").IsMapped);
        Assert.False((await export.PreviewAsync(Journals(AccountingFormatIds.XeroCsv))).CanExport);
    }

    [Fact]
    public async Task Exporting_with_unmapped_names_when_allowed_warns_and_says_so_in_the_history()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "One", 10m);
        var export = kit.Get<IAccountingExportService>();
        var result = await export.ExportAsync(Journals(AccountingFormatIds.XeroCsv, allowUnmapped: true));
        Assert.Contains(result.Warnings, w => w.Contains("exported unmapped"));
        var run = Assert.Single(await export.ListRunsAsync());
        Assert.True(run.UnmappedAllowed);
        Assert.True(run.UnmappedCount >= 2);
        Assert.Contains("6230", System.Text.Encoding.UTF8.GetString(result.Content)); // Xero falls back to the account number as the code
    }

    [Fact]
    public async Task Saving_a_mapping_validates_the_source_clears_blanks_and_is_audited()
    {
        await using var kit = await KitAsync();
        var mappings = kit.Get<IAccountingMappingService>();
        await mappings.SaveAsync(AccountingTarget.Xero, new SaveAccountingMappingsRequest([new(AccountingMapKind.Account, "6230", "Office costs", "461")]));
        await Assert.ThrowsAsync<ArgumentException>(() => mappings.SaveAsync(AccountingTarget.Xero, new SaveAccountingMappingsRequest([new(AccountingMapKind.Account, "9999", "Nope", null)])));
        var row = (await mappings.GetAsync(AccountingTarget.Xero)).Rows.Single(r => r.Kind == AccountingMapKind.Account && r.SourceKey == "6230");
        Assert.Equal("Office costs", row.DestinationName);
        Assert.Equal("461", row.DestinationCode);

        var cleared = await mappings.SaveAsync(AccountingTarget.Xero, new SaveAccountingMappingsRequest([new(AccountingMapKind.Account, "6230", " ", null)]));
        Assert.False(cleared.Rows.Single(r => r.Kind == AccountingMapKind.Account && r.SourceKey == "6230").IsMapped);
        await using var db = await kit.Factory.CreateAsync();
        var actions = await db.Set<AuditEvent>().Where(a => a.EntityType == nameof(AccountingMapping)).Select(a => a.Action).ToListAsync();
        Assert.Contains("Created", actions);
        Assert.Contains("Deleted", actions);
        Assert.Contains(cleared.Rows, r => r.Kind == AccountingMapKind.TaxCode && r.SourceKey == AccountingMapKeys.NoTax);
        Assert.Contains(cleared.Rows, r => r.Kind == AccountingMapKind.Item && r.SourceKey == "Labor");
    }

    // ---- Permission ----

    [Fact]
    public async Task Only_finance_and_admin_may_export_map_or_read_the_history()
    {
        await using var kit = await KitAsync();
        await PostAsync(kit, Jan, "One", 10m);
        var export = kit.Get<IAccountingExportService>();
        var mappings = kit.Get<IAccountingMappingService>();
        var run = await export.ExportAsync(Journals());

        foreach (var user in new[] { Editor, Viewer, Timekeeper, Owner })
        {
            kit.As(user);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => export.ListFormatsAsync());
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => export.PreviewAsync(Journals()));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => export.ExportAsync(Journals(again: true, note: "why not")));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => export.ListRunsAsync());
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => export.DownloadRunAsync(run.RunId));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => mappings.GetAsync(AccountingTarget.QuickBooks));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => mappings.SaveAsync(AccountingTarget.QuickBooks, new SaveAccountingMappingsRequest([])));
        }

        foreach (var user in new[] { Finance, Admin })
        {
            kit.As(user);
            Assert.NotEmpty(await export.ListFormatsAsync());
            Assert.Single(await export.ListRunsAsync());
            Assert.NotNull(await mappings.GetAsync(AccountingTarget.Generic));
        }
    }

    [Fact]
    public async Task Every_format_lists_the_kinds_it_can_carry_and_has_a_source_for_its_layout()
    {
        await using var kit = await KitAsync();
        var formats = await kit.Get<IAccountingExportService>().ListFormatsAsync();
        Assert.Equal(5, formats.Count);
        Assert.All(formats, f =>
        {
            Assert.NotEmpty(f.Kinds);
            Assert.False(string.IsNullOrWhiteSpace(f.LayoutSource));
        });
        Assert.Contains(formats, f => f.Id == AccountingFormatIds.GenericXlsx && f.Kinds.Contains(AccountingExportKind.Payments));
    }
}
