using System.Globalization;
using ClosedXML.Excel;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Pricing;

namespace Workbench.Application.Pricing;

/// <summary>
/// The cost volume: a summary by CLIN and period, the labor build-up, the indirect rate application, the basis of
/// estimate per WBS element, the other direct costs, and the T&amp;M / fully burdened rate table. Text cells that look like
/// formulas are written as text, so nothing in a proposal can run in Excel.
/// </summary>
internal static class CostVolumeWorkbook
{
    private const string MoneyFormat = "#,##0.00;[Red]-#,##0.00";
    private const string RateFormat = "#,##0.0000";
    private const string PercentFormat = "0.00%";
    private const string HoursFormat = "#,##0.00";

    public static byte[] Build(Proposal proposal, PriceModel model, PriceResultDto r)
    {
        using var book = new XLWorkbook();
        Summary(book.Worksheets.Add("Summary"), proposal, model, r);
        Labor(book.Worksheets.Add("Labor build-up"), proposal, r);
        Indirects(book.Worksheets.Add("Indirect rates"), proposal, r);
        Basis(book.Worksheets.Add("Basis of estimate"), proposal, r);
        Other(book.Worksheets.Add("Other direct costs"), proposal, r);
        Rates(book.Worksheets.Add("T&M rates"), proposal, r);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Summary(IXLWorksheet ws, Proposal proposal, PriceModel model, PriceResultDto r)
    {
        var row = Title(ws, proposal, r, "Cost volume summary");
        void Fact(string label, object? value, string? format = null)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            Set(ws.Cell(row, 2), value, format);
            row++;
        }

        Fact("Contract type", Label(r.ContractType));
        Fact("Customer", proposal.Customer);
        Fact("Opportunity", proposal.OpportunityName);
        Fact("Price", model.IsLocked ? $"Version {model.Version}, locked {model.LockedAtUtc:yyyy-MM-dd} by {model.LockedBy}" : $"{model.Name} (not locked)");
        Fact("Indirect rates", $"{BasisLabel(r.RateBasis)} rates");
        Fact("Fee", r.FeeExplanation);
        Fact("Total cost", r.Totals.TotalCost, MoneyFormat);
        Fact("Fee / profit", r.Totals.Fee, MoneyFormat);
        Fact("Total price", r.Totals.Price, MoneyFormat);
        Fact("Wrap rate", r.WrapRate, "0.0000");
        Fact("Margin", r.Margin, PercentFormat);
        if (r.PriceToWin is { } ptw)
        {
            Fact("Target price", ptw.TargetPrice, MoneyFormat);
            Fact("Price − target", ptw.Difference, MoneyFormat);
        }

        row++;
        var periods = r.Periods;
        var head = new List<string> { "CLIN", "Description" };
        head.AddRange(periods.Select(p => p.PeriodName));
        head.Add("Total");
        row = Header(ws, row, head);
        foreach (var clin in r.Cells.GroupBy(c => (c.ClinId, c.ClinNumber, c.ClinDescription)))
        {
            ws.Cell(row, 1).SetValue(clin.Key.ClinNumber);
            ws.Cell(row, 2).SetValue(clin.Key.ClinDescription);
            for (var i = 0; i < periods.Count; i++)
            {
                Set(ws.Cell(row, 3 + i), clin.Where(c => c.PeriodId == periods[i].PeriodId).Sum(c => c.Figures.Price), MoneyFormat);
            }

            Set(ws.Cell(row, 3 + periods.Count), clin.Sum(c => c.Figures.Price), MoneyFormat);
            row++;
        }

        ws.Cell(row, 2).Value = "Total";
        for (var i = 0; i < periods.Count; i++)
        {
            Set(ws.Cell(row, 3 + i), periods[i].Figures.Price, MoneyFormat);
        }

        Set(ws.Cell(row, 3 + periods.Count), r.Totals.Price, MoneyFormat);
        ws.Range(row, 1, row, 3 + periods.Count).Style.Font.Bold = true;
        ws.Range(row, 1, row, 3 + periods.Count).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        row += 2;

        // Cost elements by period.
        head = ["Cost element"];
        head.AddRange(periods.Select(p => p.PeriodName));
        head.Add("Total");
        row = Header(ws, row, head);
        foreach (var (label, pick) in Elements)
        {
            ws.Cell(row, 1).Value = label;
            for (var i = 0; i < periods.Count; i++)
            {
                Set(ws.Cell(row, 2 + i), pick(periods[i].Figures), label == "Hours" ? HoursFormat : MoneyFormat);
            }

            Set(ws.Cell(row, 2 + periods.Count), pick(r.Totals), label == "Hours" ? HoursFormat : MoneyFormat);
            if (label is "Total cost" or "Price")
            {
                ws.Range(row, 1, row, 2 + periods.Count).Style.Font.Bold = true;
            }

            row++;
        }

        if (r.Notes.Count > 0)
        {
            row++;
            ws.Cell(row++, 1).Value = "Notes";
            foreach (var note in r.Notes)
            {
                ws.Cell(row++, 1).SetValue(note);
            }
        }

        Finish(ws);
    }

