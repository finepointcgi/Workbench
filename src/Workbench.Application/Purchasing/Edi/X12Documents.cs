using System.Globalization;
using System.Text.RegularExpressions;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing.Edi;

/// <summary>An 855 read into what the purchase order acknowledgement needs.</summary>
public sealed record Edi855(string PurchaseOrderNumber, AcknowledgementState Status, string? Note, IReadOnlyList<Edi855Line> Lines);

public sealed record Edi855Line(int LineNumber, decimal? Quantity, decimal? UnitPrice, DateOnly? PromisedDate, string? Code);

public sealed record Edi856(string ShipmentId, DateOnly ShipDate, DateOnly? ExpectedDelivery, string? Carrier, string? TrackingNumber, string PurchaseOrderNumber, IReadOnlyList<(int LineNumber, decimal Quantity)> Lines);

public sealed record Edi810(string InvoiceNumber, DateOnly InvoiceDate, string PurchaseOrderNumber, decimal? Total, IReadOnlyList<(int LineNumber, decimal Quantity, decimal? UnitPrice)> Lines);

/// <summary>
/// The X12 004010 documents Workbench trades, segment by segment (a practical subset of each guideline):
/// <list type="bullet">
/// <item>850 out: BEG (00 original, 05 replace for change orders; SA; PO number; order date), ITD net days, N1*ST ship-to,
/// PO1 (line number, quantity, unit, price, VP part number), PID*F description, DTM*002 need date, CTT and AMT*TT.</item>
/// <item>855 in: BAK02 AD/AK accepted, AC/AE accepted with changes, RJ/RD rejected; BAK03 PO number; PO1 by line number with
/// ACK (IA accepted, IC/IP/IQ/DR changed, IR rejected; ACK04 067/068/017 date in ACK05 is the promised date); MSG note.</item>
/// <item>856 in: BSN02 shipment id, BSN03 ship date; TD5 carrier; REF*BM/CN/2I tracking; DTM*011 ship, *017 delivery;
/// PRF01 PO number; LIN01 the PO line number with SN102 the quantity.</item>
/// <item>810 in: BIG01 invoice date, BIG02 invoice number, BIG04 PO number; IT1 (PO line number, quantity, unit, price); TDS01 total in cents.</item>
/// </list>
/// </summary>
public static partial class X12Documents
{
    public static X12TransactionSet Write850(PurchaseOrder po, IReadOnlyList<PurchaseOrderLine> lines, IReadOnlyDictionary<Guid, string> partNumbers,
        IReadOnlyDictionary<Guid, string> units, string controlNumber)
    {
        var segments = new List<X12Segment>
        {
            X12Segment.Of("BEG", po.Revision == 0 ? "00" : "05", "SA", po.Number, po.Revision == 0 ? "" : po.Revision.ToString(CultureInfo.InvariantCulture), X12.Date(po.OrderDate)),
        };
        if (po.Terms is { } terms && NetDays().Match(terms) is { Success: true } m)
        {
            segments.Add(X12Segment.Of("ITD", "01", "3", "", "", "", "", m.Groups[1].Value));
        }

        if (po.ShipTo is { } shipTo)
        {
            segments.Add(X12Segment.Of("N1", "ST", shipTo.ReplaceLineEndings(" ")[..Math.Min(60, shipTo.ReplaceLineEndings(" ").Length)]));
        }

        foreach (var l in lines.OrderBy(l => l.LineNumber))
        {
            var part = partNumbers.GetValueOrDefault(l.Id);
            segments.Add(X12Segment.Of("PO1", l.LineNumber.ToString(CultureInfo.InvariantCulture), X12.Number(l.Quantity), units.GetValueOrDefault(l.Id) ?? "EA",
                X12.Number(l.UnitPrice), "", part is null ? "" : "VP", part));
            segments.Add(X12Segment.Of("PID", "F", "", "", "", l.Description.Length > 80 ? l.Description[..80] : l.Description));
            if (l.NeededBy is { } need)
            {
                segments.Add(X12Segment.Of("DTM", "002", X12.Date(need)));
            }
        }

        segments.Add(X12Segment.Of("CTT", lines.Count.ToString(CultureInfo.InvariantCulture), X12.Number(lines.Sum(l => l.Quantity))));
        segments.Add(X12Segment.Of("AMT", "TT", po.Total.ToString("0.00", CultureInfo.InvariantCulture)));
        return new X12TransactionSet("850", controlNumber, segments);
    }

