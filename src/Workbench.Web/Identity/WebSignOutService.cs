using Microsoft.AspNetCore.Components;
using Workbench.Contracts.Identity;

namespace Workbench.Web.Identity;

/// <summary>Signing out clears the auth cookie, which only an HTTP response can do, so leave the circuit for the endpoint.</summary>
internal sealed class WebSignOutService(NavigationManager navigation) : ISignOutService
{
    public Task SignOutAsync()
    {
        navigation.NavigateTo(AccountEndpoints.SignOutPath, forceLoad: true);
        return Task.CompletedTask;
    }

    public string? AccountSecurityUrl => PasswordSignIn.SecurityPath;
}
