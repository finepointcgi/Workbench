using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Time;
using Workbench.Ui.Help;
using Workbench.Ui.Pages.Odcs;
using Workbench.Ui.Purchasing;
using Workbench.Ui.Purchasing.Procurement;
using Workbench.Ui.Resourcing;
using Workbench.Ui.Time;

namespace Workbench.Tests.Identity;

/// <summary>
/// Plain language for employees: glossary tooltips, names before codes, the timesheet's totals and today column, and
/// pages trimmed to what the signed-in person's role uses (cosmetic; the server enforces access).
/// </summary>
public sealed class PlainLanguageTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public async Task Term_shows_its_text_with_the_glossary_explanation()
    {
        await using var ctx = NewContext(OrgRoles.Employee);

        var term = ctx.Render<Term>(p => p.Add(t => t.Key, "eac").AddChildContent("EAC"));
        var fallback = ctx.Render<Term>(p => p.Add(t => t.Key, "WBS"));
        var unknown = ctx.Render<Term>(p => p.Add(t => t.Key, "no-such-term").AddChildContent("Plain"));

        var span = term.Find("[data-term=eac]");
        Assert.Equal("EAC", span.TextContent);
        Assert.Equal("0", span.GetAttribute("tabindex"));
        Assert.Equal("WBS", fallback.Find("[data-term=wbs]").TextContent);
        Assert.Empty(unknown.FindAll("[data-term]"));
        Assert.Contains("Plain", unknown.Markup);
        Assert.StartsWith("Estimate at completion:", Glossary.Find("EAC")!.Tooltip);
    }

    [Theory]
    [InlineData("odc")]
    [InlineData("wbs")]
    [InlineData("charge-code")]
    [InlineData("burdened")]
    [InlineData("fringe")]
    [InlineData("overhead")]
    [InlineData("ga")]
    [InlineData("etc")]
    [InlineData("eac")]
    [InlineData("bac")]
    [InlineData("cpi")]
    [InlineData("spi")]
    [InlineData("clin")]
    [InlineData("ceiling")]
    [InlineData("funded")]
    [InlineData("unallowable")]
    [InlineData("provisional-rate")]
    [InlineData("capitalize")]
    [InlineData("three-way-match")]
    [InlineData("retainage")]
    public void Glossary_explains_the_common_terms(string key)
    {
        var entry = Glossary.Find(key);
        Assert.NotNull(entry);
        Assert.False(string.IsNullOrWhiteSpace(entry.Explanation));
    }

    [Fact]
    public void Charge_codes_read_name_first_and_leave_by_name_alone()
    {
        Assert.Equal("Requirements review (1001.01.02)", ChargeCodeText.Label("1001.01.02", "Requirements review", ChargeCodeKind.Direct));
        Assert.Equal("Holiday", ChargeCodeText.Label("LV.HOL", "Holiday", ChargeCodeKind.Leave));
        Assert.Equal("Overhead (IND.OH)", ChargeCodeText.Label("IND.OH", "Overhead", ChargeCodeKind.Indirect));
        Assert.Equal("1001.01", ChargeCodeText.Label("1001.01", ""));
        Assert.Equal("Leave", ChargeCodeText.Context(ChargeCodeKind.Leave, "IND"));
    }

    [Fact]
    public async Task Timesheet_highlights_today_dims_weekends_and_totals_against_the_standard()
    {
        await using var ctx = NewContext(OrgRoles.Employee);
        var monday = new DateOnly(2026, 10, 19);

        var grid = ctx.Render<TimesheetGrid>(p => p.Add(g => g.Detail, Timesheet(monday)).Add(g => g.Today, monday.AddDays(2)));

        Assert.Contains("today", grid.Find("[data-testid='day-2026-10-21']").ClassList);
        Assert.Equal("date", grid.Find("[data-testid='day-2026-10-21']").GetAttribute("aria-current"));
        Assert.DoesNotContain("today", grid.Find("[data-testid='day-2026-10-20']").ClassList);
        Assert.Contains("nonwork", grid.Find("[data-testid='day-2026-10-24']").ClassList);
        Assert.Contains("nonwork", grid.Find("[data-testid='day-2026-10-25']").ClassList);
        Assert.DoesNotContain("nonwork", grid.Find("[data-testid='day-2026-10-19']").ClassList);

        Assert.Equal("Requirements review (1001.01.02)", grid.Find("[data-testid='row-1001.01.02'] .name").TextContent.Trim());
        Assert.Equal("Holiday", grid.Find("[data-testid='row-LV.HOL'] .name").TextContent.Trim());
        Assert.Equal("16", grid.Find("[data-testid='row-total-1001.01.02']").TextContent.Trim());
        Assert.Equal("16 of 40 h", grid.Find("[data-testid='week-total']").TextContent.Trim());
        Assert.Contains("24 h to go", grid.Find("[data-testid='week-summary']").TextContent);

        grid.Find("[data-testid='cell-1001.01.02-2026-10-21']").Change("8");

        Assert.Equal("24", grid.Find("[data-testid='row-total-1001.01.02']").TextContent.Trim());
        Assert.Equal("24 of 40 h", grid.Find("[data-testid='week-total']").TextContent.Trim());
    }

    [Fact]
    public async Task Timesheet_status_says_who_it_is_waiting_on()
    {
        await using var ctx = NewContext(OrgRoles.Employee);
        var sheet = Timesheet(new DateOnly(2026, 10, 19), TimesheetState.Submitted).Timesheet;

        var banner = ctx.Render<TimesheetStatusBanner>(p => p.Add(b => b.Timesheet, sheet).Add(b => b.WaitingOn, "Pat Morgan"));
        var draft = ctx.Render<TimesheetStatusBanner>(p => p.Add(b => b.Timesheet, sheet with { Status = TimesheetState.Draft, SubmittedAtUtc = null }));

        Assert.Equal("Submitted, waiting on Pat Morgan", banner.Find("[data-testid=status-text]").TextContent);
        Assert.Equal("Draft", draft.Find("[data-testid=status-text]").TextContent);
    }

    [Theory]
    [InlineData(false, "My project costs")]
    [InlineData(true, "Project costs (ODCs)")]
    public async Task Project_costs_offer_vendor_invoices_only_to_those_who_manage_them(bool canManage, string title)
    {
        await using var ctx = NewContext(canManage ? OrgRoles.Finance : OrgRoles.Employee);
        var project = new ProjectDto(ProjectId, "1001", "Modernization", Guid.NewGuid(), "C-1", null, ProjectState.Active, null, null, null);
        ctx.Services.AddSingleton(Fake<IProjectService>.Create(("ListProjectsAsync", _ => (IReadOnlyList<ProjectDto>)[project])));
        ctx.Services.AddSingleton(Fake<IOdcService>.Create(
            ("GetPolicyAsync", _ => new ExpensePolicyDto(75m)),
            ("ListProjectOdcsAsync", _ => new OdcListDto([], [], 0m, 0m, 0m, 0m))));
        ctx.Services.AddSingleton(Fake<IApBillService>.Create());
        ctx.Services.AddSingleton(Fake<IFinancialAccessService>.Create(("CanReadProjectFinancialsAsync", _ => canManage)));

        var page = ctx.Render<ProjectOdcs>(p => p.Add(o => o.ProjectId, ProjectId));

        page.WaitForAssertion(() =>
        {
            Assert.Equal(title, page.Find("[data-testid=odc-title]").TextContent.Trim());
            Assert.Equal(canManage, page.FindAll("[data-testid=new-vendor-odc]").Count == 1);
            Assert.Equal(!canManage, page.FindAll("[data-testid=go-expenses]").Count == 1);
            Assert.Contains(canManage ? "No vendor costs on this project yet" : "Start an expense report", page.Find("[data-testid=odc-empty]").ParentElement!.TextContent);
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Finance, true)]
    public async Task Receiving_shows_dollar_columns_only_to_finance(OrgRoles roles, bool finance)
    {
        await using var ctx = NewContext(roles);
        var order = new PurchaseOrderSummaryDto(Guid.NewGuid(), "PO-0001", Guid.NewGuid(), "ACME", "Acme", new DateOnly(2026, 10, 1),
            PurchaseOrderState.Issued, 0, 12_345m, 0m, 0m, 12_345m);
        ctx.Services.AddSingleton(Fake<IPurchaseOrderService>.Create(("ListReceivableAsync", _ => (IReadOnlyList<PurchaseOrderSummaryDto>)[order])));

        var page = RenderIn<PurchasingLayout, ReceivingPage>(ctx);

        page.WaitForAssertion(() =>
        {
            Assert.Contains("PO-0001", page.Markup);
            Assert.Equal(finance, page.Markup.Contains("12,345"));
            Assert.Equal(finance, page.Find("[data-testid=orders] thead").TextContent.Contains("Committed"));
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Timekeeper, false)]
    [InlineData(OrgRoles.ResourceManager, true)]
    [InlineData(OrgRoles.Finance, true)]
    public async Task Resourcing_shows_staffing_tabs_only_to_staffing_roles(OrgRoles roles, bool staffing)
    {
        await using var ctx = NewContext(roles);
        ctx.Services.AddSingleton(Fake<IPeopleService>.Create());
        ctx.Services.AddSingleton(Fake<IProjectService>.Create());
        ctx.Services.AddSingleton(Fake<IResourceAllocationService>.Create());
        ctx.Services.AddSingleton(Fake<IResourcePlanningService>.Create(("GetHeatMapAsync", _ => throw new InvalidOperationException("not faked"))));

        var page = ctx.Render<ResourcingPage>();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(staffing, page.FindAll("[data-testid=resourcing-tabs]").Count == 1);
            Assert.Equal(staffing, page.Markup.Contains("Project staffing"));
            Assert.Contains("My allocations", page.Markup);
        });
        if (!staffing)
        {
            Assert.Single(page.FindAll("[data-testid=allocations-empty]"));
        }
    }

    [Fact]
    public async Task A_project_manager_sees_the_staffing_tabs()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var me = Guid.NewGuid();
        ctx.Services.AddSingleton<ICurrentUserContext>(new FakeUserContext(OrgRoles.Employee, me));
        var project = new ProjectDto(ProjectId, "1001", "Modernization", null, null, me, ProjectState.Active, null, null, null);
        ctx.Services.AddSingleton(Fake<IPeopleService>.Create());
        ctx.Services.AddSingleton(Fake<IProjectService>.Create(("ListProjectsAsync", _ => (IReadOnlyList<ProjectDto>)[project])));
        ctx.Services.AddSingleton(Fake<IResourceAllocationService>.Create(("CanManageAsync", _ => true)));
        ctx.Services.AddSingleton(Fake<IResourcePlanningService>.Create(("GetHeatMapAsync", _ => throw new InvalidOperationException("not faked"))));

        var page = ctx.Render<ResourcingPage>();

        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid=resourcing-tabs]")));
    }

    private static TimesheetDetailDto Timesheet(DateOnly monday, TimesheetState status = TimesheetState.Draft)
    {
        var lcat = Guid.NewGuid();
        var direct = Guid.NewGuid();
        var holiday = Guid.NewGuid();
        var now = new DateTime(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc);
        var sheet = new TimesheetDto(Guid.NewGuid(), Guid.NewGuid(), "Alex Rivera", "analyst@example.com", monday, monday.AddDays(6), status, 0, 16m, 40m,
            null, status == TimesheetState.Submitted ? now : null, null, null, null, null, null, now);
        var days = Enumerable.Range(0, 7).Select(i => new TimesheetDayDto(monday.AddDays(i), i > 2, true, true, null, false)).ToList();
        var all = days.Select(d => d.Date).ToList();
        return new TimesheetDetailDto(
            sheet,
            [
                new TimeEntryDto(Guid.NewGuid(), direct, "1001.01.02", "Requirements review", "1001", lcat, "BA2", monday, 8m, null, now, now),
                new TimeEntryDto(Guid.NewGuid(), direct, "1001.01.02", "Requirements review", "1001", lcat, "BA2", monday.AddDays(1), 8m, null, now, now),
            ],
            [],
            days,
            [
                new TimesheetChargeCodeDto(new ChargeCodeDto(direct, "1001.01.02", "Requirements review", ProjectId, "1001", "Modernization", ChargeCodeKind.Direct, lcat), all, true),
                new TimesheetChargeCodeDto(new ChargeCodeDto(holiday, "LV.HOL", "Holiday", Guid.NewGuid(), "IND", "Indirect and leave", ChargeCodeKind.Leave, lcat), all, true),
            ],
            []);
    }

    private static IRenderedComponent<TLayout> RenderIn<TLayout, TPage>(BunitContext ctx)
        where TLayout : LayoutComponentBase where TPage : IComponent =>
        ctx.Render<TLayout>(p => p.Add(l => l.Body, (RenderFragment)(b =>
        {
            b.OpenComponent<TPage>(0);
            b.CloseComponent();
        })));

    private static BunitContext NewContext(OrgRoles roles)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ICurrentUserContext>(new FakeUserContext(roles, null));
        return ctx;
    }

    private sealed class FakeUserContext(OrgRoles roles, Guid? employeeId) : ICurrentUserContext
    {
        public Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentUserInfo("someone@example.com", "Someone", roles, employeeId));
    }
}
