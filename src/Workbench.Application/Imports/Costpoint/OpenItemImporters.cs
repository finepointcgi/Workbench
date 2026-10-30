using Workbench.Contracts.Accounting;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using ImportKind = Workbench.Domain.Imports.ImportKind;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>Costpoint vendors (Manage Vendors / VEND export) → vendors, matched on the vendor id.</summary>
public class VendorImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.Vendors;
    public override string Title => "Vendors";
    public override string Description => "Vendors with payment terms, 1099 flag, UEI and contact. Needed before open AP bills.";
    public override string? SampleFile => "samples/import/costpoint/vendors.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Code", "Vendor ID", true, "Natural key: the Costpoint vendor id.", "VEND_ID", "Vendor ID", "Vendor Code", "Vendor"),
        new("Name", "Vendor name", true, "", "VEND_NAME", "Vendor Name", "Name"),
        new("Active", "Active", false, "Y/N or Active/Inactive/Hold. Defaults to active.", "ACTIVE_FL", "Status", "Active"),
        new("Is1099", "1099", false, "Y/N.", "PRINT_1099_FL", "1099 Flag", "1099", "Is 1099"),
        new("Terms", "Payment terms (days)", false, "Days, or terms like Net 30. Defaults to 30.", "TERMS_DC", "Terms", "Payment Terms", "Net Days"),
        new("Uei", "UEI", false, "SAM.gov unique entity id.", "UEI", "Unique Entity ID", "SAM UEI"),
        new("BusinessSize", "Business size", false, "Small or Large.", "BUS_SIZE", "Business Size", "Size"),
        new("ContactName", "Contact", false, "", "CNTACT_NAME", "Contact Name", "Contact"),
        new("ContactEmail", "Contact email", false, "", "EMAIL_ID", "Contact Email", "Email"),
        new("ContactPhone", "Contact phone", false, "", "PHONE_ID", "Phone", "Contact Phone"),
        new("RemitTo", "Remit-to address", false, "", "REMIT_ADDR", "Remit To", "Address"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var vendors = Service<IVendorService>(context);
        var existing = (await vendors.ListAsync(includeInactive: true, cancellationToken)).ToDictionary(v => v.Code, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var code = row.Required("Code");
            var name = row.Required("Name");
            var status = row.Get("Active") is { } s ? ParseStatus(row, s) : VendorState.Active;
            var is1099 = row.Bool("Is1099") ?? false;
            var terms = ParseTerms(row);
            if (row.HasErrors || code is null || name is null)
            {
                continue;
            }

            if (code.Length > 32)
            {
                row.Error("Code", $"Vendor id {code} is longer than 32 characters.");
                continue;
            }

            if (!seen.Add(code))
            {
                row.Error("Code", $"Vendor {code} appears more than once in the file.");
                continue;
            }

            existing.TryGetValue(code, out var vendor);
            var request = new SaveVendorRequest(code, name, status, is1099, vendor?.Form1099Box, row.Get("Uei") ?? vendor?.Uei, row.Get("BusinessSize") ?? vendor?.BusinessSize,
                vendor?.SocioEconomicCategories, terms ?? vendor?.PaymentTermsDays ?? 30, row.Get("RemitTo") ?? vendor?.RemitToAddress, row.Get("ContactName") ?? vendor?.ContactName,
                row.Get("ContactEmail") ?? vendor?.ContactEmail, row.Get("ContactPhone") ?? vendor?.ContactPhone, ConfirmDuplicate: true);
            if (vendor is not null)
            {
                var changed = Changed(("Name", vendor.Name, name), ("Status", vendor.Status, status), ("1099", vendor.Is1099, is1099),
                    ("Terms", vendor.PaymentTermsDays, request.PaymentTermsDays), ("UEI", vendor.Uei, request.Uei), ("Size", vendor.BusinessSize, request.BusinessSize),
                    ("Contact", vendor.ContactName, request.ContactName), ("Email", vendor.ContactEmail, request.ContactEmail), ("Phone", vendor.ContactPhone, request.ContactPhone),
                    ("Remit to", vendor.RemitToAddress, request.RemitToAddress));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Vendor", code, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(Vendor), vendor.Id, "Vendor", code, UpdateSummary(changed),
                    () => vendors.UpdateAsync(vendor.Id, request, cancellationToken), cancellationToken);
                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(Vendor), "Vendor", code, name,
                async () => (await vendors.CreateAsync(request, cancellationToken)).Id, cancellationToken);
        }
    }

    private static VendorState ParseStatus(MappedRow row, string text)
    {
        var key = FieldMatcher.Normalize(text);
        if (key is "h" or "hold" or "onhold")
        {
            return VendorState.OnHold;
        }

        if (MappedRow.ParseBool(text) is { } active)
        {
            return active ? VendorState.Active : VendorState.Inactive;
        }

        row.Warning("Active", $"Status '{text}' isn't recognized; the vendor is imported as active.");
        return VendorState.Active;
    }

    private static int? ParseTerms(MappedRow row)
    {
        var text = row.Get("Terms");
        if (text is null)
        {
            return null;
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length is > 0 and <= 3)
        {
            return int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (FieldMatcher.Normalize(text) is "dueonreceipt" or "receipt" or "cod")
        {
            return 0;
        }

        row.Warning("Terms", $"Terms '{text}' aren't a number of days; 30 is used.");
        return null;
    }
}

