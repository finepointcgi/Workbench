using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.BankFiles;
using Workbench.Tests.Accessibility;
using Workbench.Ui.Purchasing;

namespace Workbench.Tests.BankFiles;

public sealed class VendorBankCardTests
{
    private static readonly Guid VendorId = Guid.NewGuid();

    private static VendorBankChangeDto Change(bool canDecide) => new(Guid.NewGuid(), VendorId, "PRIMA", "Primavera", BankChangeState.Pending, "Primavera", BankAccountType.Checking,
        "••••0015", "••••9999", null, "Called back", "finance@example.com", DateTime.UtcNow, null, null, null, canDecide);

    [Fact]
    public async Task The_card_shows_masked_numbers_and_the_pending_change_with_approve_only_for_someone_else()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var service = new FakeVendorBank(new VendorBankDetailsDto(VendorId, "PRIMA", "Primavera", true, "Primavera", BankAccountType.Checking, "••••0021", "••••4321",
            null, "admin@example.com", DateTime.UtcNow, Change(canDecide: true), []));
        ctx.Services.AddSingleton<IVendorBankService>(service);
        ctx.Render<MudPopoverProvider>();

        var card = ctx.Render<VendorBankCard>(p => p.Add(c => c.VendorId, VendorId));

        card.WaitForAssertion(() => Assert.Contains("••••4321", card.Find("[data-testid=bank-account-masked]").TextContent));
        Assert.Contains("••••9999", card.Find("[data-testid=bank-pending]").TextContent);
        Assert.NotNull(card.Find("[data-testid=bank-approve]"));
        Assert.DoesNotContain("4321", card.Markup.Replace("••••4321", "", StringComparison.Ordinal), StringComparison.Ordinal);
        A11yAssert.Check(card);

        service.Details = service.Details with { PendingChange = Change(canDecide: false) };
        var requester = ctx.Render<VendorBankCard>(p => p.Add(c => c.VendorId, VendorId));
        requester.WaitForAssertion(() => Assert.Contains("Another Finance user approves it", requester.Find("[data-testid=bank-pending]").TextContent));
        Assert.Empty(requester.FindAll("[data-testid=bank-approve]"));
    }

    private sealed class FakeVendorBank(VendorBankDetailsDto details) : IVendorBankService
    {
        public VendorBankDetailsDto Details { get; set; } = details;

        public Task<VendorBankDetailsDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default) => Task.FromResult(Details);

        public Task<IReadOnlyList<VendorBankChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<VendorBankChangeDto> RequestChangeAsync(Guid vendorId, RequestVendorBankChangeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<VendorBankChangeDto> ApproveChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<VendorBankChangeDto> RejectChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<VendorBankChangeDto> CancelChangeAsync(Guid changeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<VendorBankRevealDto> RevealAsync(Guid vendorId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
