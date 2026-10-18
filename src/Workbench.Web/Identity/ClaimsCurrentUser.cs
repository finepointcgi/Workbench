using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Workbench.Contracts.Collaboration;

namespace Workbench.Web.Identity;

/// <summary>
/// The principal for code that runs outside an HTTP request pipeline: a Blazor circuit (set by
/// <see cref="PrincipalCircuitHandler"/>) or a SignalR hub call (set by the hub from <c>Context.User</c>).
/// </summary>
public sealed class CurrentPrincipal
{
    public ClaimsPrincipal? Principal { get; set; }
}

/// <summary>
/// <see cref="ICurrentUser"/> for the web host. Inside a Blazor circuit it uses the circuit's authentication
/// state; in API and page requests it uses <c>HttpContext.User</c> (cookie or bearer token). HttpContext isn't
/// reliable inside circuits, which is why the circuit principal wins.
/// </summary>
internal sealed class ClaimsCurrentUser(CurrentPrincipal current, IHttpContextAccessor http) : ICurrentUser
{
    private ClaimsPrincipal? Principal => current.Principal ?? http.HttpContext?.User;

    public string Email => ClaimsUser.EmailOf(Principal) ?? string.Empty;

    public string DisplayName => ClaimsUser.NameOf(Principal) ?? Email;

    public bool IsAuthenticated => Email.Length > 0;
}

/// <summary>Copies the circuit's authentication state into <see cref="CurrentPrincipal"/> and keeps it current.</summary>
internal sealed class PrincipalCircuitHandler(AuthenticationStateProvider provider, CurrentPrincipal current) : CircuitHandler, IDisposable
{
    private bool _subscribed;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        provider.AuthenticationStateChanged += OnChanged;
        _subscribed = true;
        current.Principal = (await provider.GetAuthenticationStateAsync()).User;
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            provider.AuthenticationStateChanged -= OnChanged;
        }
    }

    private async void OnChanged(Task<AuthenticationState> state)
    {
        try
        {
            current.Principal = (await state).User;
        }
        catch (Exception)
        {
            // Keep the previous principal; the next change will try again.
        }
    }
}
