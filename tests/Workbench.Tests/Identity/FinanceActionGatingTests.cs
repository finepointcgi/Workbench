using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Purchasing;
using Workbench.Ui.Billing;
using Workbench.Ui.Identity;
using Workbench.Ui.Purchasing;

namespace Workbench.Tests.Identity;

/// <summary>
/// Billing and purchasing hide Finance-only actions from everyone else (the server refuses them too), while a project
/// manager keeps what's theirs: reading their contracts' billing and deciding the AP bills routed to them.
/// </summary>
public sealed class FinanceActionGatingTests
{
    private static readonly Guid ContractId = Guid.NewGuid();

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Timekeeper, false)]
    [InlineData(OrgRoles.Finance, true)]
    [InlineData(OrgRoles.Admin, true)]
    public async Task Billing_pages_show_new_invoice_only_to_finance(OrgRoles roles, bool finance)
    {
        await using var ctx = NewContext(roles);

        var invoices = RenderIn<BillingLayout, InvoicesPage>(ctx);
        var contracts = RenderIn<BillingLayout, ContractsBillingPage>(ctx);

        invoices.WaitForAssertion(() => Assert.Equal(finance, invoices.FindAll("[data-testid=new-invoice]").Count == 1));
        contracts.WaitForAssertion(() =>
        {
            Assert.Contains("C-100", contracts.Markup); // a PM still sees the contracts they may read
            Assert.Equal(finance, contracts.FindAll("[data-testid=new-invoice]").Count == 1);
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Finance, true)]
    public async Task The_new_invoice_wizard_is_for_finance(OrgRoles roles, bool finance)
    {
        await using var ctx = NewContext(roles);

        var page = RenderIn<BillingLayout, NewInvoicePage>(ctx);

        page.WaitForAssertion(() =>
        {
            Assert.Equal(finance, page.FindAll("[data-testid=wizard-preview]").Count == 1);
            Assert.Equal(!finance, page.FindAll("[data-testid=load-error]").Count == 1);
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Timekeeper, false)]
    [InlineData(OrgRoles.Finance, true)]
    [InlineData(OrgRoles.Admin, true)]
    public async Task Purchasing_pages_show_new_bill_vendor_and_subcontract_only_to_finance(OrgRoles roles, bool finance)
    {
        await using var ctx = NewContext(roles);

        var bills = RenderIn<PurchasingLayout, BillsPage>(ctx);
        var vendors = RenderIn<PurchasingLayout, VendorsPage>(ctx);
        var subcontracts = RenderIn<PurchasingLayout, SubcontractsPage>(ctx);

        bills.WaitForAssertion(() =>
        {
            Assert.Equal(finance, bills.FindAll("[data-testid=new-bill]").Count == 1);
            Assert.Contains("Approval queue", bills.Markup);
        });
        vendors.WaitForAssertion(() => Assert.Equal(finance, vendors.FindAll("[data-testid=new-vendor]").Count == 1));
        subcontracts.WaitForAssertion(() => Assert.Equal(finance, subcontracts.FindAll("[data-testid=new-subcontract]").Count == 1));
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Finance, true)]
    [InlineData(OrgRoles.Admin, true)]
    public async Task Purchase_orders_show_new_po_only_to_finance(OrgRoles roles, bool finance)
    {
        await using var ctx = NewContext(roles);
        ctx.Services.AddSingleton(Fake<IPurchaseOrderService>.Create());
        ctx.Services.AddSingleton(Fake<IRequisitionService>.Create(("GetOptionsAsync", _ => throw new Workbench.Contracts.WorkbenchForbiddenException())));

        var orders = RenderIn<PurchasingLayout, Workbench.Ui.Purchasing.Procurement.PurchaseOrdersPage>(ctx);

        orders.WaitForAssertion(() =>
        {
            Assert.Contains("Purchase orders", orders.Markup);
            Assert.Equal(finance, orders.FindAll("[data-testid=new-po]").Count == 1);
        });
    }

    [Theory]
    [InlineData(OrgRoles.Employee, false)]
    [InlineData(OrgRoles.Finance, true)]
    public async Task A_project_manager_can_still_decide_a_bill_routed_to_them(OrgRoles roles, bool finance)
    {
        await using var ctx = NewContext(roles);

        var page = RenderIn<PurchasingLayout, BillPage>(ctx, b => b.AddAttribute(1, nameof(BillPage.BillId), Guid.NewGuid()));

        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll("[data-testid=approve]"));
            Assert.Single(page.FindAll("[data-testid=reject]"));
            Assert.Equal(finance, page.FindAll("[data-testid=edit-bill]").Count == 1);
            Assert.Equal(finance, page.FindAll("[data-testid=submit]").Count == 1);
            Assert.Equal(finance, page.FindAll("[data-testid=void]").Count == 1);
            Assert.Contains(finance ? "purchasing/bills" : "purchasing/approvals", page.Find("a").GetAttribute("href"));
        });
    }

    [Fact]
    public async Task Finance_only_content_fails_closed_without_the_cascade()
    {
        await using var ctx = NewContext(OrgRoles.Admin);

        var cut = ctx.Render<FinanceOnly>(p => p
            .AddChildContent("<button data-action>New bill</button>")
            .Add(f => f.NotAllowed, "<p data-note>Finance only</p>"));

        Assert.Empty(cut.FindAll("[data-action]"));
        Assert.Single(cut.FindAll("[data-note]"));
    }

    private static IRenderedComponent<TLayout> RenderIn<TLayout, TPage>(BunitContext ctx, Action<Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder>? attributes = null)
        where TLayout : LayoutComponentBase where TPage : IComponent =>
        ctx.Render<TLayout>(p => p.Add(l => l.Body, (RenderFragment)(b =>
        {
            b.OpenComponent<TPage>(0);
            attributes?.Invoke(b);
            b.CloseComponent();
        })));

    private static BunitContext NewContext(OrgRoles roles)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ICurrentUserContext>(new FakeUserContext(roles));

        var clin = new BillingClinDto(Guid.NewGuid(), "0001", "Engineering", "CPFF", true, 0.07m, 100_000m, 200_000m, 0m);
        var contract = new BillingContractDto(ContractId, "C-100", "Program", Guid.NewGuid(), "Agency", "CPFF",
            new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31), [clin], [InvoiceKind.CostPlus]);
        ctx.Services.AddSingleton(Fake<IInvoiceService>.Create(("ListBillingContractsAsync", _ => (IReadOnlyList<BillingContractDto>)[contract])));

        var bill = new BillDto(
            new BillSummaryDto(Guid.NewGuid(), "B-0001", Guid.NewGuid(), "ACME", "Acme", "INV-1", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
                BillState.PendingApproval, 500m, 0m, 0m, "finance@example.com", null),
            null, [], [], [], null, null, null, null, null, CanEdit: true, CanApprove: true);
        ctx.Services.AddSingleton(Fake<IApBillService>.Create(("GetAsync", _ => bill)));
        ctx.Services.AddSingleton(Fake<IVendorService>.Create());
        ctx.Services.AddSingleton(Fake<IApCreditService>.Create(("GetTrailAsync", _ => throw new Workbench.Contracts.WorkbenchForbiddenException())));
        ctx.Services.AddSingleton(Fake<Workbench.Contracts.Assets.IAssetService>.Create());
        ctx.Services.AddSingleton(Fake<ISubcontractService>.Create());
        ctx.Services.AddSingleton(Fake<Workbench.Contracts.ProjectAccounting.IContractService>.Create());
        ctx.Services.AddSingleton(Fake<Workbench.Contracts.ProjectAccounting.IProjectService>.Create());
        return ctx;
    }

    private sealed class FakeUserContext(OrgRoles roles) : ICurrentUserContext
    {
        public Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentUserInfo("someone@example.com", "Someone", roles, null));
    }
}

/// <summary>A service stub: named methods return the given values, list methods return empty lists, anything else throws.</summary>
public class Fake<T> : DispatchProxy where T : class
{
    private Dictionary<string, Func<object?[]?, object?>> _results = [];

    public static T Create(params (string Method, Func<object?[]?, object?> Result)[] results)
    {
        var proxy = Create<T, Fake<T>>();
        ((Fake<T>)(object)proxy)._results = results.ToDictionary(r => r.Method, r => r.Result);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        var inner = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>) ? type.GetGenericArguments()[0] : null;
        object? value;
        if (_results.TryGetValue(method.Name, out var result))
        {
            value = result(args);
        }
        else if (inner is { IsGenericType: true } && inner.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            value = Array.CreateInstance(inner.GetGenericArguments()[0], 0);
        }
        else
        {
            throw new NotSupportedException($"{typeof(T).Name}.{method.Name} isn't faked.");
        }

        return inner is null ? value : typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
    }
}
