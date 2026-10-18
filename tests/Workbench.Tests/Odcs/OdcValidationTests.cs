using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using static Workbench.Tests.Odcs.OdcTestHost;

namespace Workbench.Tests.Odcs;

public sealed class OdcValidationTests
{
    private static async Task<(OdcTestHost Host, ExpenseReportDto Report)> ReportAsync()
    {
        var host = await CreateAsync();
        var report = await host.As(Dev).Expenses.CreateReportAsync(new SaveExpenseReportRequest("Trip"));
        return (host, report);
    }

    [Fact]
    public async Task Expense_line_on_a_parent_wbs_is_rejected()
    {
        var (host, report) = await ReportAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AddLineAsync(report.Id, host.Line(wbs: host.Root.Id)));
        Assert.Contains("not chargeable", ex.Message);
    }

    [Fact]
    public async Task Vendor_odc_on_a_chargeable_parent_is_rejected_as_not_a_leaf()
    {
        var host = await CreateAsync();
        await using (var db = await host.Factory.CreateAsync())
        {
            var root = await db.Set<Workbench.Domain.ProjectAccounting.WbsElement>().FindAsync(host.Root.Id);
            root!.IsChargeable = true;
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(wbs: host.Root.Id)));
        Assert.Contains("has children", ex.Message);
    }

    [Fact]
    public async Task Closed_wbs_is_rejected()
    {
        var host = await CreateAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(wbs: host.ClosedLeaf.Id)));
        Assert.Contains("closed", ex.Message);
    }

    [Fact]
    public async Task Wbs_of_an_inactive_project_is_rejected()
    {
        var host = await CreateAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.As(Analyst).Odcs.CreateVendorOdcAsync(host.OnHoldProject.Id, host.Vendor(wbs: host.OnHoldLeaf.Id)));
        Assert.Contains("not active", ex.Message);
    }

    [Fact]
    public async Task Vendor_odc_wbs_must_belong_to_the_project()
    {
        var host = await CreateAsync();
        await using (var db = await host.Factory.CreateAsync())
        {
            var p = await db.Set<Workbench.Domain.ProjectAccounting.Project>().FindAsync(host.OnHoldProject.Id);
            p!.Status = Workbench.Domain.ProjectAccounting.ProjectStatus.Active;
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(wbs: host.OnHoldLeaf.Id)));
        Assert.Contains("another project", ex.Message);
    }

    [Fact]
    public async Task Expense_wbs_must_be_one_of_the_employees_charge_codes_on_that_date()
    {
        var (host, report) = await ReportAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AddLineAsync(report.Id, host.Line(wbs: host.UnassignedLeaf.Id)));
        Assert.Contains("not one of your charge codes", ex.Message);

        // The same unassigned leaf is fine for a vendor ODC, which isn't tied to an employee.
        await host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor(wbs: host.UnassignedLeaf.Id));
    }

    [Fact]
    public async Task Date_in_a_closed_period_is_rejected()
    {
        var host = await CreateAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.As(Analyst).Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor() with { IncurredOn = ClosedDay }));
        Assert.Contains("is closed", ex.Message);
    }

    [Fact]
    public async Task Date_outside_the_fiscal_calendar_is_rejected()
    {
        var (host, report) = await ReportAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AddLineAsync(report.Id, host.Line(on: new DateOnly(2030, 1, 1))));
        Assert.Contains("No fiscal period", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Amount_must_be_greater_than_zero(decimal amount)
    {
        var (host, report) = await ReportAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AddLineAsync(report.Id, host.Line(amount)));
        Assert.Contains("greater than 0", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Description_is_required(string description)
    {
        var (host, report) = await ReportAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AddLineAsync(report.Id, host.Line(description: description)));
        Assert.Contains("Description", ex.Message);
    }

    [Fact]
    public async Task Vendor_and_invoice_number_are_required_for_vendor_odcs()
    {
        var host = await CreateAsync();
        host.As(Analyst);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor() with { Vendor = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Odcs.CreateVendorOdcAsync(host.Project.Id, host.Vendor() with { InvoiceNumber = "" }));
    }

    [Fact]
    public async Task Expense_lines_are_travel_or_other_only()
    {
        var (host, report) = await ReportAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AddLineAsync(report.Id, host.Line(type: OdcCostType.Equipment)));
        await host.Expenses.AddLineAsync(report.Id, host.Line(type: OdcCostType.Other));
    }

    [Fact]
    public async Task Unallowable_needs_a_reason()
    {
        var (host, report) = await ReportAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.AddLineAsync(report.Id, host.Line(unallowable: true)));
        var line = await host.Expenses.AddLineAsync(report.Id, host.Line(unallowable: true, reason: "Alcohol, FAR 31.205-51"));
        Assert.True(line.IsUnallowable);
        Assert.Equal("Alcohol, FAR 31.205-51", line.UnallowableReason);
    }

    [Fact]
    public async Task Submit_revalidates_lines_after_the_wbs_closes()
    {
        var host = await CreateAsync();
        var report = await host.DevReportAsync(20m);
        await using (var db = await host.Factory.CreateAsync())
        {
            var leaf = await db.Set<Workbench.Domain.ProjectAccounting.WbsElement>().FindAsync(host.Leaf.Id);
            leaf!.IsOpen = false;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.SubmitAsync(report.Id));
    }

    [Fact]
    public async Task Empty_report_cannot_be_submitted()
    {
        var (host, report) = await ReportAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => host.Expenses.SubmitAsync(report.Id));
    }

    [Fact]
    public async Task Another_employee_cannot_change_my_report()
    {
        var (host, report) = await ReportAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.As(Analyst).Expenses.AddLineAsync(report.Id, host.Line()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.As(Analyst).Expenses.GetReportAsync(report.Id));
        Assert.NotNull(await host.As(Pm).Expenses.GetReportAsync(report.Id));
    }

    [Fact]
    public async Task Missing_report_is_not_found()
    {
        var host = await CreateAsync();
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.As(Dev).Expenses.GetReportAsync(Guid.NewGuid()));
    }
}
