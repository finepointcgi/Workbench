using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Materials;
using Workbench.Tests.Accessibility;
using Workbench.Ui.Mobile;
using static Workbench.Tests.Mobile.MobileTestData;

namespace Workbench.Tests.Mobile;

/// <summary>Physical inventory counts on the phone: blind, by tag, and a recount is asked for when the count is off.</summary>
public sealed class CountPageTests
{
    [Fact]
    public async Task A_count_is_entered_by_tag_and_the_counter_is_told_to_recount_without_seeing_the_quantity_on_record()
    {
        await using var ctx = Context();
        var (service, fake) = Fake<IPhysicalInventoryService>.Create();
        ctx.Services.AddSingleton(service);
        var pi = new PhysicalInventorySummaryDto(Guid.NewGuid(), "PI-000001", Guid.NewGuid(), "MAIN", "1 bin", PhysicalInventoryState.Counting, new DateOnly(2026, 10, 19), 2, 0, 0, 0,
            DateTime.UtcNow, "mia@example.com");
        fake.Returns(nameof(IPhysicalInventoryService.ListAsync), new List<PhysicalInventorySummaryDto> { pi })
            .On(nameof(IPhysicalInventoryService.EnterCountAsync), a => new CountResultDto(((EnterCountRequest)a[1]!).TagNumber, CountTagState.RecountNeeded,
                $"Tag {((EnterCountRequest)a[1]!).TagNumber} needs a recount. Count it again and enter the new count."));

        var page = ctx.Render<CountPage>();
        page.Find("[data-testid='count-tag']").Change("1");
        page.Find("[data-testid='count-qty']").Change("9");
        await page.Find("[data-testid='count-save']").ClickAsync(new());

        var args = fake.Single(nameof(IPhysicalInventoryService.EnterCountAsync));
        Assert.Equal((pi.Id, 1, 9m), ((Guid)args[0]!, ((EnterCountRequest)args[1]!).TagNumber, ((EnterCountRequest)args[1]!).Quantity));
        Assert.Contains("needs a recount", page.Find("[data-testid='count-result']").TextContent);
        Assert.DoesNotContain("On record", page.Markup);
        A11yAssert.Check(page);
    }
}
