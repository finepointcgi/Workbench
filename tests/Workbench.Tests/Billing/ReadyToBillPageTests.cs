using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Tests.Identity;
using Workbench.Ui.Billing;

namespace Workbench.Tests.Billing;

public sealed class ReadyToBillPageTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static ReadyToBillDto Data()
    {
        var clean = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        return new ReadyToBillDto(Today,
        [
            new(clean, "W1", "Support", Guid.NewGuid(), "Army", 4_000m, new DateOnly(2026, 10, 1), "Time, expenses", [],
                [new ReadyToBillDraftDto(InvoiceKind.TimeAndMaterials, new DateOnly(2026, 10, 1), Today, 4_000m)]),
            new(blocked, "N2", "Fleet", Guid.NewGuid(), "Navy", 900m, new DateOnly(2026, 10, 5), "Time",
                [new ReadyToBillBlockerDto("Navy has no billing address yet.", $"billing/contracts/{blocked}", "Add the address")],
                [new ReadyToBillDraftDto(InvoiceKind.TimeAndMaterials, new DateOnly(2026, 10, 1), Today, 900m)]),
        ]);
    }

    [Fact]
    public void Each_contract_has_one_button_and_blockers_are_said_in_words()
    {
        var requests = new List<GenerateInvoiceRequest>();
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Fake<IInvoiceService>.Create(
            ("GetReadyToBillAsync", _ => Data()),
            ("GenerateDraftAsync", a => { requests.Add((GenerateInvoiceRequest)a![0]!); throw new InvalidOperationException("stop here"); })));

        var page = ctx.Render<ReadyToBillPage>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid=ready-row]").Count));
        Assert.Contains("$4,900.00 ready to bill", page.Find("[data-testid=ready-total]").TextContent);
        Assert.Contains("1 of them ready to draft now", page.Find("[data-testid=ready-total]").TextContent);
        Assert.Equal(["W1", "N2"], page.FindAll("[data-testid=ready-row]").Select(r => r.GetAttribute("data-contract")));
        Assert.Equal("Navy has no billing address yet.", page.Find("[data-testid=ready-blocker]").TextContent);
        Assert.Contains("billing/contracts/", page.Find("[data-testid=fix-first]").GetAttribute("href"));

        page.Find("[data-testid=draft-invoice]").Click();

        page.WaitForAssertion(() => Assert.Equal(new GenerateInvoiceRequest(requests[0].ContractId, InvoiceKind.TimeAndMaterials, new DateOnly(2026, 10, 1), Today), Assert.Single(requests)));
    }

    [Fact]
    public void An_empty_list_says_nothing_is_waiting()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Fake<IInvoiceService>.Create(("GetReadyToBillAsync", _ => new ReadyToBillDto(Today, []))));

        var page = ctx.Render<ReadyToBillPage>();

        page.WaitForAssertion(() => Assert.Contains("Nothing is waiting", page.Find("[data-testid=ready-empty]").TextContent));
    }

    [Fact]
    public void Finance_gets_one_to_do_for_all_contracts_and_nothing_when_nothing_is_ready()
    {
        var context = new MyTodoContext(new CurrentUserInfo("f@example.com", "Fin", OrgRoles.Finance, Guid.NewGuid()), Today, new ServiceCollection().BuildServiceProvider(), null!);

        var item = ReadyToBillTodoProvider.For(Data(), context)!;

        Assert.Equal("2 contracts have work ready to bill", item.Title);
        Assert.Equal("billing/ready", item.Link);
        Assert.Contains("$4,900", item.Reason);
        Assert.Null(ReadyToBillTodoProvider.For(new ReadyToBillDto(Today, []), context));
    }
}
