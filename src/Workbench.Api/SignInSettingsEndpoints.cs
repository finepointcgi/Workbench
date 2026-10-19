using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;

namespace Workbench.Api;

/// <summary>Sign-in settings (SSO, MFA, provisioning, sessions), re-authentication status and compliance checklists.</summary>
internal static class SignInSettingsEndpoints
{
    public static void MapSignInSettingsEndpoints(this RouteGroupBuilder v1)
    {
        var settings = v1.MapGroup("/admin/sign-in").WithTags("Sign-in settings");
        settings.MapGet("/", (ISignInSettingsService s, CancellationToken ct) => s.GetAsync(ct));
        settings.MapPut("/provider", (SaveSsoProviderRequest r, ISignInSettingsService s, CancellationToken ct) => s.SaveProviderAsync(r, ct));
        settings.MapPut("/mfa", (SaveMfaPolicyRequest r, ISignInSettingsService s, CancellationToken ct) => s.SaveMfaPolicyAsync(r, ct));
        settings.MapPut("/provisioning", (SaveProvisioningRequest r, ISignInSettingsService s, CancellationToken ct) => s.SaveProvisioningAsync(r, ct));
        settings.MapPut("/sessions", (SaveSessionPolicyRequest r, ISignInSettingsService s, CancellationToken ct) => s.SaveSessionPolicyAsync(r, ct));

        v1.MapGet("/users/me/reauthentication", (IReauthenticationService s, CancellationToken ct) => s.GetStatusAsync(ct)).WithTags("Users");

        var checklists = v1.MapGroup("/admin/compliance").WithTags("Compliance");
        checklists.MapGet("/{key}", (string key, IComplianceChecklistService s, CancellationToken ct) => s.GetAsync(key, ct));
        checklists.MapPut("/{key}/items/{itemKey}", (string key, string itemKey, SaveChecklistAnswerRequest r, IComplianceChecklistService s, CancellationToken ct) =>
            s.SaveAnswerAsync(key, itemKey, r, ct));
    }
}
