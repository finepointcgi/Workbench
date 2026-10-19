using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Billing;

/// <summary>The real registrations: government billing changes are Finance/Admin; pm@ reads C1's financing only.</summary>
public sealed class GovernmentBillingPermissionTests
{
    private static readonly SaveGovernmentBillingSettingsRequest Sf1034Only = new(VoucherFormat.Sf1034, FinancingKind.None);

    [Theory]
    [InlineData(Viewer)]
    [InlineData(Outsider)]
    public async Task Everyone_outside_finance_who_manages_no_contract_is_refused(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var host = seeded.As(email).Host;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IGovernmentBillingService>().GetSettingsAsync(fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IGovernmentBillingService>().GetFinancingAsync(fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IProgressPaymentService>().ListAsync(fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IPerformanceBasedPaymentService>().ListAsync(fx.Contract1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IGovernmentBillingService>().SaveSettingsAsync(fx.Contract1, Sf1034Only));
    }

    [Fact]
    public async Task A_project_manager_reads_their_contracts_financing_but_changes_and_exports_nothing()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var host = seeded.As(Owner).Host;
        var government = host.Get<IGovernmentBillingService>();

        Assert.Equal(FinancingKind.None, (await government.GetSettingsAsync(fx.Contract1)).Financing);
        Assert.Equal(0m, (await government.GetFinancingAsync(fx.Contract1)).Unliquidated);
        Assert.Empty(await host.Get<IProgressPaymentService>().ListAsync(fx.Contract1));
        Assert.Empty(await host.Get<IPerformanceBasedPaymentService>().ListAsync(fx.Contract1));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => government.GetSettingsAsync(fx.Contract2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => government.SaveSettingsAsync(fx.Contract1, Sf1034Only));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => government.GetPieeAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => government.ExportPieeAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IProgressPaymentService>().PreviewAsync(new ProgressPaymentPreviewRequest(fx.Contract1, new DateOnly(2026, 10, 31))));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IPerformanceBasedPaymentService>().CreateAsync(fx.Contract1,
            new SavePerformanceEventRequest("Kickoff", 1m, new DateOnly(2026, 10, 1))));
    }

    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    public async Task Finance_and_admins_change_the_settings(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var government = seeded.As(email).Host.Get<IGovernmentBillingService>();

        var saved = await government.SaveSettingsAsync(fx.Contract2, Sf1034Only with { PayOfficeDodaac = "hq0337" });

        Assert.Equal((VoucherFormat.Sf1034, "HQ0337"), (saved.Format, saved.PayOfficeDodaac));
    }
}