    private static readonly (string Label, Func<PriceFiguresDto, decimal> Pick)[] Elements =
    [
        ("Hours", f => f.Hours),
        ("Direct labor", f => f.DirectLabor),
        ("Fringe", f => f.Fringe),
        ("Overhead", f => f.Overhead),
        ("Other direct costs", f => f.Odc),
        ("Travel", f => f.Travel),
        ("Materials", f => f.Materials),
        ("Subcontracts", f => f.Subcontracts),
        ("G&A", f => f.GeneralAndAdministrative),
        ("Total cost", f => f.TotalCost),
        ("Fee / profit", f => f.Fee),
        ("Price", f => f.Price),
    ];

    private static void Labor(IXLWorksheet ws, Proposal proposal, PriceResultDto r)
    {
        var row = Title(ws, proposal, r, "Labor build-up");
        row = Header(ws, row, ["WBS", "CLIN", "Labor category", "Name", "Staff", "Rate source", "Base rate", "Escalation", "Period", "Hours", "Direct rate", "Direct labor"]);
        foreach (var l in r.Labor)
        {
            ws.Cell(row, 1).SetValue(l.WbsCode);
            ws.Cell(row, 2).SetValue(l.ClinNumber ?? "");
            ws.Cell(row, 3).SetValue(l.LaborCategoryCode);
            ws.Cell(row, 4).SetValue(l.LaborCategoryName);
            ws.Cell(row, 5).SetValue(l.Staff);
            ws.Cell(row, 6).SetValue(l.RateSource);
            Set(ws.Cell(row, 7), l.BaseRate, RateFormat);
            Set(ws.Cell(row, 8), l.Escalation, PercentFormat);
            ws.Cell(row, 9).SetValue(l.PeriodName);
            Set(ws.Cell(row, 10), l.Hours, HoursFormat);
            Set(ws.Cell(row, 11), l.DirectRate, RateFormat);
            Set(ws.Cell(row, 12), l.DirectLabor, MoneyFormat);
            row++;
        }

        ws.Cell(row, 9).Value = "Total";
        Set(ws.Cell(row, 10), r.Totals.Hours, HoursFormat);
        Set(ws.Cell(row, 12), r.Totals.DirectLabor, MoneyFormat);
        ws.Range(row, 1, row, 12).Style.Font.Bold = true;
        Finish(ws);
    }

