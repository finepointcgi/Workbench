using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Billing;
using Workbench.Domain.Companies;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>A DD250 item with what WAWF needs besides: the original part number (for the unique item identifiers).</summary>
internal sealed record WawfItem(Dd250ItemDto Item, string PartNumber);

/// <summary>Everything the DD250 and the WAWF file are made from, for one shipment.</summary>
internal sealed record ShippingPaperwork(
    Dd250Dto Dd250,
    IReadOnlyList<WawfItem> Items,
    string? IssuedByCode,
    string? InspectByCode,
    string? AcceptByCode,
    bool InvoiceApproved,
    decimal? InvoiceTotal,
    IReadOnlyList<ShipmentPackage> Packages);

/// <summary>Builds the DD Form 250 from a shipment, its order, the contract's billing settings and the company.</summary>
internal static class ShippingDocuments
{
    public const string Unassigned = "(on shipping)";

    public static async Task<ShippingPaperwork> LoadAsync(IWorkbenchDbContext db, Guid shipmentId, CancellationToken ct)
    {
        var shipment = await db.Set<Shipment>().AsNoTracking().Include(s => s.Lines).Include(s => s.Packages).FirstOrDefaultAsync(s => s.Id == shipmentId, ct)
            ?? throw WorkbenchNotFoundException.For("Shipment", shipmentId);
        var order = await db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == shipment.SalesOrderId, ct);
        var contract = order.ContractId is { } cid ? await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var settings = contract is null ? null : await db.Set<ContractBillingSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.ContractId == contract.Id, ct);
        var profile = await db.Set<CustomerBillingProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == order.CustomerId, ct);
        var company = await db.Set<Company>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == order.CompanyId, ct);
        var customer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == order.CustomerId, ct);
        var invoice = shipment.InvoiceId is { } iid ? await db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == iid, ct) : null;
        var partIds = shipment.Lines.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var warehouseId = shipment.Lines.Select(l => (Guid?)l.WarehouseId).FirstOrDefault();
        var warehouse = warehouseId is { } wid ? await db.Set<Warehouse>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wid, ct) : null;

        var cage = Clean(settings?.CageCode);
        var warnings = new List<string>();
        if (contract is null)
        {
            warnings.Add("The order has no contract: a DD250 is for deliveries on government contracts.");
        }

        if (cage is null)
        {
            warnings.Add("The contract has no CAGE code (contract billing settings).");
        }

        if (shipment.Status != ShipmentStatus.Confirmed)
        {
            warnings.Add("Draft: the shipment number and date are set when the shipment is confirmed.");
        }

        if (shipment.ShipToCode is null)
        {
            warnings.Add("The ship-to has no DoDAAC code.");
        }

        var orderLines = order.Lines.ToDictionary(l => l.Id);
        var items = shipment.Lines.GroupBy(l => l.SalesOrderLineId).OrderBy(g => orderLines[g.Key].LineNumber).Select(g =>
        {
            var ol = orderLines[g.Key];
            var part = parts[ol.PartId];
            var quantity = g.Sum(l => l.Quantity);
            var item = new Dd250ItemDto(
                ol.ItemNumber ?? ol.LineNumber.ToString("D4", CultureInfo.InvariantCulture),
                ol.StockNumber,
                part.Display,
                ol.Description,
                g.Where(l => l.Serial.Length > 0).Select(l => l.Serial).Distinct().Order(StringComparer.Ordinal).ToList(),
                g.Where(l => l.Lot.Length > 0).Select(l => l.Lot).Distinct().Order(StringComparer.Ordinal).ToList(),
                quantity,
                part.UnitOfMeasure,
                ol.UnitPrice,
                Math.Round(quantity * ol.UnitPrice, 2, MidpointRounding.AwayFromZero),
                g.Any(l => l.Ownership == InventoryOwnership.GovernmentFurnished),
                ol.UidRequired);
            return new WawfItem(item, part.Number);
        }).ToList();

        var terms = settings?.PaymentTermsDays ?? profile?.PaymentTermsDays;
        var (adminCode, adminName) = Office(settings?.AdminOffice);
        var (payCode, payName) = Office(settings?.PayingOffice);
        var name = company?.LegalName ?? "Contractor";
        var dd250 = new Dd250Dto(
            shipment.Id,
            contract?.ContractNumber ?? "",
            order.CustomerReference,
            shipment.ShipmentNumber ?? Unassigned,
            shipment.ShipDate,
            shipment.BillOfLading,
            terms is { } t ? $"Net {t} days" : null,
            invoice?.Number,
            invoice?.InvoiceDate,
            order.InspectionPoint.ToContract(),
            order.AcceptancePoint.ToContract(),
            new Dd250PartyDto(cage, name, warehouse?.Address),
            new Dd250PartyDto(adminCode, adminName ?? "", null),
            new Dd250PartyDto(cage, warehouse is null ? name : $"{name}, {warehouse.Name}", warehouse?.Address),
            new Dd250PartyDto(payCode, payName ?? "", profile?.BillingAddress),
            new Dd250PartyDto(shipment.ShipToCode, shipment.ShipToName ?? customer.Name, shipment.ShipToAddress),
            order.MarkForCode is { } mark ? new Dd250PartyDto(mark, "", null) : null,
            shipment.Carrier,
            shipment.Packages.Count,
            shipment.Packages.Sum(p => p.Weight),
            items.Select(i => i.Item).ToList(),
            items.Sum(i => i.Item.Amount),
            shipment.IsFinal,
            shipment.Status == ShipmentStatus.Confirmed,
            warnings);
        return new ShippingPaperwork(dd250, items, order.IssuedByCode, order.InspectByCode, order.AcceptByCode,
            invoice is { Status: not InvoiceStatus.Draft and not InvoiceStatus.Void }, invoice?.Total, shipment.Packages.OrderBy(p => p.Number).ToList());
    }

    /// <summary>
    /// Splits an office into its DoDAAC and name: "DFAS Columbus, HQ0338" or "HQ0338 DFAS Columbus" → ("HQ0338", "DFAS Columbus").
    /// The DoDAAC is a 6-character word with a digit in it; without one the code is empty.
    /// </summary>
    internal static (string? Code, string? Name) Office(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var t = text.Trim();
        var code = Regex.Matches(t, @"(?<![A-Za-z0-9])[A-Za-z0-9]{6}(?![A-Za-z0-9])").Select(m => m.Value).LastOrDefault(v => v.Any(char.IsAsciiDigit));
        if (code is null)
        {
            return (null, t);
        }

        var name = Regex.Replace(t, $@"(?<![A-Za-z0-9]){code}(?![A-Za-z0-9])", "").Trim(' ', ',', '-', ';', '\n');
        name = Regex.Replace(name, @"\s*,\s*,", ",").Trim(' ', ',');
        return (code.ToUpperInvariant(), name.Length == 0 ? null : name);
    }

    internal static string? Clean(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
}

