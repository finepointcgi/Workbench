using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Pages;

namespace Workbench.Tests;

public sealed class SheetsPageTests
{
    [Fact]
    public async Task Lists_sheets_from_the_service()
    {
        // MudBlazor registers async-only disposables, so dispose the context asynchronously.
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ISheetService>(new FakeSheetService(
        [
            new SheetSummary(Guid.NewGuid(), "Integrated Master Schedule", null, "Program Office", 2, true, "1001.01", DateTime.UtcNow),
        ]));

        var page = ctx.Render<Sheets>();

        page.WaitForAssertion(() => Assert.Contains("Integrated Master Schedule", page.Markup));
        Assert.Contains("1001.01", page.Markup);
    }

    private sealed class FakeSheetService(IReadOnlyList<SheetSummary> sheets) : ISheetService
    {
        public Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(sheets);

        public Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