    public static Edi855 Read855(X12TransactionSet set)
    {
        var bak = set.First("BAK") ?? throw new X12FormatException("The 855 has no BAK segment.");
        var po = Required(bak[3], "BAK03 (the purchase order number)");
        var status = bak[2] switch
        {
            "AD" or "AK" => AcknowledgementState.Accepted,
            "AC" or "AE" => AcknowledgementState.AcceptedWithChanges,
            "RJ" or "RD" => AcknowledgementState.Rejected,
            var other => throw new X12FormatException($"BAK02 \"{other}\" isn't an acknowledgement type Workbench reads (AD, AK, AC, AE, RJ, RD)."),
        };
        var lines = new List<Edi855Line>();
        Edi855Line? current = null;
        foreach (var s in set.Segments)
        {
            if (s.Id == "PO1")
            {
                if (current is not null)
                {
                    lines.Add(current);
                }

                current = new Edi855Line(LineNumber(s[1], "PO101"), null, null, null, null);
            }
            else if (s.Id == "ACK" && current is not null)
            {
                var date = s[4] is "067" or "068" or "017" ? X12.ParseDate(s[5]) : null;
                current = current with
                {
                    Code = s[1],
                    Quantity = X12.ParseDecimal(s[2]) ?? current.Quantity,
                    PromisedDate = date ?? current.PromisedDate,
                };
            }
            else if (s.Id == "CTP" && current is not null)
            {
                current = current with { UnitPrice = X12.ParseDecimal(s[3]) };
            }
        }

        if (current is not null)
        {
            lines.Add(current);
        }

        var note = string.Join(" ", set.Segments.Where(s => s.Id == "MSG").Select(s => s[1].Trim()).Where(t => t.Length > 0));
        return new Edi855(po, status, note.Length == 0 ? null : note, lines);
    }

    public static Edi856 Read856(X12TransactionSet set)
    {
        var bsn = set.First("BSN") ?? throw new X12FormatException("The 856 has no BSN segment.");
        var shipDate = set.Segments.Where(s => s.Id == "DTM" && s[1] == "011").Select(s => X12.ParseDate(s[2])).FirstOrDefault()
            ?? X12.ParseDate(bsn[3]) ?? throw new X12FormatException("The 856 has no ship date (BSN03 or DTM*011).");
        var delivery = set.Segments.Where(s => s.Id == "DTM" && s[1] == "017").Select(s => X12.ParseDate(s[2])).FirstOrDefault();
        var td5 = set.First("TD5");
        var carrier = td5 is null ? null : NullIfEmpty(td5[5]) ?? NullIfEmpty(td5[3]);
        var tracking = set.Segments.Where(s => s.Id == "REF" && s[1] is "CN" or "BM" or "2I").Select(s => NullIfEmpty(s[2])).FirstOrDefault(t => t is not null);
        var prf = set.First("PRF") ?? throw new X12FormatException("The 856 has no PRF segment (the purchase order number).");
        var lines = new List<(int, decimal)>();
        int? line = null;
        foreach (var s in set.Segments)
        {
            if (s.Id == "LIN")
            {
                line = LineNumber(s[1], "LIN01");
            }
            else if (s.Id == "SN1")
            {
                if (line is null)
                {
                    throw new X12FormatException("An SN1 quantity comes before any LIN item.");
                }

                lines.Add((line.Value, X12.ParseDecimal(s[2]) ?? throw new X12FormatException($"SN102 \"{s[2]}\" isn't a quantity.")));
            }
        }

        return new Edi856(Required(bsn[2], "BSN02 (the shipment id)"), shipDate, delivery, carrier, tracking, Required(prf[1], "PRF01"), lines);
    }

    public static Edi810 Read810(X12TransactionSet set)
    {
        var big = set.First("BIG") ?? throw new X12FormatException("The 810 has no BIG segment.");
        var date = X12.ParseDate(big[1]) ?? throw new X12FormatException($"BIG01 \"{big[1]}\" isn't a date.");
        var lines = set.Segments.Where(s => s.Id == "IT1")
            .Select(s => (LineNumber(s[1], "IT101"),
                X12.ParseDecimal(s[2]) ?? throw new X12FormatException($"IT102 \"{s[2]}\" isn't a quantity."),
                X12.ParseDecimal(s[4])))
            .ToList();
        var tds = set.First("TDS");
        decimal? total = tds is null ? null : X12.ParseDecimal(tds[1]) is { } cents ? cents / 100m : null;
        return new Edi810(Required(big[2], "BIG02 (the invoice number)"), date, Required(big[4], "BIG04 (the purchase order number)"), total, lines);
    }

    private static int LineNumber(string value, string what) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n
        : throw new X12FormatException($"{what} \"{value}\" should be the purchase order line number.");

    private static string Required(string value, string what) =>
        NullIfEmpty(value) ?? throw new X12FormatException($"{what} is missing.");

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"(?i)\bnet\s*(\d{1,3})\b")]
    private static partial Regex NetDays();
}
