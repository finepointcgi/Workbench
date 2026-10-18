using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Contracts.Evm;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Evm;

/// <summary>
/// IPMDAR-style workbooks. Format 1: cost by WBS (current period and cumulative BCWS/BCWP/ACWP/SV/CV, and BAC/EAC/VAC at
/// completion) with undistributed budget, management reserve and the contract budget base. Format 5: the narrative
/// (summary, variance analysis per flagged control account, and baseline changes approved in the period).
/// </summary>
public sealed class EvmExportService(IWorkbenchDbContextFactory dbFactory, BillingCostSource costs, TimeProvider clock) : IEvmExportService
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string MoneyFormat = "#,##0.00;[Red]-#,##0.00";
    private const string IndexFormat = "0.00";

    public async Task<EvmExportFile> ExportFormat1Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var (p, status, _) = await LoadAsync(projectId, fiscalPeriodId, cancellationToken);
        using var book = new XLWorkbook();
        var ws = book.Worksheets.Add("Format 1 (WBS)");
        var row = Header(ws, "IPMDAR Format 1 — Work Breakdown Structure", status);

        string[] heads =
        [
            "WBS", "Name", "Technique",
            "Period BCWS", "Period BCWP", "Period ACWP", "Period SV", "Period CV",
            "Cum BCWS", "Cum BCWP", "Cum ACWP", "Cum SV", "Cum CV",
            "BAC", "EAC", "VAC", "SPI", "CPI",
        ];
        for (var i = 0; i < heads.Length; i++)
        {
            ws.Cell(row, i + 1).Value = heads[i];
        }

        ws.Range(row, 1, row, heads.Length).Style.Font.Bold = true;
        ws.Range(row, 1, row, heads.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        row++;

        void Line(string code, string name, string? technique, EvmPeriodAmounts? period, EvmMetrics? m, decimal? bacOnly = null, bool bold = false, int indent = 0)
        {
            ws.Cell(row, 1).Value = code;
            ws.Cell(row, 2).Value = name;
            ws.Cell(row, 2).Style.Alignment.Indent = indent;
            if (technique is not null)
            {
                ws.Cell(row, 3).Value = technique;
            }

            if (period is not null)
            {
                Money(ws.Cell(row, 4), period.Bcws);
                Money(ws.Cell(row, 5), period.Bcwp);
                Money(ws.Cell(row, 6), period.Acwp);
                Money(ws.Cell(row, 7), period.Sv);
                Money(ws.Cell(row, 8), period.Cv);
            }

            if (m is not null)
            {
                Money(ws.Cell(row, 9), m.Bcws);
                Money(ws.Cell(row, 10), m.Bcwp);
                Money(ws.Cell(row, 11), m.Acwp);
                Money(ws.Cell(row, 12), m.Sv);
                Money(ws.Cell(row, 13), m.Cv);
                Money(ws.Cell(row, 14), m.Bac);
                Money(ws.Cell(row, 15), m.Eac);
                Money(ws.Cell(row, 16), m.Vac);
                Index(ws.Cell(row, 17), m.Spi);
                Index(ws.Cell(row, 18), m.Cpi);
            }
            else if (bacOnly is { } bac)
            {
                Money(ws.Cell(row, 14), bac);
                Money(ws.Cell(row, 15), bac);
                Money(ws.Cell(row, 16), 0);
            }

            if (bold)
            {
                ws.Range(row, 1, row, heads.Length).Style.Font.Bold = true;
            }

            row++;
        }

        foreach (var ca in status.ControlAccounts)
        {
            Line(ca.Code, ca.Name + (ca.NeedsVar ? "  (VAR)" : ""), "Control account", ca.Period, ca.Cumulative, bold: true);
            foreach (var wp in ca.WorkPackages)
            {
                Line(wp.Code, wp.Name, wp.WorkPackageId is null ? "Not in a work package" : Technique(wp.Technique), wp.Period, wp.Cumulative, indent: 2);
            }
        }

        Line("", "Distributed budget subtotal", null, status.PeriodTotal, status.Total, bold: true);
        Line("", "Undistributed budget", null, null, null, status.UndistributedBudget);
        Line("", "Performance measurement baseline", null, status.PeriodTotal,
            status.Total with
            {
                Bac = status.PerformanceMeasurementBaseline,
                Eac = status.Total.Eac + status.UndistributedBudget,
                Vac = status.PerformanceMeasurementBaseline - (status.Total.Eac + status.UndistributedBudget),
            }, bold: true);
        Line("", "Management reserve", null, null, null, status.ManagementReserve);
        Line("", "Contract budget base", null, null, null, status.ContractBudgetBase, bold: true);

        row++;
        ws.Cell(row++, 1).Value = $"EAC method: control account manager's estimate (defaults to ACWP + (BAC − BCWP) / CPI). {status.CostBasis}";
        foreach (var note in status.Notes)
        {
            ws.Cell(row++, 1).Value = note;
        }

        ws.Columns(1, 3).AdjustToContents();
        ws.Columns(4, heads.Length).Width = 14;
        ws.SheetView.FreezeRows(Header0Rows + 1);
        return Save(book, $"{p.Project.Code}-IPMDAR-Format1-{status.Period.Name}.xlsx");
    }

    public async Task<EvmExportFile> ExportFormat5Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var (p, status, changes) = await LoadAsync(projectId, fiscalPeriodId, cancellationToken);
        var period = p.Calendar.First(c => c.Id == status.Period.FiscalPeriodId);
        var variances = EvmService.Variances(p, period, status);
        using var book = new XLWorkbook();

        var summary = book.Worksheets.Add("Summary");
        var row = Header(summary, "IPMDAR Format 5 — Explanations and problem analyses", status);
        var t = status.Total;
        (string Label, object? Value, string Format)[] facts =
        [
            ("Budget at completion (distributed)", t.Bac, MoneyFormat),
            ("Performance measurement baseline", status.PerformanceMeasurementBaseline, MoneyFormat),
            ("Management reserve", status.ManagementReserve, MoneyFormat),
            ("Cumulative BCWS", t.Bcws, MoneyFormat),
            ("Cumulative BCWP", t.Bcwp, MoneyFormat),
            ("Cumulative ACWP", t.Acwp, MoneyFormat),
            ("Schedule variance", t.Sv, MoneyFormat),
            ("Cost variance", t.Cv, MoneyFormat),
            ("SPI", t.Spi, IndexFormat),
            ("CPI", t.Cpi, IndexFormat),
            ("TCPI (to BAC)", t.Tcpi, IndexFormat),
            ("EAC — CPI", t.EacCpi, MoneyFormat),
            ("EAC — CPI × SPI", t.EacCpiSpi, MoneyFormat),
            ("EAC — managers' estimates", t.EacManager, MoneyFormat),
            ("Estimate to complete", t.Etc, MoneyFormat),
            ("Variance at completion", t.Vac, MoneyFormat),
            ("Control accounts beyond thresholds", variances.Count(v => v.NeedsVar), "0"),
            ("Variance thresholds", $"±{p.Baseline.ThresholdPercent:0.##}% and ±${p.Baseline.ThresholdAmount:N0}", "@"),
        ];
        foreach (var (label, value, format) in facts)
        {
            summary.Cell(row, 1).Value = label;
            switch (value)
            {
                case decimal d:
                    summary.Cell(row, 2).Value = d;
                    summary.Cell(row, 2).Style.NumberFormat.Format = format;
                    break;
                case int i:
                    summary.Cell(row, 2).Value = i;
                    break;
                case string s:
                    summary.Cell(row, 2).Value = s;
                    break;
                default:
                    summary.Cell(row, 2).Value = "n/a";
                    break;
            }

            row++;
        }

        summary.Column(1).Width = 38;
        summary.Column(2).Width = 22;

        var vars = book.Worksheets.Add("Variance analysis");
        string[] heads = ["WBS", "Control account", "SV", "SV %", "CV", "CV %", "BAC", "EAC", "VAC", "Threshold breaches", "Cause", "Impact", "Corrective action", "Manager's EAC"];
        for (var i = 0; i < heads.Length; i++)
        {
            vars.Cell(1, i + 1).Value = heads[i];
        }

        vars.Range(1, 1, 1, heads.Length).Style.Font.Bold = true;
        vars.Range(1, 1, 1, heads.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        row = 2;
        foreach (var v in variances.Where(v => v.NeedsVar || v.Cause is not null || v.ManagerEac is not null))
        {
            vars.Cell(row, 1).Value = v.Code;
            vars.Cell(row, 2).Value = v.Name;
            Money(vars.Cell(row, 3), v.Sv);
            Percent(vars.Cell(row, 4), v.SvPercent);
            Money(vars.Cell(row, 5), v.Cv);
            Percent(vars.Cell(row, 6), v.CvPercent);
            Money(vars.Cell(row, 7), v.Bac);
            Money(vars.Cell(row, 8), v.Eac);
            Money(vars.Cell(row, 9), v.Vac);
            vars.Cell(row, 10).Value = v.NeedsVar ? string.Join("; ", v.Reasons) : "Within thresholds";
            vars.Cell(row, 11).Value = v.Cause ?? (v.NeedsVar ? "(not written)" : "");
            vars.Cell(row, 12).Value = v.Impact ?? "";
            vars.Cell(row, 13).Value = v.CorrectiveAction ?? "";
            if (v.ManagerEac is { } eac)
            {
                Money(vars.Cell(row, 14), eac);
            }

            row++;
        }

        if (row == 2)
        {
            vars.Cell(2, 1).Value = "No control account is beyond the variance thresholds this period.";
        }

        vars.Columns(1, 10).AdjustToContents();
        foreach (var c in new[] { 11, 12, 13 })
        {
            vars.Column(c).Width = 50;
            vars.Column(c).Style.Alignment.WrapText = true;
        }

        var bcrs = book.Worksheets.Add("Baseline changes");
        string[] bcrHeads = ["BCR", "Title", "Justification", "Approved by", "Approved", "Revision", "Changes"];
        for (var i = 0; i < bcrHeads.Length; i++)
        {
            bcrs.Cell(1, i + 1).Value = bcrHeads[i];
        }

        bcrs.Range(1, 1, 1, bcrHeads.Length).Style.Font.Bold = true;
        row = 2;
        var codes = p.Wbs.ToDictionary(w => w.Key, w => w.Value.Code);
        foreach (var c in changes)
        {
            bcrs.Cell(row, 1).Value = $"BCR-{c.Number:000}";
            bcrs.Cell(row, 2).Value = c.Title;
            bcrs.Cell(row, 3).Value = c.Justification;
            bcrs.Cell(row, 4).Value = c.DecidedBy ?? "";
            bcrs.Cell(row, 5).Value = c.DecidedAtUtc?.ToString("yyyy-MM-dd") ?? "";
            bcrs.Cell(row, 6).Value = c.AppliedRevision ?? 0;
            bcrs.Cell(row, 7).Value = string.Join("; ", c.Lines.OrderBy(l => l.Order).Select(l =>
                $"{l.BudgetChange:N2} from {l.Source} to {(l.WbsElementId is { } id ? codes.GetValueOrDefault(id, "?") : "undistributed budget")}"
                + (l.NewTechnique is { } nt ? $", technique {nt}" : "")
                + (l.NewStart is { } s ? $", start {s:yyyy-MM-dd}" : "")
                + (l.NewFinish is { } f ? $", finish {f:yyyy-MM-dd}" : "")));
            row++;
        }

        if (row == 2)
        {
            bcrs.Cell(2, 1).Value = "No baseline changes were approved in this period.";
        }

        bcrs.Columns(1, 7).AdjustToContents();
        bcrs.Column(3).Width = 60;
        return Save(book, $"{p.Project.Code}-IPMDAR-Format5-{status.Period.Name}.xlsx");
    }

    private const int Header0Rows = 6;

    private static int Header(IXLWorksheet ws, string title, EvmStatusDto status)
    {
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = "Project";
        ws.Cell(2, 2).Value = $"{status.ProjectCode} {status.ProjectName}";
        ws.Cell(3, 1).Value = "Contract";
        ws.Cell(3, 2).Value = status.ContractNumber ?? "(none)";
        ws.Cell(4, 1).Value = "Reporting period";
        ws.Cell(4, 2).Value = $"{status.Period.Name} ({status.Period.Start:yyyy-MM-dd} – {status.Period.End:yyyy-MM-dd}){(status.IsFrozen ? ", closed" : ", open (preliminary)")}";
        ws.Cell(5, 1).Value = "PMB revision";
        ws.Cell(5, 2).Value = status.BaselineRevision;
        ws.Range(2, 1, 5, 1).Style.Font.Bold = true;
        return Header0Rows + 1;
    }

    private async Task<(EvmProject P, EvmStatusDto Status, List<EvmChangeRequest> Changes)> LoadAsync(Guid projectId, Guid? fiscalPeriodId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var p = await EvmEngine.LoadAsync(db, projectId, today, ct) ?? throw new ArgumentException("This project has no performance measurement baseline yet.");
        var period = EvmEngine.PeriodOf(p, fiscalPeriodId);
        var (rows, basis) = await EvmEngine.RowsAsync(p, period, costs, db, ct);
        var from = period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = period.End.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var changes = (await db.Set<EvmChangeRequest>().AsNoTracking().Include(c => c.Lines)
                .Where(c => c.ProjectId == projectId && c.Status == EvmChangeRequestStatus.Approved && c.DecidedAtUtc >= from && c.DecidedAtUtc < to)
                .ToListAsync(ct))
            .OrderBy(c => c.Number).ToList();
        return (p, EvmEngine.BuildStatus(p, period, rows, basis), changes);
    }

    private static string Technique(EvTechnique t) => t switch
    {
        EvTechnique.ZeroHundred => "0/100",
        EvTechnique.FiftyFifty => "50/50",
        EvTechnique.PercentComplete => "% complete",
        EvTechnique.Milestones => "Weighted milestones",
        EvTechnique.LevelOfEffort => "Level of effort",
        EvTechnique.Apportioned => "Apportioned",
        _ => t.ToString(),
    };

    private static void Money(IXLCell cell, decimal value)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = MoneyFormat;
    }

    private static void Index(IXLCell cell, decimal? value)
    {
        if (value is { } v)
        {
            cell.Value = v;
            cell.Style.NumberFormat.Format = IndexFormat;
        }
    }

    private static void Percent(IXLCell cell, decimal? value)
    {
        if (value is { } v)
        {
            cell.Value = v / 100m;
            cell.Style.NumberFormat.Format = "0.0%";
        }
    }

    private static EvmExportFile Save(XLWorkbook book, string fileName)
    {
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return new EvmExportFile(fileName, Xlsx, stream.ToArray());
    }
}
