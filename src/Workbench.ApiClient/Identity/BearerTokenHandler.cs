using System.Net.Http.Headers;

namespace Workbench.ApiClient.Identity;

/// <summary>The Workbench server the app talks to. Tokens are only ever sent there.</summary>
public sealed record WorkbenchServer(Uri BaseAddress);

/// <summary>
/// Adds <c>Authorization: Bearer</c> from the <see cref="IAccessTokenProvider"/> to requests for the Workbench
/// server. It's registered on every HTTP client, so requests to any other host are left alone.
/// </summary>
public sealed class BearerTokenHandler(IAccessTokenProvider tokens, WorkbenchServer server) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null
            && request.RequestUri is { } uri
            && Uri.Compare(uri, server.BaseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
            && await tokens.GetAccessTokenAsync(cancellationToken) is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
