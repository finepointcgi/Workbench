using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.IncurredCost;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.IncurredCost;

public sealed class IncurredCostTests
{
    private static Task<IncurredCostPackageDto> CreateAsync(IncurredCostKit kit) => kit.Service.CreateAsync(new CreateIncurredCostRequest(2026));

    [Fact]
    public async Task Schedules_total_to_the_seeded_ledger()
    {
        var kit = await IncurredCostKit.CreateAsync();
        var p = await CreateAsync(kit);

        Assert.Equal(IncurredCostState.Draft, p.Status);
        Assert.Equal(new DateOnly(2025, 10, 1), p.YearStart);
        Assert.Equal(["Cover", "A", "B", "C", "D", "E", "F", "G", "H", "H-1", "I", "J", "K", "L", "M", "N", "O", "U"], p.Schedules.Select(s => s.Code));

        // A: claimed pools and rates in cascade order.
        var a = p.Schedule("A");
        Assert.Equal(3_000m, a.Row("Fringe")!.Number("poolCost"));
        Assert.Equal(10_000m, a.Row("Fringe")!.Number("baseAmount"));
        Assert.Equal(0.30m, a.Row("Fringe")!.Number("claimedRate"));
        Assert.Equal(13_000m, a.Row("Overhead")!.Number("baseAmount"));
        Assert.Equal(0.20m, a.Row("Overhead")!.Number("claimedRate"));
        Assert.Equal(17_600m, a.Row("GeneralAndAdministrative")!.Number("baseAmount"));
        Assert.Equal(0.10m, a.Row("GeneralAndAdministrative")!.Number("claimedRate"));
        Assert.Equal(19_360m, p.Total("A", "claimedCost"));

        // B and C by account.
        Assert.Equal(1_760m, p.Schedule("B").TotalRow!.Number("claimed"));
        Assert.Equal(5_600m, p.Schedule("C").TotalRow!.Number("claimed"));

        // D, E and H on the one contract.
        var key = kit.Rates.Contract.Id.ToString();
        Assert.Equal(10_000m, p.Schedule("D").Row(key)!.Number("labor"));
        Assert.Equal(2_000m, p.Schedule("D").Row(key)!.Number("odc"));
        Assert.Equal(12_000m, p.Total("D", "direct"));
        var e = p.Schedule("E").Row(key)!;
        Assert.Equal(3_000m, e.Number("pool:Fringe"));
        Assert.Equal(2_600m, e.Number("pool:Overhead"));
        Assert.Equal(1_760m, e.Number("pool:GeneralAndAdministrative"));
        Assert.Equal(19_360m, e.Number("claimed"));
        Assert.Equal(2_000m, p.Schedule("H").Row(key)!.Number("account:5120"));
        Assert.Equal(12_000m, p.Total("H", "direct"));

        // I: flexibly priced (CPFF), no prior package.
        Assert.Equal(19_360m, p.Schedule("I").Row(key)!.Number("cumulative"));
        Assert.Contains(p.Checks, c => c.Code == "I-PRIOR");
    }

    [Fact]
    public async Task Unallowable_costs_are_excluded_and_listed_separately()
    {
        var kit = await IncurredCostKit.CreateAsync();

        // A flagged ODC that (wrongly) posts to an allowable direct account, through a misconfigured posting rule, is excluded all the same.
        await using (var db = await kit.Rates.Factory.CreateAsync())
        {
            var rule = await db.Set<Workbench.Domain.Accounting.PostingRule>()
                .SingleAsync(r => r.Kind == Workbench.Domain.Accounting.PostingRuleKind.UnallowableOdc && r.Key == "Other");
            rule.AccountId = kit.Rates.Gl["5190"];
            await db.SaveChangesAsync();
        }

        var odc = await kit.AddFlaggedOdcAsync(300m);
        Assert.Equal(1, (await kit.Rates.Gl.Engine.PostOdcAsync(odc)).Posted);

        var p = await CreateAsync(kit);

        Assert.Equal(12_000m, p.Total("D", "direct"));
        Assert.Equal(19_360m, p.Total("A", "claimedCost"));
        Assert.Equal(1_000m, p.Total("U", "unallowable")); // 500 account + 200 flagged on 7010 + 300 flagged on 5190
        var u = p.Schedule("U");
        Assert.Equal(300m, u.Rows.Single(r => r.Key.StartsWith("flagged:", StringComparison.Ordinal)).Number("amount"));
        Assert.Equal(2, u.Rows.Count(r => r.Key.StartsWith("odc:", StringComparison.Ordinal)));
        Assert.Contains(p.Checks, c => c.Code == "U-FLAGGED" && c.Severity == IceCheckSeverity.Warning);
        Assert.Equal(0m, p.Total("G", "difference"));
    }