/// <summary>
/// The WAWF (PIEE) receiving report import file: validation of the fields PIEE rejects, and the XML (header, routing
/// DoDAACs, transportation, line items with UID data, and for Combo / 2-in-1 the invoice). It follows the structure of
/// PIEE's receiving report import but hasn't been certified against the official schema.
/// </summary>
internal static class WawfFile
{
    public static WawfValidationDto Validate(ShippingPaperwork p, WawfDocumentType type)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var d = p.Dd250;
        if (!d.IsConfirmed)
        {
            errors.Add("Confirm the shipment first.");
        }

        if (d.ContractNumber.Length == 0)
        {
            errors.Add("WAWF receiving reports are for government contracts; this order has no contract.");
        }
        else if (!Regex.IsMatch(Piid(d.ContractNumber), "^[A-Z0-9]{6}[0-9]{2}[A-Z][A-Z0-9]{4}$"))
        {
            errors.Add($"The contract number {d.ContractNumber} isn't a 13-character PIID (for example W56KGU-26-C-0001).");
        }

        if (d.DeliveryOrder is { } order && Piid(order).Length > 13)
        {
            errors.Add($"The delivery order number {order} is longer than 13 characters.");
        }

        Require(errors, d.PrimeContractor.Code, "^[A-Z0-9]{5}$", "the contractor's CAGE code (contract billing settings)", 5);
        if (d.IsConfirmed && !Regex.IsMatch(d.ShipmentNumber, "^[A-Z]{3}[0-9]{4}Z?$"))
        {
            errors.Add($"The shipment number {d.ShipmentNumber} must be three letters and four digits (with Z on the final shipment).");
        }