/// <summary>
/// Open Costpoint AP vouchers at cut-over → AP bills for their open balance, posted without a journal entry (the AP
/// balance comes in with the GL opening balances). Several rows with one voucher (or vendor and invoice) are its lines.
/// </summary>
public class OpenApBillImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.OpenApBills;
    public override string Title => "Open AP bills";
    public override string Description => "Unpaid vendor invoices at cut-over, for their open balance. Not posted: the AP balance comes in with the opening balances.";
    public override string? SampleFile => "samples/import/costpoint/open-ap.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Vendor", "Vendor ID", true, "Vendor id (import vendors first).", "VEND_ID", "Vendor ID", "Vendor"),
        new("InvoiceNumber", "Vendor invoice", true, "The vendor's invoice number.", "INVC_ID", "Invoice ID", "Invoice Number", "Vendor Invoice", "Invoice"),
        new("Voucher", "Voucher", false, "Costpoint voucher number (kept as Workbench's voucher number; 16 characters).", "VCHR_NO", "Voucher Number", "Voucher No", "Voucher"),
        new("InvoiceDate", "Invoice date", true, "", "INVC_DT", "Invoice Date", "Inv Date"),
        new("DueDate", "Due date", false, "Defaults to the invoice date plus the vendor's terms.", "DUE_DT", "Due Date"),
        new("Amount", "Invoice amount", false, "Original amount. With a balance column, only the balance is carried over.", "INVC_AMT", "Invoice Amount", "Amount", "Gross Amount"),
        new("Balance", "Open balance", false, "What's still owed. Defaults to the amount less any paid amount.", "OPEN_AMT", "Open Amount", "Balance", "Balance Due", "Unpaid Amount"),
        new("Paid", "Paid", false, "Paid so far (used when there's no balance column).", "PAID_AMT", "Paid Amount", "Amount Paid", "Paid"),
        new("Retainage", "Retainage", false, "Held back; owed later.", "RETAINAGE_AMT", "Retainage", "Retention"),
        new("Account", "Account", false, "Expense or asset account of the line (for reference; nothing posts).", "ACCT_ID", "Account ID", "Account"),
        new("Org", "Org", false, "", "ORG_ID", "Org ID", "Org"),
        new("Description", "Description", false, "", "VCHR_LN_DESC", "Description", "Memo", "Line Description"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var vendors = (await Service<IVendorService>(context).ListAsync(includeInactive: true, cancellationToken)).ToDictionary(v => v.Code, StringComparer.OrdinalIgnoreCase);
        var accounts = (await Service<IChartOfAccountsService>(context).ListAccountsAsync(includeInactive: true, cancellationToken)).ToDictionary(a => a.Number, a => a.Id, StringComparer.OrdinalIgnoreCase);
        var orgs = (await Service<IPeopleService>(context).ListOrgUnitsAsync(cancellationToken)).ToDictionary(o => o.Code, o => o.Id, StringComparer.OrdinalIgnoreCase);
        var conversion = Service<OpenItemConversion>(context);
        var (vouchers, vendorInvoices) = await conversion.BillKeysAsync(cancellationToken);
        if (!rows[0].IsMapped("Amount") && !rows[0].IsMapped("Balance"))
        {
            context.Run.Error(rows[0].FileName, 0, "Balance", "Map an open balance column (or the invoice amount).");
            return;
        }

        var parsed = new List<(MappedRow Row, VendorDto Vendor, string Invoice, string? Voucher, DateOnly Date, DateOnly? Due, decimal Open, decimal Retainage, Guid? Account, Guid? Org, string? Description)>();
        foreach (var row in rows)
        {
            var vendorCode = row.Required("Vendor");
            var invoice = row.Required("InvoiceNumber");
            var date = row.Date("InvoiceDate");
            if (date is null && row.Get("InvoiceDate") is null)
            {
                row.Error("InvoiceDate", "Invoice date is required.");
            }

            var due = row.Date("DueDate");
            var amount = row.Decimal("Amount");
            var balance = row.Decimal("Balance");
            var paid = row.Decimal("Paid") ?? 0m;
            var retainage = row.Decimal("Retainage") ?? 0m;
            if (row.HasErrors || vendorCode is null || invoice is null || date is null)
            {
                continue;
            }

            if (!vendors.TryGetValue(vendorCode, out var vendor))
            {
                row.Error("Vendor", $"Vendor {vendorCode} isn't in Workbench; import vendors first.");
                continue;
            }

            Guid? accountId = null, orgId = null;
            if (row.Get("Account") is { } number)
            {
                if (!accounts.TryGetValue(number, out var aid))
                {
                    row.Error("Account", $"Account {number} isn't in Workbench.");
                    continue;
                }

                accountId = aid;
            }

            if (row.Get("Org") is { } org)
            {
                if (!orgs.TryGetValue(org, out var oid))
                {
                    row.Error("Org", $"Org {org} isn't in Workbench.");
                    continue;
                }

                orgId = oid;
            }

            var open = balance ?? (amount ?? 0m) - paid;
            parsed.Add((row, vendor, invoice, row.Get("Voucher"), date.Value, due, open, retainage, accountId, orgId, row.Get("Description")));
        }

        var nextVoucher = 1;
        foreach (var group in parsed.GroupBy(p => p.Voucher is { } v ? $"V:{v.ToUpperInvariant()}" : $"I:{p.Vendor.Id}:{p.Invoice.ToUpperInvariant()}"))
        {
            var first = group.First();
            var open = group.Sum(g => g.Open);
            var retainage = group.Sum(g => g.Retainage);
            var key = $"{first.Vendor.Code} {first.Invoice}";
            if (group.Select(g => (g.Vendor.Id, g.Invoice.ToUpperInvariant())).Distinct().Count() > 1)
            {
                first.Row.Error("Voucher", $"Voucher {first.Voucher} has rows for more than one vendor invoice.");
                continue;
            }

            if (open == 0)
            {
                context.Run.Skip(first.Row.FileName, first.Row.Number, "AP bill", key, "Paid in full; not open");
                continue;
            }

            if (open < 0)
            {
                first.Row.Error("Balance", $"{key} has a debit (credit memo) balance of {open:N2}; apply it in Costpoint or enter it as a vendor credit after cut-over.");
                continue;
            }

            if (retainage < 0 || retainage > open)
            {
                first.Row.Error("Retainage", $"Retainage {retainage:N2} must be between 0 and the open balance {open:N2}.");
                continue;
            }

            if (vendorInvoices.Contains((first.Vendor.Id, first.Invoice.ToUpperInvariant())))
            {
                context.Run.Skip(first.Row.FileName, first.Row.Number, "AP bill", key, "Already in Workbench");
                continue;
            }

            var voucher = first.Voucher;
            if (voucher is not null && voucher.Length > 16)
            {
                first.Row.Error("Voucher", $"Voucher {voucher} is longer than 16 characters; leave the voucher column unmapped to number them CV-000001...");
                continue;
            }

            if (voucher is not null && vouchers.Contains(voucher))
            {
                first.Row.Error("Voucher", $"Voucher number {voucher} is already used in Workbench.");
                continue;
            }

            while (voucher is null)
            {
                var candidate = $"CV-{nextVoucher++:D6}";
                if (!vouchers.Contains(candidate))
                {
                    voucher = candidate;
                }
            }

            vouchers.Add(voucher);
            vendorInvoices.Add((first.Vendor.Id, first.Invoice.ToUpperInvariant()));
            var bill = new ConvertedBill(voucher, first.Vendor.Id, first.Invoice, first.Date, first.Due ?? first.Date.AddDays(first.Vendor.PaymentTermsDays), retainage,
                $"Open balance carried over from {SourceName(context)} (import job {context.Job.Id.ToString()[..8]})",
                group.Select(g => new ConvertedBillLine(g.Description ?? $"{SourceName(context)} invoice {g.Invoice}", g.Open, g.Account, g.Org)).ToList());
            IReadOnlyList<Guid> lineIds = [];
            var id = await context.Run.CreateAsync(first.Row.FileName, first.Row.Number, typeof(ApBill), "AP bill", key, $"Voucher {voucher}, open {open:N2}, due {bill.DueDate:yyyy-MM-dd}",
                async () =>
                {
                    var (billId, lines) = await conversion.CreateBillAsync(bill, cancellationToken);
                    lineIds = lines;
                    return billId;
                }, cancellationToken);
            if (id is not null && context.Run.Applying)
            {
                // Lines are recorded after their bill, so rollback deletes them first.
                var n = 1;
                foreach (var lineId in lineIds)
                {
                    await context.Run.RecordCreatedAsync(typeof(ApBillLine), lineId, $"AP bill {key} line {n++}", cancellationToken);
                }
            }
        }
    }
}

