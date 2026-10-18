using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Workbench.Application.Payroll;

/// <summary>
/// Configuration section <c>Payroll:Paylocity</c>. The API is only used when <see cref="ClientId"/> and
/// <see cref="ClientSecret"/> are set; supply the secret from a vault or environment variable, never appsettings.
/// </summary>
public sealed class PaylocityOptions
{
    /// <summary>Paylocity API host. Production <c>https://api.paylocity.com</c>; sandbox <c>https://apisandbox.paylocity.com</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.paylocity.com";

    /// <summary>OAuth 2.0 token endpoint (client credentials grant).</summary>
    public string TokenUrl { get; set; } = "https://api.paylocity.com/IdentityServer/connect/token";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>OAuth scope granted to Web Link API clients.</summary>
    public string Scope { get; set; } = "WebLinkAPI";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class PaylocityApiException(string message) : Exception(message);

/// <summary>Employee fields Workbench maintains in Paylocity.</summary>
public sealed record PaylocityEmployee(
    string FirstName,
    string LastName,
    string Email,
    DateOnly HireDate,
    string PayType,
    decimal? AnnualSalary,
    decimal? HourlyRate,
    string? CostCenter)
{
    public static PaylocityEmployee From(PayrollEmployeeRecord e) =>
        new(e.FirstName, e.LastName, e.Email, e.HireDate, e.PayType, e.AnnualSalary, e.HourlyRate, e.CostCenter);
}

/// <summary>The part of Paylocity's public REST API (v2) employee sync uses. Faked in tests: no live calls.</summary>
public interface IPaylocityApiClient
{
    /// <summary>Adds an employee; returns Paylocity's employee id.</summary>
    Task<string> CreateEmployeeAsync(string companyId, PaylocityEmployee employee, CancellationToken cancellationToken);

    Task UpdateEmployeeAsync(string companyId, string employeeId, PaylocityEmployee employee, CancellationToken cancellationToken);

    Task TerminateEmployeeAsync(string companyId, string employeeId, DateOnly effectiveDate, CancellationToken cancellationToken);
}

/// <summary>
/// Paylocity API v2 over HTTP with an OAuth client-credentials token (cached until shortly before it expires).
/// Endpoints assumed: <c>POST /api/v2/companies/{companyId}/employees</c> (add; the response carries
/// <c>employeeId</c>), <c>PATCH /api/v2/companies/{companyId}/employees/{employeeId}</c> (update, and terminate with a
/// <c>status</c> of <c>T</c>).
/// </summary>
public sealed class PaylocityApiClient(HttpClient http, IOptions<PaylocityOptions> options, TimeProvider clock) : IPaylocityApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public async Task<string> CreateEmployeeAsync(string companyId, PaylocityEmployee employee, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/v2/companies/{Uri.EscapeDataString(companyId)}/employees", Body(employee, includeStatus: true), cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<CreatedEmployee>(Json, cancellationToken);
        return created?.EmployeeId ?? throw new PaylocityApiException("Paylocity didn't return the new employee's id.");
    }

    public async Task UpdateEmployeeAsync(string companyId, string employeeId, PaylocityEmployee employee, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Patch, EmployeeUrl(companyId, employeeId), Body(employee, includeStatus: false), cancellationToken);
    }

    public async Task TerminateEmployeeAsync(string companyId, string employeeId, DateOnly effectiveDate, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["status"] = new Dictionary<string, object?> { ["employeeStatus"] = "T", ["effectiveDate"] = effectiveDate.ToString("yyyy-MM-dd"), ["changeReason"] = "Terminated in Workbench" },
        };
        using var _ = await SendAsync(HttpMethod.Patch, EmployeeUrl(companyId, employeeId), body, cancellationToken);
    }

    private static string EmployeeUrl(string companyId, string employeeId) =>
        $"api/v2/companies/{Uri.EscapeDataString(companyId)}/employees/{Uri.EscapeDataString(employeeId)}";

    private static Dictionary<string, object?> Body(PaylocityEmployee e, bool includeStatus)
    {
        var body = new Dictionary<string, object?>
        {
            ["firstName"] = e.FirstName,
            ["lastName"] = e.LastName,
            ["workAddress"] = new Dictionary<string, object?> { ["emailAddress"] = e.Email },
            ["departmentPosition"] = e.CostCenter is null ? null : new Dictionary<string, object?> { ["costCenter1"] = e.CostCenter },
            ["primaryPayRate"] = new Dictionary<string, object?>
            {
                ["payType"] = e.PayType == "Salaried" ? "Salary" : "Hourly",
                ["annualSalary"] = e.AnnualSalary,
                ["baseRate"] = e.HourlyRate,
            },
        };
        if (includeStatus)
        {
            body["status"] = new Dictionary<string, object?> { ["employeeStatus"] = "A", ["hireDate"] = e.HireDate.ToString("yyyy-MM-dd") };
        }

        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            throw new PaylocityApiException("The Paylocity API isn't configured.");
        }

        using var request = new HttpRequestMessage(method, new Uri(new Uri(o.BaseUrl.TrimEnd('/') + "/"), path))
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new PaylocityApiException($"Paylocity returned {(int)response.StatusCode}: {(detail.Length > 300 ? detail[..300] : detail)}");
        }

        return response;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpires)
            {
                return _token;
            }

            var o = options.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, o.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = o.Scope }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(o.ClientId!)}:{Uri.EscapeDataString(o.ClientSecret!)}")));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new PaylocityApiException($"Paylocity refused the API credentials ({(int)response.StatusCode}).");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct) ?? throw new PaylocityApiException("Paylocity returned no access token.");
            _token = token.AccessToken;
            _tokenExpires = clock.GetUtcNow().AddSeconds(Math.Max(60, token.ExpiresIn - 60));
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record CreatedEmployee(string? EmployeeId);
}
