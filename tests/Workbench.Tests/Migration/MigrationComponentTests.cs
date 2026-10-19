using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Imports;
using Workbench.Ui.Setup.Import;

namespace Workbench.Tests.Migration;

public sealed class MigrationComponentTests
{
    [Fact]
    public async Task The_checklist_deep_link_opens_the_wizard_at_the_upload_step()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        IReadOnlyList<ImportKindDto> kinds =
        [
            new(ImportSource.Costpoint, ImportKind.OrgUnits, "Organizations", "", [], null),
            new(ImportSource.Costpoint, ImportKind.Accounts, "Chart of accounts", "Accounts.", [new("AccountNumber", "Account", true, "", ["AccountNumber", "ACCT_ID"])], "samples/import/costpoint/accounts.csv"),
        ];
        ctx.Services.AddSingleton(Workbench.Tests.Identity.Fake<IImportService>.Create(("ListKindsAsync", _ => kinds)));

        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("setup/import?source=Costpoint&kind=Accounts");
        var page = ctx.Render<ImportPage>();

        page.WaitForAssertion(() => Assert.Contains("Upload Chart of accounts", page.Markup));
        Assert.NotEmpty(page.FindAll("[data-testid=import-file]"));
    }
}
