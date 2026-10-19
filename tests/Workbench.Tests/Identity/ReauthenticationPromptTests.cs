using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Ui.Identity;

namespace Workbench.Tests.Identity;

/// <summary>The layout's "Confirm it's you" prompt, shown when a sensitive action was refused for an old sign-in.</summary>
public sealed class ReauthenticationPromptTests
{
    [Fact]
    public async Task A_refused_action_shows_the_prompt_and_sign_in_again_returns_to_the_page()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var reauthentication = new FakeReauthentication();
        ctx.Services.AddSingleton<IReauthenticationService>(reauthentication);
        var navigation = ctx.Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("payroll?tab=profiles");

        var prompt = ctx.Render<ReauthenticationPrompt>();
        Assert.Empty(prompt.FindAll("[data-testid=reauth-prompt]"));

        await prompt.InvokeAsync(() => reauthentication.Raise(new ReauthenticationRequiredException(SensitiveActions.PayRateReveal, 15)));
        prompt.WaitForElement("[data-testid=reauth-prompt]");
        Assert.Contains("sign in again to show pay rates", prompt.Markup);

        prompt.Find("[data-testid=reauth-sign-in]").Click();

        Assert.EndsWith("account/reauthenticate?returnUrl=%2Fpayroll%3Ftab%3Dprofiles", navigation.Uri);
    }

    private sealed class FakeReauthentication : IReauthenticationService
    {
        public event Action<ReauthenticationRequiredException>? Required;

        public void Raise(ReauthenticationRequiredException exception) => Required?.Invoke(exception);

        public Task<ReauthenticationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReauthenticationStatusDto(null, 15, false, false, null));
    }
}
