namespace Workbench.ApiClient.Identity;

/// <summary>Supplies the bearer token for calls to the Workbench server (HTTP and the SignalR hub).</summary>
public interface IAccessTokenProvider
{
    /// <summary>A current access token, signing in when needed, or null when there's no way to get one.</summary>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Forgets the tokens; the next call signs in again.</summary>
    Task SignOutAsync(CancellationToken cancellationToken);
}

/// <summary>No sign-in configured: requests go without a token, which only works against a server that allows that.</summary>
public sealed class NoAccessTokenProvider : IAccessTokenProvider
{
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
