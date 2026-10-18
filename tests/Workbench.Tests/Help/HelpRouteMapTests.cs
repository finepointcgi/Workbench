using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.Support;
using Workbench.Ui.Help;

namespace Workbench.Tests.Help;

public sealed class HelpRouteMapTests
{
    [Theory]
    [InlineData("", "getting-around")]
    [InlineData("/", "getting-around")]
    [InlineData("time", "entering-time")]
    [InlineData("time/2026-10-12", "entering-time")]
    [InlineData("time/history", "correcting-time")]
    [InlineData("time/approvals", "approving")]
    [InlineData("TIME/Approvals", "approving")]
    [InlineData("approvals?type=timesheet", "approving")]
    [InlineData("expenses/0190a8f0-0000-7000-8000-000000000001", "expenses-per-diem")]
    [InlineData("leave", "requesting-leave")]
    [InlineData("projects/0190a8f0-0000-7000-8000-000000000001/financials", "project-page")]
    [InlineData("sheets/abc", "sheets-boards-gantt")]
    [InlineData("accounting/close", "month-end-close")]
    [InlineData("accounting/revenue/estimates", "revenue-run")]
    [InlineData("accounting/incurred-cost", "ice-package")]
    [InlineData("billing/invoices/42#lines", "billing-an-invoice")]
    [InlineData("purchasing/payments", "paying-bills")]
    [InlineData("cash/reconcile", "bank-reconciliation")]
    [InlineData("setup", "admin-getting-started")]
    [InlineData("setup/approval-workflows/Timesheet", "approval-workflows")]
    [InlineData("setup/import", "importing-data")]
    [InlineData("admin/users", "users-and-roles")]
    [InlineData("https://acme.workbench.example/billing?status=draft", "billing-an-invoice")]
    [InlineData("payroll", HelpRouteMap.Fallback)]
    [InlineData("no/such/page", HelpRouteMap.Fallback)]
    public void Pages_map_to_their_article(string path, string slug) => Assert.Equal(slug, HelpRouteMap.SlugFor(path));

    [Fact]
    public void Every_mapped_article_exists()
    {
        Assert.NotNull(HelpLibrary.Find(HelpRouteMap.Fallback));
        Assert.All(HelpRouteMap.All, r => Assert.True(HelpLibrary.Find(r.Slug) is not null, $"{r.Route} → {r.Slug} has no article"));
    }

    [Fact]
    public void Pages_without_a_route_say_so()
    {
        Assert.True(HelpRouteMap.HasArticle(""));
        Assert.True(HelpRouteMap.HasArticle("time/2026-10-12"));
        Assert.False(HelpRouteMap.HasArticle("payroll/runs/1"));
    }

    [Fact]
    public async Task Help_panel_shows_the_article_for_the_current_page_and_searches()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IHelpStateService>(new FakeHelpState());
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<HelpPanel> { { p => p.PagePath, "time/2026-10-12?x=1" } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<HelpPanel>("Help", parameters));

        provider.WaitForAssertion(() => Assert.Equal("Enter and submit your time", provider.Find("[data-testid='help-panel-title']").TextContent.Trim()));
        Assert.Contains("help/entering-time", provider.Find("[data-testid='help-panel-open']").GetAttribute("href"));
        Assert.Contains("help", provider.Find("[data-testid='help-panel-center']").GetAttribute("href"));

        provider.Find("[data-testid='help-panel-search']").Input("bank reconciliation");
        provider.WaitForAssertion(() => Assert.Contains(provider.FindAll("[data-testid='help-article-link'] a"), a => a.GetAttribute("href") == "help/bank-reconciliation"));
    }
}