    [Fact]
    public async Task Schedule_G_reconciles_to_the_trial_balance_to_the_cent()
    {
        var kit = await IncurredCostKit.CreateAsync();
        await kit.PostAsync("6130", 123.45m, "2010", onProject: false);
        var p = await CreateAsync(kit);

        var september = kit.Rates.Periods["2026-09"];
        var tb = await kit.Rates.Gl.Reports.GetTrialBalanceAsync(september.Id);
        var tbExpense = tb.Rows.Where(r => r.Type == AccountKind.Expense).Sum(r => r.Ending);
        var g = p.Schedule("G");
        Assert.Equal(tbExpense, g.Row("gl")!.Number("amount"));
        Assert.Equal(20_183.45m, g.Row("gl")!.Number("amount"));
        Assert.Equal(0m, g.Row("difference")!.Number("amount"));
        Assert.Contains(p.Checks, c => c.Code == "G-GL" && c.Severity == IceCheckSeverity.Info);
        Assert.DoesNotContain(p.Checks, c => c.Severity == IceCheckSeverity.Error);
    }

    [Fact]
    public async Task Schedule_L_ties_to_the_invoices()
    {
        var kit = await IncurredCostKit.CreateAsync();
        await kit.AddInvoiceAsync(18_000m, 840m);
        await kit.AddInvoiceAsync(999m, 0m, Workbench.Domain.Billing.InvoiceStatus.Draft); // drafts aren't billed
        var p = await CreateAsync(kit);

        var l = p.Schedule("L");
        var row = l.Row(kit.Rates.Contract.Id.ToString())!;
        Assert.Equal(19_360m, row.Number("claimed"));
        Assert.Equal(18_000m, row.Number("billedCost"));
        Assert.Equal(840m, row.Number("billedFee"));
        Assert.Equal(-1_360m, row.Number("difference"));
        Assert.Equal(18_840m, l.Row("register")!.Number("billed"));
        Assert.Equal(0m, l.Row("registerDifference")!.Number("billed"));
        Assert.Contains(p.Checks, c => c.Code == "L-AR" && c.Severity == IceCheckSeverity.Info);
        Assert.Contains(p.Checks, c => c.Code.StartsWith("L-W91", StringComparison.Ordinal) && c.Severity == IceCheckSeverity.Warning);
    }

    [Fact]
    public async Task Inputs_feed_the_schedules_and_are_validated()
    {
        var kit = await IncurredCostKit.CreateAsync();
        var p = await CreateAsync(kit);
        var ga = kit.Rates.Gl["6220"].ToString();

        p = await kit.Service.SaveInputsAsync(p.Id, new SaveIceInputsRequest(
        [
            new IceInputDto("B", ga, "adjustment", "$100.00"),
            new IceInputDto("I", kit.Rates.Contract.Id.ToString(), "prior", "5,000"),
            new IceInputDto("M", kit.Rates.Contract.Id.ToString(), "co", "Jane Smith"),
        ]));

        Assert.Equal(1_660m, p.Total("B", "claimed:GeneralAndAdministrative"));
        Assert.Equal(800m, p.Total("U", "unallowable"));
        Assert.Equal(0m, p.Total("G", "difference"));
        Assert.Equal(24_260m, p.Schedule("I").Row(kit.Rates.Contract.Id.ToString())!.Number("cumulative"));
        Assert.Equal("Jane Smith", p.Schedule("M").Row(kit.Rates.Contract.Id.ToString())!.TextOf("co"));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Service.SaveInputsAsync(p.Id, new SaveIceInputsRequest([new IceInputDto("B", ga, "adjustment", "lots")])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Service.SaveInputsAsync(p.Id, new SaveIceInputsRequest([new IceInputDto("D", "total", "labor", "1")])));

        // Blank clears an input.
        p = await kit.Service.SaveInputsAsync(p.Id, new SaveIceInputsRequest([new IceInputDto("B", ga, "adjustment", " ")]));
        Assert.Equal(1_760m, p.Total("B", "claimed:GeneralAndAdministrative"));
    }

