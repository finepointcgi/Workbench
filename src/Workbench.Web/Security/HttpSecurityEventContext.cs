using Workbench.Application.Security;

namespace Workbench.Web.Security;

/// <summary>
/// The client's IP address and user agent for security events. In a Blazor circuit HttpContext is only reliable
/// while the circuit starts, so the first values seen are kept for the rest of the scope.
/// </summary>
internal sealed class HttpSecurityEventContext(IHttpContextAccessor http) : ISecurityEventContext
{
    internal const string DenialRecordedKey = "Workbench.DenialRecorded";
    private string? _ip;
    private string? _userAgent;

    public string? IpAddress => _ip ??= http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => _userAgent ??= http.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    public void MarkDenialRecorded()
    {
        if (http.HttpContext is { } context)
        {
            context.Items[DenialRecordedKey] = true;
        }
    }
}