/// <summary>
/// Open Costpoint receivables at cut-over → customer invoices (sent, not posted: the AR balance comes in with the GL
/// opening balances). Several rows with one invoice number are its lines (by CLIN or project).
/// </summary>
public class OpenArInvoiceImporter : CostpointImporter
{
    public override ImportKind Kind => ImportKind.OpenArInvoices;
    public override string Title => "Open AR invoices";
    public override string Description => "Unpaid customer invoices at cut-over, with what's been paid. Not posted: the AR balance comes in with the opening balances.";
    public override string? SampleFile => "samples/import/costpoint/open-ar.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("InvoiceNumber", "Invoice", true, "Costpoint invoice number (32 characters); kept as Workbench's invoice number.", "INVC_ID", "Invoice ID", "Invoice Number", "Invoice No", "Invoice"),
        new("Contract", "Contract", false, "Contract number. Or map the project.", "PRIME_CONTR_ID", "CONTRACT_ID", "Contract Number", "Contract ID", "Contract"),
        new("Project", "Project", false, "Project id; its contract is used when there's no contract column.", "PROJ_ID", "Project ID", "Project"),
        new("Customer", "Customer", false, "Customer id; checked against the contract's customer.", "CUST_ID", "Customer ID", "Customer"),
        new("InvoiceDate", "Invoice date", true, "", "INVC_DT", "Invoice Date", "Inv Date"),
        new("DueDate", "Due date", false, "Defaults to the invoice date plus 30 days.", "DUE_DT", "Due Date"),
        new("Amount", "Invoice amount", false, "Invoiced amount.", "INVC_AMT", "Invoice Amount", "Amount", "Billed Amount"),
        new("Balance", "Open balance", false, "Still owed. With the amount, paid = amount − balance.", "OPEN_AMT", "Open Amount", "Balance", "Balance Due", "Unpaid Amount"),
        new("Paid", "Paid", false, "Received so far (used when there's no balance column).", "RCV_AMT", "Paid Amount", "Amount Paid", "Received", "Paid"),
        new("Clin", "CLIN", false, "CLIN number on the contract.", "CLIN_ID", "CLIN", "CLIN Number"),
        new("Type", "Invoice type", false, "T&M, Cost plus or Fixed price. Defaults from the contract type.", "INVC_TYPE", "Invoice Type", "Bill Type", "Billing Type"),
        new("Description", "Description", false, "", "DESCRIPTION", "Description", "Memo"),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        if (!rows[0].IsMapped("Amount") && !rows[0].IsMapped("Balance"))
        {
            context.Run.Error(rows[0].FileName, 0, "Balance", "Map the invoice amount or the open balance.");
            return;
        }