    [Fact]
    public async Task Regenerating_a_draft_shows_the_changed_totals()
    {
        var kit = await IncurredCostKit.CreateAsync();
        var p = await CreateAsync(kit);
        Assert.Empty(p.LastChanges);

        await kit.PostAsync("5010", 1_000m, "2100");
        p = await kit.Service.RegenerateAsync(p.Id);

        Assert.Equal(2, p.Revision);
        var direct = Assert.Single(p.LastChanges, c => c.Schedule == "D" && c.Key == "direct");
        Assert.Equal(12_000m, direct.Before);
        Assert.Equal(13_000m, direct.After);
        Assert.Equal(1_000m, direct.Change);
        Assert.Contains(p.LastChanges, c => c.Key == "rate:Fringe");

        p = await kit.Service.RegenerateAsync(p.Id);
        Assert.Empty(p.LastChanges);
    }

    [Fact]
    public async Task Workflow_certifies_submits_and_locks()
    {
        var kit = await IncurredCostKit.CreateAsync();
        var p = await CreateAsync(kit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.CreateAsync(new CreateIncurredCostRequest(2026)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.CertifyAsync(p.Id, new CertifyIncurredCostRequest("Pat Lee", "CFO", true)));

        p = await kit.Service.MarkReviewedAsync(p.Id);
        Assert.Equal(IncurredCostState.Reviewed, p.Status);
        Assert.Equal("finance@example.com", p.ReviewedBy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.RegenerateAsync(p.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.SaveInputsAsync(p.Id, new SaveIceInputsRequest([])));

        // The certificate names the firm, so the contractor name is required.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Service.CertifyAsync(p.Id, new CertifyIncurredCostRequest("Pat Lee", "CFO", true)));
        p = await kit.Service.ReopenAsync(p.Id, new ReopenIncurredCostRequest("Add the contractor name"));
        Assert.Equal(IncurredCostState.Draft, p.Status);
        Assert.Null(p.ReviewedBy);
        await kit.Service.SaveInputsAsync(p.Id, new SaveIceInputsRequest([new IceInputDto("Cover", "contractor", "value", "Acme Federal, Inc.")]));
        await kit.Service.MarkReviewedAsync(p.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Service.CertifyAsync(p.Id, new CertifyIncurredCostRequest("Pat Lee", "CFO", false)));
        p = await kit.Service.CertifyAsync(p.Id, new CertifyIncurredCostRequest("Pat Lee", "CFO", true));
        Assert.Equal(IncurredCostState.Certified, p.Status);
        Assert.Equal("finance@example.com", p.CertifiedBy);
        Assert.NotNull(p.CertifiedAtUtc);
        Assert.Contains("Firm: Acme Federal, Inc.", p.CertificateText);
        Assert.Equal(p.CertificateText, p.Schedule("N").Row("text")!.TextOf("value"));
        Assert.Equal("Pat Lee", p.Schedule("N").Row("name")!.TextOf("value"));

        p = await kit.Service.SubmitAsync(p.Id, new SubmitIncurredCostRequest("DCAA-2026-001"));
        Assert.Equal(IncurredCostState.Submitted, p.Status);
        Assert.True(p.IsLocked);
        Assert.Equal("DCAA-2026-001", p.SubmissionReference);

        // Locked: the books may change, the package doesn't.
        await kit.PostAsync("5010", 5_000m, "2100");
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.RegenerateAsync(p.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.SaveInputsAsync(p.Id, new SaveIceInputsRequest([])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.ReopenAsync(p.Id, new ReopenIncurredCostRequest("Oops")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.SubmitAsync(p.Id, new SubmitIncurredCostRequest()));
        Assert.Equal(12_000m, (await kit.Service.GetAsync(p.Id)).Total("D", "direct"));

        await using var db = await kit.Rates.Factory.CreateAsync();
        var actions = await db.Set<AuditEvent>().Where(a => a.EntityId == p.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(new[] { "Created", "Reviewed", "Reopened", "InputsSaved", "Reviewed", "Certified", "Submitted" }.Order(StringComparer.Ordinal), actions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Review_is_refused_while_a_check_fails()
    {
        var kit = await IncurredCostKit.CreateAsync();
        await kit.AddInvoiceAsync(1_000m, 0m);
        var p = await CreateAsync(kit);

        // Corrupt the frozen register total so L can't tie.
        await using (var db = await kit.Rates.Factory.CreateAsync())
        {
            var s = await db.Set<Workbench.Domain.IncurredCost.IncurredCostSubmission>().SingleAsync();
            s.SchedulesJson = System.Text.RegularExpressions.Regex.Replace(s.SchedulesJson, "\"invoiceRegisterTotal\":1000(\\.0+)?", "\"invoiceRegisterTotal\":1000.01");
            await db.SaveChangesAsync();
        }

        p = await kit.Service.GetAsync(p.Id);
        var l = Assert.Single(p.Checks, c => c.Code == "L-AR");
        Assert.Equal(IceCheckSeverity.Error, l.Severity);
        Assert.Equal(-0.01m, l.Difference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.MarkReviewedAsync(p.Id));
    }

    [Fact]
    public async Task Export_is_a_workbook_with_a_sheet_per_schedule()
    {
        var kit = await IncurredCostKit.CreateAsync();
        var p = await CreateAsync(kit);
        var file = await kit.Service.ExportWorkbookAsync(p.Id);

        Assert.Equal("incurred-cost-FY2026.xlsx", file.FileName);
        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        Assert.Contains(workbook.Worksheets, w => w.Name == "Sch A");
        Assert.Contains(workbook.Worksheets, w => w.Name == "Sch H-1");
        Assert.Contains(workbook.Worksheets, w => w.Name == "Unallowable");
        Assert.Contains(workbook.Worksheets, w => w.Name == "Checks");
        Assert.Equal(19, workbook.Worksheets.Count);
        var a = workbook.Worksheet("Sch A");
        Assert.StartsWith("Schedule A", a.Cell(2, 1).GetString());
        Assert.Equal("Claimed pool", a.Cell(6, 3).GetString());
        Assert.Equal(3_000m, a.Cell(7, 3).GetValue<decimal>());
    }

    [Fact]
    public async Task A_pool_without_a_base_is_a_warning_not_an_allocation_error()
    {
        var kit = await IncurredCostKit.CreateAsync(ledger: false);
        await kit.PostAsync("5120", 2_000m, "2010");
        await kit.PostAsync("6120", 500m, "2010", onProject: false); // overhead, but no direct labor to spread it over
        await kit.PostAsync("6220", 200m, "2010", onProject: false);
        var p = await CreateAsync(kit);

        Assert.Contains(p.Checks, c => c.Code == "A-BASE-Overhead" && c.Severity == IceCheckSeverity.Warning);
        Assert.DoesNotContain(p.Checks, c => c.Severity == IceCheckSeverity.Error);
        Assert.Equal(200m, p.Total("E", "indirect"));
        Assert.Equal(0m, p.Total("G", "difference"));
    }

    [Fact]
    public async Task Unknown_fiscal_year_is_refused()
    {
        var kit = await IncurredCostKit.CreateAsync(ledger: false);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Service.CreateAsync(new CreateIncurredCostRequest(2031)));
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchNotFoundException>(() => kit.Service.GetAsync(Guid.NewGuid()));
    }
}
