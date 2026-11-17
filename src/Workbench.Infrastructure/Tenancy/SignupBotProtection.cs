using Microsoft.AspNetCore.DataProtection;

namespace Workbench.Infrastructure.Tenancy;

/// <param name="Honeypot">The value of the hidden field real visitors never fill in.</param>
/// <param name="FormToken">What <see cref="ISignupBotProtection.IssueToken"/> gave the form.</param>
/// <param name="Fields">Every other submitted form field, so a vendor check (a CAPTCHA response, for example) can read its own.</param>
public sealed record SignupBotCheck(string? Honeypot, string? FormToken, string? ClientAddress, IReadOnlyDictionary<string, string?> Fields);

/// <summary>
/// The hook where bot protection plugs into the signup form. The default (<see cref="HoneypotBotProtection"/>) needs nothing
/// from a vendor; to add a CAPTCHA, register another implementation that renders its widget in the form and checks its
/// response field in <see cref="VerifyAsync"/>. A refused check looks to the bot like a success.
/// </summary>
public interface ISignupBotProtection
{
    /// <summary>A value embedded in the form when it is shown, checked when it comes back.</summary>
    string IssueToken();

    Task<bool> VerifyAsync(SignupBotCheck check, CancellationToken cancellationToken);
}

/// <summary>A hidden field only bots fill in, plus a signed timestamp so a form posted faster than a person could type, or long after it was shown, is refused.</summary>
public sealed class HoneypotBotProtection(IDataProtectionProvider dataProtection, TimeProvider clock) : ISignupBotProtection
{
    public const string HoneypotField = "website";
    public const string TokenField = "ts";

    public static readonly TimeSpan MinimumFillTime = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(4);

    private readonly IDataProtector _protector = TenantDataProtectionProvider.RootOf(dataProtection).CreateProtector("Workbench.Signup.Form.v1");

    public string IssueToken() => _protector.Protect(clock.GetUtcNow().UtcTicks.ToString());

    public Task<bool> VerifyAsync(SignupBotCheck check, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(check.Honeypot) || string.IsNullOrEmpty(check.FormToken))
        {
            return Task.FromResult(false);
        }

        try
        {
            var shown = new DateTimeOffset(long.Parse(_protector.Unprotect(check.FormToken)), TimeSpan.Zero);
            var age = clock.GetUtcNow() - shown;
            return Task.FromResult(age >= MinimumFillTime && age <= MaximumAge);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or OverflowException)
        {
            return Task.FromResult(false);
        }
    }
}
