using Workbench.Application;
using Workbench.Application.Customization;
using Workbench.Contracts.Customization;
using Workbench.Domain.Companies;
using Workbench.Domain.Purchasing;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Customization;

public sealed class NumberingTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public async Task Default_patterns_keep_todays_formats_and_continue_after_existing_numbers()
    {
        var factory = TestDb.CreateFactory();
        await using (var db = await factory.CreateAsync())
        {
            var vendor = new Vendor { Code = "ACME", Name = "Acme" };
            db.Set<Vendor>().Add(vendor);
            db.Set<PurchaseOrder>().Add(new PurchaseOrder { Number = "PO-000041", VendorId = vendor.Id, CreatedBy = "seed" });
            await db.SaveChangesAsync();
        }

        var numbers = new DocumentNumbers(factory, Clock);
        Assert.Equal("PO-000042", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder));
        Assert.Equal("PO-000043", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder));
        Assert.Equal("RFQ-000001", await numbers.NextAsync(DocumentNumberTypes.Rfq));
        Assert.Equal("NCR-000001", await numbers.NextAsync(DocumentNumberTypes.Nonconformance));
        Assert.Equal("W56HZV26C0042-0001", await numbers.NextAsync(DocumentNumberTypes.Invoice, new(Contract: "W56HZV26C0042")));
        Assert.Equal("W56HZV26C0043-0001", await numbers.NextAsync(DocumentNumberTypes.Invoice, new(Contract: "W56HZV26C0043")));

        // A number someone stored some other way is skipped, never duplicated.
        await using (var db = await factory.CreateAsync())
        {
            db.Set<PurchaseOrder>().Add(new PurchaseOrder { Number = "PO-000044", VendorId = db.Set<Vendor>().First().Id, CreatedBy = "import" });
            await db.SaveChangesAsync();
        }

        Assert.Equal("PO-000045", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder));
    }

    [Fact]
    public async Task Admin_patterns_use_tokens_and_per_company_and_per_year_sequences()
    {
        await using var host = (await SeededHost.CreateAsync()).As(SeededHost.Admin);
        var factory = host.Host.Get<IWorkbenchDbContextFactory>();
        var fed = new Company { Code = "FED", LegalName = "Federal Sub LLC" };
        await using (var db = await factory.CreateAsync())
        {
            if (!db.Set<Company>().Any(c => c.Id == Company.DefaultId))
            {
                db.Set<Company>().Add(new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main Co" }); // Migrations seed it.
            }

            db.Set<Company>().Add(fed);
            await db.SaveChangesAsync();
        }

        var numbering = host.Host.Get<INumberingService>();
        Assert.Contains(await numbering.ListAsync(), s => s.DocumentType == "purchase-order" && s.Pattern == "PO-{seq:6}" && !s.IsCustomized);
        Assert.NotNull((await numbering.PreviewAsync("purchase-order", new("PO-{YYYY}", ResetEachYear: true))).Error); // No sequence.
        await Assert.ThrowsAsync<ArgumentException>(() => numbering.SaveAsync("purchase-order", new("PO-{seq:6}", ResetEachYear: true))); // Would repeat.
        await Assert.ThrowsAsync<ArgumentException>(() => numbering.SaveAsync("purchase-order", new("PO-{seq:6}", PerCompany: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => numbering.SaveAsync("purchase-order", new("PO-{company}-{YYYY}-{seq:12}"))); // Longer than the column.
        await Assert.ThrowsAsync<ArgumentException>(() => numbering.SaveAsync("invoice", new("INV-{seq:6}"))); // Invoices count per contract.

        var saved = await numbering.SaveAsync("purchase-order", new("{company}-{YY}-{seq:4}", PerCompany: true, ResetEachYear: true));
        Assert.True(saved.IsCustomized);
        Assert.Matches(@"^(MAIN|FED)-\d{2}-0123$", saved.Example);

        var numbers = new DocumentNumbers(factory, Clock);
        Assert.Equal("MAIN-26-0001", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder, new(Date: new DateOnly(2026, 3, 1))));
        Assert.Equal("MAIN-26-0002", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder, new(Date: new DateOnly(2026, 4, 1))));
        Assert.Equal("FED-26-0001", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder, new(fed.Id, new DateOnly(2026, 4, 1))));
        Assert.Equal("MAIN-27-0001", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder, new(Date: new DateOnly(2027, 1, 5))));

        await numbering.SaveAsync("rfq", new("RFQ-FY{FY}-{seq:5}", ResetEachYear: true));
        Assert.Equal("RFQ-FY2027-00001", await numbers.NextAsync(DocumentNumberTypes.Rfq, new(Date: new DateOnly(2026, 10, 15)))); // October starts FY2027.
        Assert.Equal("RFQ-FY2026-00001", await numbers.NextAsync(DocumentNumberTypes.Rfq, new(Date: new DateOnly(2026, 9, 30))));

        await numbering.ResetAsync("purchase-order");
        Assert.Equal("PO-000001", await numbers.NextAsync(DocumentNumberTypes.PurchaseOrder));

        host.As(SeededHost.Finance);
        await Assert.ThrowsAsync<Contracts.WorkbenchForbiddenException>(() => numbering.SaveAsync("purchase-order", new("X-{seq:6}")));
    }

    [Fact]
    public async Task In_unit_of_work_numbers_are_gapless_and_unique_within_the_save()
    {
        var factory = TestDb.CreateFactory();
        await using (var db = await factory.CreateAsync())
        {
            var a = await DocumentNumbers.NextInUnitOfWorkAsync(db, DocumentNumberTypes.Inspection, null, new DateOnly(2026, 10, 20), default);
            var b = await DocumentNumbers.NextInUnitOfWorkAsync(db, DocumentNumberTypes.Inspection, null, new DateOnly(2026, 10, 20), default);
            Assert.Equal(("INS-000001", "INS-000002"), (a.Number, b.Number));
            // Not saved: nothing was allocated.
        }

        await using (var db = await factory.CreateAsync())
        {
            Assert.Equal("INS-000001", (await DocumentNumbers.NextInUnitOfWorkAsync(db, DocumentNumberTypes.Inspection, null, new DateOnly(2026, 10, 20), default)).Number);
            await db.SaveChangesAsync();
        }

        Assert.Equal("INS-000002", await new DocumentNumbers(factory, Clock).NextAsync(DocumentNumberTypes.Inspection));
    }

    [Fact]
    public Task Concurrent_numbers_never_repeat_on_sqlite() => ConcurrentAsync(TestDatabase.Sqlite);

    [PostgresFact]
    public Task Concurrent_numbers_never_repeat_on_postgresql() => ConcurrentAsync(TestDatabase.PostgreSql);

    private static async Task ConcurrentAsync(TestDatabase provider)
    {
        var factory = TestDb.CreateFactory(provider);
        await using (var db = await factory.CreateAsync())
        {
            var vendor = new Vendor { Code = "ACME", Name = "Acme" };
            db.Set<Vendor>().Add(vendor);
            db.Set<PurchaseOrder>().Add(new PurchaseOrder { Number = "PO-000010", VendorId = vendor.Id, CreatedBy = "seed" });
            await db.SaveChangesAsync();
        }

        // Every request has its own allocator and context, like separate web requests.
        const int Requests = 24;
        var results = await Task.WhenAll(Enumerable.Range(0, Requests).Select(_ => Task.Run(() => new DocumentNumbers(factory, Clock).NextAsync(DocumentNumberTypes.PurchaseOrder))));
        Assert.Equal(Requests, results.Distinct().Count());
        Assert.Equal(Enumerable.Range(11, Requests).Select(n => $"PO-{n:000000}").Order(), results.Order());
    }
}
