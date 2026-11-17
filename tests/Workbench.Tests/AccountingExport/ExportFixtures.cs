using Workbench.Application.AccountingExport;
using Workbench.Contracts.AccountingExport;
using Workbench.Domain.Accounting;

namespace Workbench.Tests.AccountingExport;

/// <summary>A fixed set of documents (with mapped names) for the layout tests, so the golden files never depend on a database.</summary>
internal static class ExportFixtures
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static Resolved Map(string name, string? code = null) => new(name, code, true);

    private static readonly Resolved Cash = Map("Checking", "090");
    private static readonly Resolved Revenue = Map("Services Revenue", "200");
    private static readonly Resolved Ar = Map("Accounts Receivable", "610");
    private static readonly Resolved Ap = Map("Accounts Payable", "800");
    private static readonly Resolved Office = Map("Office Expenses", "461");
    private static readonly Resolved ClassHq = Map("Headquarters", "Region");
    private static readonly Resolved NoTax = Map("Tax Exempt", null);

    public static ExportDataset Dataset(AccountingExportKind kind) => kind switch
    {
        AccountingExportKind.JournalEntries => new ExportDataset
        {
            Kind = kind,
            Journals =
            [
                new(Id, "JE-000001", new DateOnly(2026, 1, 5), "Rent for January", "Manual",
                [
                    new(1, "6120", Office, 1500.00m, 0, "Office rent", ClassHq),
                    new(2, "1010", Cash, 0, 1500.00m, null, null),
                ]),
                new(Id, "JE-000002", new DateOnly(2026, 1, 31), "Accrue =bonus, \"Q1\"", "Manual",
                [
                    new(1, "6020", Office, 250.50m, 0, "Payroll tax", null),
                    new(2, "2130", Ap, 0, 250.50m, null, null),
                ]),
            ],
        },
        AccountingExportKind.Invoices => new ExportDataset
        {
            Kind = kind,
            Invoices =
            [
                new(Id, "INV-1001", Map("Acme Corp"), new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 9), 30, "C-100", null, 1250.00m, false, "1110", Ar,
                [
                    new(1, "Labor", Map("Consulting"), "4020", Revenue, "Senior engineer, January", 10m, 100m, 1000.00m, ClassHq, NoTax),
                    new(2, "Odc", Map("Reimbursable"), "4020", Revenue, "Travel", 1m, 250.00m, 250.00m, null, NoTax),
                ]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "CM-1002", Map("Acme Corp"), new DateOnly(2026, 1, 20), new DateOnly(2026, 1, 20), 0, "C-100", "Credit for travel", -250.00m, true, "1110", Ar,
                [
                    new(1, "Odc", Map("Reimbursable"), "4020", Revenue, "Travel credit", 1m, -250.00m, -250.00m, null, NoTax),
                ]),
            ],
        },
        AccountingExportKind.Payments => new ExportDataset
        {
            Kind = kind,
            Payments = [new(Id, Map("Acme Corp"), new DateOnly(2026, 2, 3), 1250.00m, "Check", "CHK 4411", null, "1010", Cash, Ar, [new("INV-1001", 1250.00m)])],
        },
        AccountingExportKind.Bills => new ExportDataset
        {
            Kind = kind,
            Bills =
            [
                new(Id, "AP-0001", Map("Bolt Consulting"), "B-77", new DateOnly(2026, 1, 12), new DateOnly(2026, 2, 11), 30, "Design work", 900.00m, false, "2010", Ap,
                [
                    new(1, "6220", Office, "Design, phase 1", 600.00m, ClassHq, NoTax),
                    new(2, "6230", Office, "Licences", 300.00m, null, NoTax),
                ]),
                new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "AP-0002", Map("Bolt Consulting"), "CR-9", new DateOnly(2026, 1, 25), new DateOnly(2026, 1, 25), 0, "Credit", 100.00m, true, "2010", Ap,
                [new(1, "6220", Office, "Credit note", 100.00m, null, NoTax)]),
            ],
        },
        _ => new ExportDataset
        {
            Kind = kind,
            Accounts =
            [
                new(Id, "1000", "Assets", AccountType.Asset, null, Map("Assets", "000"), null, true),
                new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "1010", "Cash - operating", AccountType.Asset, "1000", Cash, "Main account", true),
                new(Guid.Parse("55555555-5555-5555-5555-555555555555"), "2010", "Accounts payable", AccountType.Liability, "1000", Ap, null, true),
                new(Guid.Parse("66666666-6666-6666-6666-666666666666"), "4020", "Revenue - time and materials", AccountType.Revenue, null, Revenue, null, true),
                new(Guid.Parse("77777777-7777-7777-7777-777777777777"), "6230", "Office and IT", AccountType.Expense, null, Office, null, true),
            ],
        },
    };

    public static ExportInput Input(AccountingExportKind kind) =>
        new(kind, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Dataset(kind), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
}