        Require(errors, p.IssuedByCode, "^[A-Z0-9]{6}$", "the issued-by DoDAAC (on the sales order)", 6);
        Require(errors, d.AdministeredBy.Code, "^[A-Z0-9]{6}$", "the administered-by DoDAAC (contract billing settings, admin office)", 6);
        Require(errors, d.ShippedTo.Code, "^[A-Z0-9]{6}$", "the ship-to DoDAAC", 6);
        if (d.InspectionPoint == GovernmentInspectionPoint.Origin)
        {
            Require(errors, p.InspectByCode, "^[A-Z0-9]{6}$", "the inspect-by DoDAAC (inspection at origin)", 6);
        }

        if (d.AcceptancePoint == GovernmentInspectionPoint.Origin)
        {
            Require(errors, p.AcceptByCode, "^[A-Z0-9]{6}$", "the accept-by DoDAAC (acceptance at origin)", 6);
        }

        if (d.MarkedFor?.Code is { } mark && !Regex.IsMatch(mark, "^[A-Z0-9]{6}$"))
        {
            errors.Add($"The mark-for code {mark} must be a 6-character DoDAAC.");
        }

        if (d.Items.Count == 0)
        {
            errors.Add("The shipment has no items.");
        }

        foreach (var i in p.Items)
        {
            var item = i.Item;
            if (!Regex.IsMatch(item.ItemNumber, "^[0-9]{4}([A-Z0-9]{2})?$"))
            {
                errors.Add($"Item {item.ItemNumber}: WAWF item numbers are a 4-digit CLIN or a 6-character SLIN (for example 0001 or 0001AA).");
            }

            if (item.Quantity <= 0)
            {
                errors.Add($"Item {item.ItemNumber}: the quantity shipped must be more than zero.");
            }

            if (item.UidMarked)
            {
                if (item.Serials.Count != item.Quantity)
                {
                    errors.Add($"Item {item.ItemNumber} is UID-marked: every unit needs a serial number ({item.Quantity:0.####} units, {item.Serials.Count} serials).");
                }

                if (p.Dd250.PrimeContractor.Code is { } cage)
                {
                    foreach (var s in item.Serials.Where(s => Uii(cage, i.PartNumber, s).Length > 50))
                    {
                        errors.Add($"Item {item.ItemNumber} serial {s}: the unique item identifier is longer than 50 characters.");
                    }
                }
            }

            if (item.GovernmentFurnished)
            {
                warnings.Add($"Item {item.ItemNumber} includes government-furnished material.");
            }
        }

        if (type != WawfDocumentType.ReceivingReport)
        {
            if (d.InvoiceNumber is null)
            {
                errors.Add("A Combo or 2-in-1 includes the invoice: create the delivery invoice first.");
            }
            else
            {
                if (!p.InvoiceApproved)
                {
                    errors.Add($"Approve invoice {d.InvoiceNumber} first.");
                }

                if (d.InvoiceNumber.Length > 22)
                {
                    errors.Add($"Invoice number {d.InvoiceNumber} is longer than WAWF's 22 characters.");
                }
            }

            Require(errors, d.PaymentBy.Code, "^[A-Z0-9]{6}$", "the pay office DoDAAC (contract billing settings, paying office)", 6);
            if (p.Items.Any(i => i.Item.UnitPrice <= 0))
            {
                errors.Add("Every item needs a unit price on an invoice.");
            }
        }

        if (type == WawfDocumentType.TwoInOne)
        {
            warnings.Add("A 2-in-1 is for services; deliveries of supplies normally use a Combo.");
        }