        if (!rows[0].IsMapped("Contract") && !rows[0].IsMapped("Project"))
        {
            context.Run.Error(rows[0].FileName, 0, "Contract", "Map the contract or the project.");
            return;
        }

        var contractsService = Service<IContractService>(context);
        var contracts = (await contractsService.ListContractsAsync(cancellationToken)).ToDictionary(c => c.ContractNumber, StringComparer.OrdinalIgnoreCase);
        var byId = contracts.Values.ToDictionary(c => c.Id);
        var customers = (await contractsService.ListCustomersAsync(cancellationToken)).ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var projects = (await Service<IProjectService>(context).ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var clinService = Service<IClinService>(context);
        var clinsByContract = new Dictionary<Guid, IReadOnlyList<ClinDto>>();
        var conversion = Service<OpenItemConversion>(context);
        var existing = await conversion.InvoiceNumbersAsync(cancellationToken);

        var parsed = new List<(MappedRow Row, string Number, ContractDto Contract, Guid? ProjectId, Guid? ClinId, DateOnly Date, DateOnly? Due, decimal Amount, decimal Paid, InvoiceType? Type, string? Description)>();
        foreach (var row in rows)
        {
            var number = row.Required("InvoiceNumber");
            var date = row.Date("InvoiceDate");
            if (date is null && row.Get("InvoiceDate") is null)
            {
                row.Error("InvoiceDate", "Invoice date is required.");
            }

            var due = row.Date("DueDate");
            var amount = row.Decimal("Amount");
            var balance = row.Decimal("Balance");
            var paid = row.Decimal("Paid");
            var type = ParseType(row);
            if (row.HasErrors || number is null || date is null)
            {
                continue;
            }

            if (number.Length > 32)
            {
                row.Error("InvoiceNumber", $"Invoice number {number} is longer than 32 characters.");
                continue;
            }

            ContractDto? contract = null;
            Guid? projectId = null;
            if (row.Get("Project") is { } projectCode)
            {
                if (!projects.TryGetValue(projectCode.Split('.')[0], out var project))
                {
                    row.Error("Project", $"Project {projectCode} isn't in Workbench.");
                    continue;
                }

                projectId = project.Id;
                if (project.ContractId is { } cid && byId.TryGetValue(cid, out var projectContract))
                {
                    contract = projectContract;
                }
            }

            if (row.Get("Contract") is { } contractNumber)
            {
                if (!contracts.TryGetValue(contractNumber, out var named))
                {
                    row.Error("Contract", $"Contract {contractNumber} isn't in Workbench; import contracts first.");
                    continue;
                }

                if (contract is not null && contract.Id != named.Id)
                {
                    row.Error("Project", $"Project {row.Get("Project")} is on contract {contract.ContractNumber}, not {contractNumber}.");
                    continue;
                }

                contract = named;
            }

            if (contract is null)
            {
                row.Error("Contract", "The row has no contract (and its project isn't on one).");
                continue;
            }

            if (row.Get("Customer") is { } customerCode && (!customers.TryGetValue(customerCode, out var customer) || customer.Id != contract.CustomerId))
            {
                row.Error("Customer", $"Customer {customerCode} isn't contract {contract.ContractNumber}'s customer ({contract.CustomerName}).");
                continue;
            }

            Guid? clinId = null;
            if (row.Get("Clin") is { } clinNumber)
            {
                if (!clinsByContract.TryGetValue(contract.Id, out var clins))
                {
                    clinsByContract[contract.Id] = clins = await clinService.ListClinsAsync(contract.Id, cancellationToken);
                }

                var clin = clins.FirstOrDefault(c => string.Equals(c.Number, clinNumber, StringComparison.OrdinalIgnoreCase));
                if (clin is null)
                {
                    row.Error("Clin", $"CLIN {clinNumber} isn't on contract {contract.ContractNumber}.");
                    continue;
                }

                clinId = clin.Id;
            }

            // Amount and paid: from the amount and balance, the amount and paid, or the balance alone (nothing paid).
            var invoiced = amount ?? (balance ?? 0m) + (paid ?? 0m);
            var received = amount is not null && balance is not null ? amount.Value - balance.Value : paid ?? 0m;
            parsed.Add((row, number, contract, projectId, clinId, date.Value, due, invoiced, received, type, row.Get("Description")));
        }

        foreach (var group in parsed.GroupBy(p => p.Number, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (group.Any(g => g.Contract.Id != first.Contract.Id))
            {
                first.Row.Error("Contract", $"Invoice {first.Number} has rows on more than one contract.");
                continue;
            }

            var total = group.Sum(g => g.Amount);
            var paid = group.Sum(g => g.Paid);
            var open = total - paid;
            if (existing.Contains(first.Number))
            {
                context.Run.Skip(first.Row.FileName, first.Row.Number, "Invoice", first.Number, "Already in Workbench");
                continue;
            }

            if (open == 0)
            {
                context.Run.Skip(first.Row.FileName, first.Row.Number, "Invoice", first.Number, "Paid in full; not open");
                continue;
            }

            if (total <= 0 || open < 0 || paid < 0)
            {
                first.Row.Error("Balance", $"Invoice {first.Number}: amount {total:N2}, paid {paid:N2}. Credit balances and overpayments aren't carried over as invoices; enter them as unapplied cash after cut-over.");
                continue;
            }

            existing.Add(first.Number);
            var contractType = Enum.Parse<ContractType>(first.Contract.Type.ToString());
            var invoice = new ConvertedInvoice(first.Number, first.Contract.Id, first.Contract.CustomerId, first.Type ?? OpenItemConversion.DefaultType(contractType),
                first.Date, first.Due ?? first.Date.AddDays(30), paid,
                $"Carried over from {SourceName(context)} at cut-over (import job {context.Job.Id.ToString()[..8]}). Not posted: the receivable is in the GL opening balances.",
                group.Select(g => new ConvertedInvoiceLine(g.Description ?? $"{SourceName(context)} invoice {g.Number}", g.Amount, g.ClinId, g.ProjectId)).ToList());
            IReadOnlyList<Guid> lineIds = [];
            var id = await context.Run.CreateAsync(first.Row.FileName, first.Row.Number, typeof(Invoice), "Invoice", first.Number,
                $"{first.Contract.ContractNumber}: {total:N2} invoiced, {open:N2} open, due {invoice.DueDate:yyyy-MM-dd}",
                async () =>
                {
                    var (invoiceId, lines) = await conversion.CreateInvoiceAsync(invoice, cancellationToken);
                    lineIds = lines;
                    return invoiceId;
                }, cancellationToken);
            if (id is not null && context.Run.Applying)
            {
                var n = 1;
                foreach (var lineId in lineIds)
                {
                    await context.Run.RecordCreatedAsync(typeof(InvoiceLine), lineId, $"Invoice {first.Number} line {n++}", cancellationToken);
                }
            }
        }
    }

    private static InvoiceType? ParseType(MappedRow row) => row.Get("Type") is { } text
        ? FieldMatcher.Normalize(text) switch
        {
            "tm" or "tandm" or "timeandmaterials" or "lh" or "laborhour" => InvoiceType.TimeAndMaterials,
            "cp" or "cost" or "costplus" or "costreimbursable" or "cpff" or "cpaf" or "cpif" => InvoiceType.CostPlus,
            "ffp" or "fp" or "fixedprice" or "milestone" or "firmfixedprice" => InvoiceType.FixedPriceMilestone,
            _ => Warn(row, text),
        }
        : null;

    private static InvoiceType? Warn(MappedRow row, string text)
    {
        row.Warning("Type", $"Invoice type '{text}' isn't recognized; the contract's type is used.");
        return null;
    }
}