    private static void Indirects(IXLWorksheet ws, Proposal proposal, PriceResultDto r)
    {
        var row = Title(ws, proposal, r, "Indirect rate application");
        ws.Cell(row++, 1).SetValue("Cascade: " + string.Join(" → ", r.Cascade.Select(s => $"{s.Pool} on {s.Base.ToLowerInvariant()}")) + ". Unallowable costs are excluded.");
        row++;
        row = Header(ws, row, ["Period", "Fiscal year", "Rates from", "Basis", "Fringe", "Overhead", "G&A", "Labor multiplier", "Override"]);
        foreach (var p in r.PeriodRates)
        {
            ws.Cell(row, 1).SetValue(p.PeriodName);
            ws.Cell(row, 2).Value = p.FiscalYear;
            ws.Cell(row, 3).Value = p.RatesFiscalYear;
            ws.Cell(row, 4).SetValue(BasisLabel(p.Basis));
            Set(ws.Cell(row, 5), p.Fringe, PercentFormat);
            Set(ws.Cell(row, 6), p.Overhead, PercentFormat);
            Set(ws.Cell(row, 7), p.GeneralAndAdministrative, PercentFormat);
            Set(ws.Cell(row, 8), p.LaborMultiplier, "0.0000");
            ws.Cell(row, 9).SetValue(p.Overridden ? "What-if override" : "");
            row++;
        }

        row++;
        row = Header(ws, row, ["CLIN", "Period", "Direct labor", "Fringe", "Overhead", "Direct ODCs", "G&A", "Total cost", "Fee", "Price"]);
        foreach (var c in r.Cells)
        {
            ws.Cell(row, 1).SetValue(c.ClinNumber);
            ws.Cell(row, 2).SetValue(c.PeriodName);
            Set(ws.Cell(row, 3), c.Figures.DirectLabor, MoneyFormat);
            Set(ws.Cell(row, 4), c.Figures.Fringe, MoneyFormat);
            Set(ws.Cell(row, 5), c.Figures.Overhead, MoneyFormat);
            Set(ws.Cell(row, 6), c.Figures.DirectOther, MoneyFormat);
            Set(ws.Cell(row, 7), c.Figures.GeneralAndAdministrative, MoneyFormat);
            Set(ws.Cell(row, 8), c.Figures.TotalCost, MoneyFormat);
            Set(ws.Cell(row, 9), c.Figures.Fee, MoneyFormat);
            Set(ws.Cell(row, 10), c.Figures.Price, MoneyFormat);
            row++;
        }

        ws.Cell(row, 2).Value = "Total";
        var t = r.Totals;
        decimal[] totals = [t.DirectLabor, t.Fringe, t.Overhead, t.DirectOther, t.GeneralAndAdministrative, t.TotalCost, t.Fee, t.Price];
        for (var i = 0; i < totals.Length; i++)
        {
            Set(ws.Cell(row, 3 + i), totals[i], MoneyFormat);
        }

        ws.Range(row, 1, row, 10).Style.Font.Bold = true;
        Finish(ws);
    }

    private static void Basis(IXLWorksheet ws, Proposal proposal, PriceResultDto r)
    {
        var row = Title(ws, proposal, r, "Basis of estimate");
        row = Header(ws, row, ["WBS", "Name", "Hours", "Direct labor", "Direct ODCs", "Total cost", "Price", "Basis of estimate"]);
        foreach (var w in r.Wbs)
        {
            ws.Cell(row, 1).SetValue(w.Code);
            ws.Cell(row, 2).SetValue(w.Name);
            ws.Cell(row, 2).Style.Alignment.Indent = w.Depth;
            Set(ws.Cell(row, 3), w.Figures.Hours, HoursFormat);
            Set(ws.Cell(row, 4), w.Figures.DirectLabor, MoneyFormat);
            Set(ws.Cell(row, 5), w.Figures.DirectOther, MoneyFormat);
            Set(ws.Cell(row, 6), w.Figures.TotalCost, MoneyFormat);
            Set(ws.Cell(row, 7), w.Figures.Price, MoneyFormat);
            ws.Cell(row, 8).SetValue(w.BasisOfEstimate ?? "");
            ws.Cell(row, 8).Style.Alignment.WrapText = true;
            ws.Cell(row, 8).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            row++;
        }

        Finish(ws);
        ws.Column(8).Width = 90;
    }

    private static void Other(IXLWorksheet ws, Proposal proposal, PriceResultDto r)
    {
        var row = Title(ws, proposal, r, "Other direct costs, travel, materials and subcontracts");
        row = Header(ws, row, ["WBS", "CLIN", "Period", "Type", "Description", "Basis", "Amount", "Unallowable"]);
        foreach (var c in r.OtherCosts)
        {
            ws.Cell(row, 1).SetValue(c.WbsCode);
            ws.Cell(row, 2).SetValue(c.ClinNumber ?? "");
            ws.Cell(row, 3).SetValue(c.PeriodName);
            ws.Cell(row, 4).SetValue(c.Kind == PriceCostType.Odc ? "ODC" : c.Kind.ToString());
            ws.Cell(row, 5).SetValue(c.Description);
            ws.Cell(row, 6).SetValue(c.Detail);
            Set(ws.Cell(row, 7), c.Amount, MoneyFormat);
            ws.Cell(row, 8).SetValue(c.IsUnallowable ? "Excluded" : "");
            row++;
        }

        Finish(ws);
        ws.Column(6).Width = Math.Min(ws.Column(6).Width, 80);
    }