        return new WawfValidationDto(type.ToContract(), errors, warnings);
    }

    private static void Require(List<string> errors, string? value, string pattern, string what, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add($"Enter {what}.");
        }
        else if (!Regex.IsMatch(value, pattern))
        {
            errors.Add($"{char.ToUpperInvariant(what[0])}{what[1..]} must be {length} letters or digits ({value}).");
        }
    }

    /// <summary>The contract number without dashes or spaces.</summary>
    public static string Piid(string number) => new(number.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>UII construct #2 (DFARS 252.211-7003): issuing agency "D" (CAGE), the CAGE code, the original part number and the serial number.</summary>
    public static string Uii(string cage, string partNumber, string serial) =>
        new(("D" + cage + partNumber + serial).ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '/').ToArray());

    public static byte[] Write(ShippingPaperwork p, WawfDocumentType type)
    {
        var d = p.Dd250;
        var inv = CultureInfo.InvariantCulture;
        var cage = d.PrimeContractor.Code ?? "";
        string Money(decimal v) => v.ToString("0.00", inv);
        string Qty(decimal v) => v.ToString("0.####", inv);
        var kind = type switch
        {
            WawfDocumentType.Combo => "Combo",
            WawfDocumentType.TwoInOne => "2-in-1",
            _ => "Receiving Report",
        };
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("WawfReceivingReport",
                new XAttribute("documentType", kind),
                new XElement("Header",
                    new XElement("ContractNumber", Piid(d.ContractNumber)),
                    d.DeliveryOrder is { } o ? new XElement("DeliveryOrderNumber", Piid(o)) : null,
                    new XElement("ShipmentNumber", d.ShipmentNumber),
                    new XElement("ShipmentDate", d.DateShipped?.ToString("yyyy-MM-dd", inv)),
                    new XElement("FinalShipment", d.IsFinal ? "Y" : "N"),
                    new XElement("InspectionPoint", d.InspectionPoint == GovernmentInspectionPoint.Origin ? "S" : "D"),
                    new XElement("AcceptancePoint", d.AcceptancePoint == GovernmentInspectionPoint.Origin ? "S" : "D")),
                new XElement("Routing",
                    new XElement("PrimeContractorCage", cage),
                    new XElement("ShipFromCage", d.ShippedFrom.Code),
                    new XElement("IssueByDodaac", p.IssuedByCode),
                    new XElement("AdminByDodaac", d.AdministeredBy.Code),
                    type != WawfDocumentType.ReceivingReport ? new XElement("PayOfficeDodaac", d.PaymentBy.Code) : null,
                    new XElement("ShipToDodaac", d.ShippedTo.Code),
                    d.MarkedFor?.Code is { } mark ? new XElement("MarkForDodaac", mark) : null,
                    p.InspectByCode is { } ib ? new XElement("InspectByDodaac", ib) : null,
                    p.AcceptByCode is { } ab ? new XElement("AcceptByDodaac", ab) : null),
                new XElement("Transportation",
                    d.BillOfLading is { } bol ? new XElement("BillOfLading", bol) : null,
                    d.Carrier is { } carrier ? new XElement("Carrier", carrier) : null,
                    new XElement("Packages",
                        p.Packages.Select(pk => new XElement("Package",
                            new XAttribute("number", pk.Number),
                            new XElement("WeightLb", Qty(pk.Weight)),
                            pk.TrackingNumber is { } tn ? new XElement("TrackingNumber", tn) : null)))),
                type != WawfDocumentType.ReceivingReport
                    ? new XElement("Invoice",
                        new XElement("InvoiceNumber", d.InvoiceNumber),
                        new XElement("InvoiceDate", d.InvoiceDate?.ToString("yyyy-MM-dd", inv)),
                        new XElement("Amount", Money(p.InvoiceTotal ?? d.Total)))
                    : null,
                new XElement("LineItems",
                    p.Items.Select(i => new XElement("LineItem",
                        new XElement("ItemNumber", i.Item.ItemNumber),
                        i.Item.StockNumber is { } nsn ? new XElement("StockPartNumber", new XAttribute("type", "FS"), nsn) : new XElement("StockPartNumber", new XAttribute("type", "VP"), i.PartNumber),
                        new XElement("Description", i.Item.Description),
                        new XElement("QuantityShipped", Qty(i.Item.Quantity)),
                        new XElement("UnitOfMeasure", i.Item.Unit),
                        new XElement("UnitPrice", i.Item.UnitPrice.ToString("0.00####", inv)),
                        new XElement("Amount", Money(i.Item.Amount)),
                        i.Item.GovernmentFurnished ? new XElement("GovernmentFurnished", "Y") : null,
                        i.Item.UidMarked
                            ? new XElement("UidItems", i.Item.Serials.Select(s => new XElement("UidItem",
                                new XElement("Uii", Uii(cage, i.PartNumber, s)),
                                new XElement("Construct", "2"),
                                new XElement("IssuingAgencyCode", "D"),
                                new XElement("EnterpriseIdentifier", cage),
                                new XElement("OriginalPartNumber", i.PartNumber),
                                new XElement("SerialNumber", s),
                                new XElement("UnitAcquisitionCost", i.Item.UnitPrice.ToString("0.00", inv)))))
                            : null))),
                new XElement("Totals", new XElement("Amount", Money(d.Total)))));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            doc.Save(writer);
        }

        return stream.ToArray();
    }
}