    private static void Rates(IXLWorksheet ws, Proposal proposal, PriceResultDto r)
    {
        var tm = r.ContractType is PricingContractKind.TimeAndMaterials or PricingContractKind.LaborHour;
        var row = Title(ws, proposal, r, tm ? "Time-and-materials hourly rates" : "Fully burdened hourly rates (for reference)");
        row = Header(ws, row, ["Labor category", "Name", "Period", "Effective", "Hours", "Direct rate", "Fringe", "Overhead", "G&A", "Fee", "Bill rate"]);
        foreach (var b in r.BillRates)
        {
            ws.Cell(row, 1).SetValue(b.LaborCategoryCode);
            ws.Cell(row, 2).SetValue(b.LaborCategoryName);
            ws.Cell(row, 3).SetValue(b.PeriodName);
            ws.Cell(row, 4).Value = b.PeriodStart.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy-mm-dd";
            Set(ws.Cell(row, 5), b.Hours, HoursFormat);
            Set(ws.Cell(row, 6), b.DirectRate, RateFormat);
            Set(ws.Cell(row, 7), b.Fringe, RateFormat);
            Set(ws.Cell(row, 8), b.Overhead, RateFormat);
            Set(ws.Cell(row, 9), b.GeneralAndAdministrative, RateFormat);
            Set(ws.Cell(row, 10), b.Fee, RateFormat);
            Set(ws.Cell(row, 11), b.BillRate, RateFormat);
            ws.Cell(row, 11).Style.Font.Bold = true;
            row++;
        }

        Finish(ws);
    }

    private static int Title(IXLWorksheet ws, Proposal proposal, PriceResultDto r, string title)
    {
        ws.Cell(1, 1).SetValue($"{proposal.Number} — {proposal.Title}");
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).SetValue(title + (r.IsLocked ? $" · version {r.Version}" : " · not locked"));
        ws.Cell(2, 1).Style.Font.Italic = true;
        return 4;
    }

    private static int Header(IXLWorksheet ws, int row, IReadOnlyList<string> heads)
    {
        for (var i = 0; i < heads.Count; i++)
        {
            ws.Cell(row, i + 1).SetValue(heads[i]);
        }

        var range = ws.Range(row, 1, row, heads.Count);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        return row + 1;
    }

    private static void Set(IXLCell cell, object? value, string? format = null)
    {
        switch (value)
        {
            case null:
                return;
            case decimal d:
                cell.Value = d;
                break;
            case int i:
                cell.Value = i;
                break;
            default:
                cell.SetValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }

        if (format is not null)
        {
            cell.Style.NumberFormat.Format = format;
        }
    }

    private static void Finish(IXLWorksheet ws)
    {
        ws.Columns().AdjustToContents(4, 200);
        foreach (var column in ws.ColumnsUsed())
        {
            column.Width = Math.Min(column.Width, 60);
        }
    }

    internal static string Label(PricingContractKind type) => type switch
    {
        PricingContractKind.FirmFixedPrice => "Firm-fixed-price",
        PricingContractKind.TimeAndMaterials => "Time-and-materials",
        PricingContractKind.LaborHour => "Labor-hour",
        PricingContractKind.CostPlusFixedFee => "Cost-plus-fixed-fee",
        PricingContractKind.CostPlusAwardFee => "Cost-plus-award-fee",
        PricingContractKind.CostPlusIncentiveFee => "Cost-plus-incentive-fee",
        _ => type.ToString(),
    };

    internal static string BasisLabel(Contracts.Billing.RateBasis basis) => basis == Contracts.Billing.RateBasis.ForwardPricing ? "Forward pricing" : basis.ToString();
}
