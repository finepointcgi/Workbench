using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Application.Identity.Passwords;
using Workbench.Application.Security;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;
using static Workbench.Web.Identity.AccountPages;

namespace Workbench.Web.Identity;

/// <summary>
/// Built-in Workbench accounts in the browser: email and password sign-in (the default when the organization has no
/// single sign-on), the authenticator app step, setting it up, forgot password, accepting an invite and the account
/// security page. Between the password and the second factor the person holds only a short-lived "pending" cookie
/// (<see cref="PendingScheme"/>) that no other endpoint accepts; the auth cookie is issued only after
/// <see cref="SignInGate"/> admits them. Every path is under <c>/account</c>, so the sign-in rate limit applies.
/// </summary>
public static class PasswordSignIn
{
    public const string PendingScheme = "WorkbenchPending";
    public const string MfaPath = "/account/signin/mfa";
    public const string MfaSetupPath = "/account/mfa/setup";
    public const string ForgotPath = "/account/forgot-password";
    public const string ResetPath = "/account/reset-password";
    public const string AcceptInvitePath = "/account/accept-invite";
    public const string SecurityPath = "/account/security";

    private const string StageClaim = "wb_pending";
    private const string ReturnUrlClaim = "wb_return";
    private const string ReauthClaim = "wb_reauth";
    private const string StageMfa = "mfa";
    private const string StageEnroll = "enroll";

    internal static void AddPendingCookie(this AuthenticationBuilder auth, bool development) =>
        auth.AddCookie(PendingScheme, o =>
        {
            o.Cookie.Name = ".Workbench.Pending";
            o.Cookie.Path = "/account";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
        });

    /// <summary>Whether this request can use built-in accounts: on, and not the MultiTenant host admin site (which has no user table).</summary>
    internal static bool Available(HttpContext http) =>
        http.RequestServices.GetRequiredService<PasswordOptions>().Enabled && !http.RequestServices.GetRequiredService<ITenantContext>().IsHost;

    /// <summary>The sign-in page: email and password, plus the organization's SSO and (Development, demo) the user picker.</summary>
    internal static async Task<IResult> SignInPageAsync(HttpContext http, string returnUrl, bool reauth, string? email = null, string? error = null, string? notice = null,
        int statusCode = StatusCodes.Status200OK)
    {
        var services = http.RequestServices;
        var options = services.GetRequiredService<AccountOptions>();
        var policy = await services.GetRequiredService<SignInSettingsStore>().GetAsync(http.RequestAborted);
        var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        email ??= reauth ? ClaimsUser.EmailOf(http.User) : null;

        var body = new System.Text.StringBuilder();
        body.Append(reauth
            ? "<h1>Confirm it's you</h1><p class=\"muted\">A sensitive action needs a recent sign-in. Enter your password again.</p>"
            : "<h1>Sign in</h1>");
        body.Append(Error(error)).Append(Notice(notice));
        body.Append(Form(tokens, AccountEndpoints.SignInPath,
            Field("Email", "email", "email", "username", email, autofocus: email is null)
            + Field("Password", "password", "password", "current-password", autofocus: email is not null)
            + (reauth ? """<input type="hidden" name="reauth" value="1" />""" : "")
            + """<button type="submit">Sign in</button>""", returnUrl));
        body.Append($"""<p><a href="{E(WithReturnUrl(ForgotPath, returnUrl))}">Forgot your password?</a></p>""");

        if (policy.OrganizationIdpReady)
        {
            body.Append("<div class=\"or\">or</div>");
            body.Append($"""<a class="button secondary" href="{E(WithReturnUrl(AccountEndpoints.SignInPath + "?provider=sso", returnUrl))}">Sign in with {E(policy.Settings.DisplayName ?? "your organization")}</a>""");
        }
        else if (options.Oidc)
        {
            body.Append("<div class=\"or\">or</div>");
            body.Append($"""<a class="button secondary" href="{E(WithReturnUrl(AccountEndpoints.SignInPath + "?provider=server", returnUrl))}">Sign in with your organization</a>""");
        }

        if (options.DevSignIn)
        {
            var demo = DemoModeActive(http);
            body.Append("<div class=\"or\">or</div>");
            body.Append($"""<a class="button secondary" href="{E(WithReturnUrl(DevSignIn.Path, returnUrl))}">{(demo ? "Pick a demo user" : "Pick a seeded user (development)")}</a>""");
        }

        return Page(reauth ? "Confirm it's you" : "Sign in", body.ToString(), statusCode);
    }

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(AccountEndpoints.SignInPath, async (HttpContext http, PasswordAccounts passwords, SignInSettingsStore store, SecurityEventLog log,
            [FromForm] string? email, [FromForm] string? password, [FromForm] string? returnUrl, [FromForm] string? reauth) =>
        {
            if (!Available(http))
            {
                return Results.NotFound();
            }

            var target = AccountEndpoints.SafeReturnUrl(returnUrl);
            var address = AccessControl.Normalize(email ?? string.Empty);
            if (await OrganizationIdpRequiredAsync(store, http))
            {
                await log.RecordAsync(SecurityEventTypes.SignInFailure, address.Length == 0 ? null : address, SecurityOutcomes.Failure,
                    "password: this organization only allows sign-in through its own identity provider", actor: address.Length == 0 ? "anonymous" : address);
                return Results.Redirect($"{AccountEndpoints.AccessDeniedPath}?reason={SignInDenials.OrganizationIdp}");
            }

            var check = await passwords.CheckPasswordAsync(address, password, http.RequestAborted);
            return check.Status switch
            {
                PasswordCheckStatus.Ok => await ContinueAsync(http, address, check.MfaEnrolled, target, reauth == "1"),
                PasswordCheckStatus.LockedOut => await SignInPageAsync(http, target, reauth == "1", address, LockedMessage(check.LockedOutUntilUtc), statusCode: StatusCodes.Status400BadRequest),
                _ => await SignInPageAsync(http, target, reauth == "1", address, "That email and password don't match. Check them and try again, or reset your password.",
                    statusCode: StatusCodes.Status400BadRequest),
            };
        }).AllowAnonymous().ExcludeFromDescription();

        // ---------- Second factor at sign-in ----------

        app.MapGet(MfaPath, async (HttpContext http) =>
            await PendingAsync(http, StageMfa) is { } pending ? MfaPage(http, pending) : Results.LocalRedirect(AccountEndpoints.SignInPath))
            .AllowAnonymous().ExcludeFromDescription();

        app.MapPost(MfaPath, async (HttpContext http, PasswordAccounts passwords, [FromForm] string? code) =>
        {
            if (await PendingAsync(http, StageMfa) is not { } pending)
            {
                return Results.LocalRedirect(AccountEndpoints.SignInPath);
            }

            var check = await passwords.CheckSecondFactorAsync(pending.Email, code, http.RequestAborted);
            switch (check.Status)
            {
                case PasswordCheckStatus.Ok:
                    return await FinishAsync(http, pending.Email, mfa: true, pending.ReturnUrl, pending.Reauth);
                case PasswordCheckStatus.LockedOut:
                    await http.SignOutAsync(PendingScheme);
                    return await SignInPageAsync(http, pending.ReturnUrl, pending.Reauth, pending.Email, LockedMessage(check.LockedOutUntilUtc), statusCode: StatusCodes.Status400BadRequest);
                default:
                    return MfaPage(http, pending, "That code didn't work. Codes change every 30 seconds: enter the one your app shows now, or a recovery code.");
            }
        }).AllowAnonymous().ExcludeFromDescription();

        // ---------- Setting up the authenticator app ----------

        app.MapGet(MfaSetupPath, async (HttpContext http, PasswordAccounts passwords) =>
        {
            if (await SetupSubjectAsync(http) is not { } subject)
            {
                return Results.LocalRedirect(AccountEndpoints.SignInPath);
            }

            var enrollment = await passwords.PendingTotpEnrollmentAsync(subject.Email, http.RequestAborted) ?? await passwords.BeginTotpEnrollmentAsync(subject.Email, http.RequestAborted);
            return SetupPage(http, subject, enrollment);
        }).ExcludeFromDescription().AllowAnonymous();

        app.MapPost(MfaSetupPath, async (HttpContext http, PasswordAccounts passwords, [FromForm] string? code, [FromForm] string? password) =>
        {
            if (await SetupSubjectAsync(http) is not { } subject)
            {
                return Results.LocalRedirect(AccountEndpoints.SignInPath);
            }

            var enrollment = await passwords.PendingTotpEnrollmentAsync(subject.Email, http.RequestAborted) ?? await passwords.BeginTotpEnrollmentAsync(subject.Email, http.RequestAborted);
            if (subject.Pending is null)
            {
                // Signed in already: the password guards against a borrowed or hijacked session adding its own app.
                var check = await passwords.CheckPasswordAsync(subject.Email, password, http.RequestAborted);
                if (check.Status != PasswordCheckStatus.Ok)
                {
                    return SetupPage(http, subject, enrollment, check.Status == PasswordCheckStatus.LockedOut ? LockedMessage(check.LockedOutUntilUtc) : "Your password isn't right.");
                }
            }

            if (await passwords.ConfirmTotpEnrollmentAsync(subject.Email, code, http.RequestAborted) is not { } codes)
            {
                return SetupPage(http, subject, enrollment, "That code didn't match. Check the app shows Workbench with your email, and enter its current 6-digit code.");
            }

            if (subject.Pending is { } pending)
            {
                var decision = await SignInAsync(http, pending.Email, mfa: true, pending.Reauth);
                if (!decision.Allowed)
                {
                    return Denied(decision);
                }

                return RecoveryCodesPage(http, codes, "Your authenticator app is set up.", pending.ReturnUrl, "Continue to Workbench");
            }

            return RecoveryCodesPage(http, codes, "Your authenticator app is set up.", SecurityPath, "Back to Password and sign-in");
        }).AllowAnonymous().ExcludeFromDescription();

        // ---------- Forgot password ----------

        app.MapGet(ForgotPath, (HttpContext http, string? returnUrl) => Available(http) ? ForgotPage(http, AccountEndpoints.SafeReturnUrl(returnUrl)) : Results.NotFound())
            .AllowAnonymous().ExcludeFromDescription();

        app.MapPost(ForgotPath, async (HttpContext http, PasswordAccounts passwords, SignInSettingsStore store, [FromForm] string? email, [FromForm] string? returnUrl) =>
        {
            if (!Available(http))
            {
                return Results.NotFound();
            }

            if (!await OrganizationIdpRequiredAsync(store, http))
            {
                await passwords.RequestPasswordResetAsync(email ?? string.Empty, RequestBaseUrl(http.Request), cancellationToken: http.RequestAborted);
            }

            // The same answer whether or not the email has an account.
            var minutes = (int)passwords.Options.ResetLinkLifetime.TotalMinutes;
            return Page("Check your email", $"""
                <h1>Check your email</h1>
                <p>If {E(AccessControl.Normalize(email ?? ""))} has a Workbench account, we've sent it a link to choose a new password. The link works once, for {minutes} minutes.</p>
                <p class="muted">Nothing arrived? Check your spam folder, or ask your Workbench administrator.</p>
                <p><a href="{E(WithReturnUrl(AccountEndpoints.SignInPath, AccountEndpoints.SafeReturnUrl(returnUrl)))}">Back to sign in</a></p>
                """);
        }).AllowAnonymous().ExcludeFromDescription();

        // ---------- Links from email: reset a password, accept an invite ----------

        app.MapGet(ResetPath, async (HttpContext http, PasswordAccounts passwords, string? token) =>
        {
            if (!Available(http))
            {
                return Results.NotFound();
            }

            return await passwords.FindTokenAsync(token, AccountTokenPurpose.PasswordReset, http.RequestAborted) is { } row
                ? ChoosePasswordPage(http, ResetPath, token!, "Choose a new password", $"For {row.Email}.")
                : LinkUsedPage();
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapPost(ResetPath, async (HttpContext http, PasswordAccounts passwords, [FromForm] string? token, [FromForm] string? password, [FromForm] string? confirm) =>
        {
            if (!Available(http))
            {
                return Results.NotFound();
            }

            if (await passwords.FindTokenAsync(token, AccountTokenPurpose.PasswordReset, http.RequestAborted) is not { } row)
            {
                return LinkUsedPage();
            }

            if (NewPasswordProblem(passwords, row.Email, password, confirm) is { } problem)
            {
                return ChoosePasswordPage(http, ResetPath, token!, "Choose a new password", $"For {row.Email}.", problem);
            }

            if (!await passwords.UseTokenAsync(row.Id, http.RequestAborted))
            {
                return LinkUsedPage();
            }

            await passwords.SetPasswordAsync(row.Email, password, "Reset with an emailed link", http.RequestAborted);
            await passwords.EndSessionsAsync(row.Email, row.Email, "Password reset", http.RequestAborted);
            var status = await passwords.GetStatusAsync(row.Email, http.RequestAborted);
            return await ContinueAsync(http, row.Email, status.MfaEnabled, "/", reauth: false);
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapGet(AcceptInvitePath, async (HttpContext http, PasswordAccounts passwords, SignInSettingsStore store, string? token) =>
        {
            if (!Available(http))
            {
                return Results.NotFound();
            }

            if (await InviteForAsync(http, passwords, token) is not { } found)
            {
                return InviteGonePage();
            }

            return await AcceptInvitePageAsync(http, store, token!, found.Invite);
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapPost(AcceptInvitePath, async (HttpContext http, PasswordAccounts passwords, SignInSettingsStore store,
            [FromForm] string? token, [FromForm] string? password, [FromForm] string? confirm) =>
        {
            if (!Available(http))
            {
                return Results.NotFound();
            }

            if (await InviteForAsync(http, passwords, token) is not { } found)
            {
                return InviteGonePage();
            }

            if (await OrganizationIdpRequiredAsync(store, http))
            {
                return await AcceptInvitePageAsync(http, store, token!, found.Invite);
            }

            if (NewPasswordProblem(passwords, found.Invite.Email, password, confirm) is { } problem)
            {
                return await AcceptInvitePageAsync(http, store, token!, found.Invite, problem);
            }

            if (!await passwords.UseTokenAsync(found.Token.Id, http.RequestAborted))
            {
                return InviteGonePage();
            }

            // The invite itself is accepted (and the user created with its roles) by the sign-in gate, once any MFA is done.
            await passwords.SetPasswordAsync(found.Invite.Email, password, "Set from an invitation", http.RequestAborted);
            return await ContinueAsync(http, found.Invite.Email, mfaEnrolled: false, "/", reauth: false);
        }).AllowAnonymous().ExcludeFromDescription();

        // ---------- Account security (signed in) ----------

        // Two parameters: a lambda taking only HttpContext would bind as a RequestDelegate and drop the returned page.
        app.MapGet(SecurityPath, (HttpContext http, CancellationToken _) => SecurityPageAsync(http)).ExcludeFromDescription();

        app.MapPost(SecurityPath + "/password", async (HttpContext http, PasswordAccounts passwords, [FromForm] string? current, [FromForm] string? password, [FromForm] string? confirm) =>
        {
            var email = ClaimsUser.EmailOf(http.User)!;
            if (NewPasswordProblem(passwords, email, password, confirm) is { } problem)
            {
                return await SecurityPageAsync(http, error: problem);
            }

            try
            {
                await passwords.ChangePasswordAsync(email, current, password, http.RequestAborted);
            }
            catch (ArgumentException ex)
            {
                return await SecurityPageAsync(http, error: ex.Message);
            }

            // Other devices are signed out; this one gets a fresh cookie with the same proof (MFA) it had.
            await passwords.EndSessionsAsync(email, email, "Password changed", http.RequestAborted);
            await ReissueAsync(http);
            return await SecurityPageAsync(http, notice: "Your password was changed, and your other sessions were signed out.");
        }).ExcludeFromDescription();

        app.MapPost(SecurityPath + "/recovery-codes", async (HttpContext http, PasswordAccounts passwords, [FromForm] string? current) =>
        {
            var email = ClaimsUser.EmailOf(http.User)!;
            if (await ConfirmPasswordAsync(passwords, email, current, http) is { } wrong)
            {
                return await SecurityPageAsync(http, error: wrong);
            }

            try
            {
                return RecoveryCodesPage(http, await passwords.RegenerateRecoveryCodesAsync(email, http.RequestAborted), "New recovery codes. The old ones no longer work.",
                    SecurityPath, "Back to Password and sign-in");
            }
            catch (ArgumentException ex)
            {
                return await SecurityPageAsync(http, error: ex.Message);
            }
        }).ExcludeFromDescription();

        app.MapPost(SecurityPath + "/mfa/remove", async (HttpContext http, PasswordAccounts passwords, SignInGate gate, [FromForm] string? current) =>
        {
            var email = ClaimsUser.EmailOf(http.User)!;
            if (await gate.RequiresMfaAsync(email, http.RequestAborted))
            {
                return await SecurityPageAsync(http, error: "Your organization requires MFA, so the app can't be removed. Set up a different app instead.");
            }

            if (await ConfirmPasswordAsync(passwords, email, current, http) is { } wrong)
            {
                return await SecurityPageAsync(http, error: wrong);
            }

            await passwords.RemoveTotpAsync(email, email, http.RequestAborted);
            return await SecurityPageAsync(http, notice: "The authenticator app was removed. Sign-in now needs only your password.");
        }).ExcludeFromDescription();

        app.MapPost(SecurityPath + "/password-link", async (HttpContext http, PasswordAccounts passwords) =>
        {
            var email = ClaimsUser.EmailOf(http.User)!;
            await passwords.RequestPasswordResetAsync(email, RequestBaseUrl(http.Request), requestedBy: email, cancellationToken: http.RequestAborted);
            return await SecurityPageAsync(http, notice: $"We emailed {email} a link to choose a password. It works once, for {(int)passwords.Options.ResetLinkLifetime.TotalMinutes} minutes.");
        }).ExcludeFromDescription();

        app.MapPost(SecurityPath + "/sign-out-everywhere", async (HttpContext http, PasswordAccounts passwords) =>
        {
            var email = ClaimsUser.EmailOf(http.User)!;
            await passwords.EndSessionsAsync(email, email, "Signed out everywhere by the user", http.RequestAborted);
            return Results.LocalRedirect(AccountEndpoints.SignOutPath);
        }).ExcludeFromDescription();
    }

    // ---------- Flow ----------

    private sealed record Pending(string Email, string Stage, string ReturnUrl, bool Reauth);

    private sealed record SetupSubject(string Email, Pending? Pending);

    /// <summary>After a correct password (or a new one from a link): the second factor, setting one up, or straight in.</summary>
    private static async Task<IResult> ContinueAsync(HttpContext http, string email, bool mfaEnrolled, string returnUrl, bool reauth)
    {
        if (mfaEnrolled)
        {
            await SetPendingAsync(http, new Pending(email, StageMfa, returnUrl, reauth));
            return Results.LocalRedirect(MfaPath);
        }

        if (await http.RequestServices.GetRequiredService<SignInGate>().RequiresMfaAsync(email, http.RequestAborted))
        {
            await SetPendingAsync(http, new Pending(email, StageEnroll, returnUrl, reauth));
            return Results.LocalRedirect(MfaSetupPath);
        }

        return await FinishAsync(http, email, mfa: false, returnUrl, reauth);
    }

    private static async Task<IResult> FinishAsync(HttpContext http, string email, bool mfa, string returnUrl, bool reauth)
    {
        var decision = await SignInAsync(http, email, mfa, reauth);
        return decision.Allowed ? Results.LocalRedirect(returnUrl) : Denied(decision);
    }

    private static IResult Denied(SignInDecision decision) =>
        Results.Redirect($"{AccountEndpoints.AccessDeniedPath}?reason={Uri.EscapeDataString(decision.Code ?? SignInDenials.General)}");

    /// <summary>The sign-in gate decides (active, invited, MFA policy); then the auth cookie replaces the pending one.</summary>
    private static async Task<SignInDecision> SignInAsync(HttpContext http, string email, bool mfa, bool reauth)
    {
        var services = http.RequestServices;
        var claims = new SignInClaims(mfa ? ["pwd", SignInGate.OtpAmr] : ["pwd"], [], null);
        var decision = await services.GetRequiredService<SignInGate>().EvaluateAsync(email, null, SignInGate.PasswordMethod, interactive: true, http.RequestAborted, claims);
        await http.SignOutAsync(PendingScheme);
        if (!decision.Allowed || decision.User is not { } user)
        {
            return decision with { Allowed = false };
        }

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(user.Email, user.DisplayName, now, mfa));
        if (reauth)
        {
            await services.GetRequiredService<SecurityEventLog>().RecordAsync(SecurityEventTypes.Reauthenticated, user.Email, detail: SignInGate.PasswordMethod, actor: user.Email);
        }

        return decision;
    }

    private static ClaimsPrincipal Principal(string email, string name, DateTimeOffset now, bool mfa) =>
        new(new ClaimsIdentity(
            [new Claim("sub", email), new Claim("email", email), new Claim("name", name), Security.SessionValidation.SignedInAt(now),
                new Claim(OrganizationSso.AuthTimeClaim, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                new Claim(OrganizationSso.MfaClaim, mfa ? "true" : "false"), new Claim(OrganizationSso.MethodClaim, SignInGate.PasswordMethod)],
            SignInGate.PasswordMethod, "name", "role"));

    /// <summary>A fresh auth cookie for the signed-in person (after ending their other sessions), keeping their sign-in time and MFA.</summary>
    private static async Task ReissueAsync(HttpContext http)
    {
        var user = http.User;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var identity = new ClaimsIdentity(user.Claims.Where(c => c.Type != Security.SessionValidation.SignedInAtClaim), user.Identity?.AuthenticationType ?? SignInGate.PasswordMethod, "name", "role");
        identity.AddClaim(Security.SessionValidation.SignedInAt(now));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    private static Task SetPendingAsync(HttpContext http, Pending pending) =>
        http.SignInAsync(PendingScheme, new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", pending.Email), new Claim(StageClaim, pending.Stage), new Claim(ReturnUrlClaim, pending.ReturnUrl), new Claim(ReauthClaim, pending.Reauth ? "1" : "0")],
            PendingScheme)));

    private static async Task<Pending?> PendingAsync(HttpContext http, string stage)
    {
        if (!Available(http))
        {
            return null;
        }

        var result = await http.AuthenticateAsync(PendingScheme);
        var p = result.Principal;
        return result.Succeeded && p?.FindFirst(StageClaim)?.Value == stage && p.FindFirst("email")?.Value is { Length: > 0 } email
            ? new Pending(email, stage, AccountEndpoints.SafeReturnUrl(p.FindFirst(ReturnUrlClaim)?.Value), p.FindFirst(ReauthClaim)?.Value == "1")
            : null;
    }

    /// <summary>Who's setting up an app: someone mid-sign-in whose organization requires MFA, or a signed-in person with a password.</summary>
    private static async Task<SetupSubject?> SetupSubjectAsync(HttpContext http)
    {
        if (await PendingAsync(http, StageEnroll) is { } pending)
        {
            return new SetupSubject(pending.Email, pending);
        }

        if (http.User.Identity?.IsAuthenticated == true && ClaimsUser.EmailOf(http.User) is { } email && Available(http)
            && (await http.RequestServices.GetRequiredService<PasswordAccounts>().GetStatusAsync(email, http.RequestAborted)).HasPassword)
        {
            return new SetupSubject(email, null);
        }

        return null;
    }

    private static async Task<(AccountToken Token, UserInvite Invite)?> InviteForAsync(HttpContext http, PasswordAccounts passwords, string? token)
    {
        if (await passwords.FindTokenAsync(token, AccountTokenPurpose.Invite, http.RequestAborted) is not { InviteId: { } inviteId } row)
        {
            return null;
        }

        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        await using var db = await http.RequestServices.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(http.RequestAborted);
        var invite = await db.Set<UserInvite>().AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now, http.RequestAborted);
        if (invite is null || await db.Set<AppUser>().AnyAsync(u => u.Email == invite.Email, http.RequestAborted))
        {
            return null;
        }

        return (row, invite);
    }

    private static async Task<bool> OrganizationIdpRequiredAsync(SignInSettingsStore store, HttpContext http)
    {
        var policy = await store.GetAsync(http.RequestAborted);
        return policy.OrganizationIdpReady && policy.Settings.RequireOrganizationIdp;
    }

    private static string? NewPasswordProblem(PasswordAccounts passwords, string email, string? password, string? confirm) =>
        PasswordPolicy.Problem(password, email, passwords.Options) ?? (password != confirm ? "The two passwords don't match." : null);

    private static async Task<string?> ConfirmPasswordAsync(PasswordAccounts passwords, string email, string? password, HttpContext http)
    {
        var check = await passwords.CheckPasswordAsync(email, password, http.RequestAborted);
        return check.Status switch
        {
            PasswordCheckStatus.Ok => null,
            PasswordCheckStatus.LockedOut => LockedMessage(check.LockedOutUntilUtc),
            _ => "Your current password isn't right.",
        };
    }

    private static string LockedMessage(DateTime? until) =>
        $"Too many wrong attempts, so sign-in is paused for this account{(until is { } u ? $" until {u:HH:mm} UTC" : "")}. Try again then, or reset your password.";

    internal static string RequestBaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    private static bool DemoModeActive(HttpContext http) =>
        Infrastructure.Persistence.DemoMode.IsActive(http.RequestServices.GetRequiredService<IConfiguration>(), http.RequestServices.GetRequiredService<IHostEnvironment>());

    // ---------- Pages ----------

    private static IResult MfaPage(HttpContext http, Pending pending, string? error = null)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        return Page("Two-step verification", $"""
            <h1>Two-step verification</h1>
            <p class="muted">Enter the 6-digit code from the authenticator app for {E(pending.Email)}.</p>
            {Error(error)}
            {Form(tokens, MfaPath, Field("Code", "code", "text", "one-time-code", autofocus: true, extra: "inputmode=\"numeric\" maxlength=\"12\" spellcheck=\"false\"",
                hint: "Lost your phone? Enter one of your recovery codes instead.") + """<button type="submit">Verify</button>""")}
            <p><a href="{AccountEndpoints.SignOutPath}">Cancel</a></p>
            """, error is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }

    private static IResult SetupPage(HttpContext http, SetupSubject subject, TotpEnrollment enrollment, string? error = null)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(enrollment.ProvisioningUri, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(5, "#000000", "#ffffff", drawQuietZones: true);
        var intro = subject.Pending is not null
            ? "<p>Your organization requires two-step verification. Set up an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password, Okta Verify or similar) to finish signing in.</p>"
            : "<p>Use an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password, Okta Verify or similar). After this, signing in asks for its code as well as your password.</p>";
        var password = subject.Pending is null ? Field("Your password", "password", "password", "current-password") : "";
        return Page("Set up an authenticator app", $"""
            <h1>Set up an authenticator app</h1>
            {intro}
            {Error(error)}
            <p><strong>1.</strong> In the app, add an account and scan this code:</p>
            <div class="qr" role="img" aria-label="QR code for setting up Workbench in an authenticator app">{svg}</div>
            <p class="muted">Can't scan it? Choose "enter a setup key" in the app and type <code>{E(Totp.Grouped(enrollment.Secret))}</code> (time-based).</p>
            <p><strong>2.</strong> Enter the 6-digit code the app now shows for Workbench.</p>
            {Form(tokens, MfaSetupPath, Field("Code", "code", "text", "one-time-code", autofocus: error is null, extra: "inputmode=\"numeric\" maxlength=\"8\" spellcheck=\"false\"")
                + password + """<button type="submit">Turn on two-step verification</button>""")}
            <p><a href="{(subject.Pending is null ? SecurityPath : AccountEndpoints.SignOutPath)}">Cancel</a></p>
            """, error is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }

    private static IResult RecoveryCodesPage(HttpContext http, IReadOnlyList<string> codes, string heading, string next, string nextLabel) =>
        Page("Save your recovery codes", $"""
            <h1>{E(heading)}</h1>
            <p>Save these recovery codes somewhere safe, like a password manager. If you lose your phone, each one signs you in once instead of an app code.
            This is the only time they're shown.</p>
            <ul class="codes" aria-label="Recovery codes">{string.Concat(codes.Select(c => $"<li>{E(c)}</li>"))}</ul>
            <a class="button" href="{E(next)}">{E(nextLabel)}</a>
            """);

    private static IResult ForgotPage(HttpContext http, string returnUrl, string? error = null)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        return Page("Reset your password", $"""
            <h1>Reset your password</h1>
            <p class="muted">Enter your email address and we'll send you a link to choose a new password.</p>
            {Error(error)}
            {Form(tokens, ForgotPath, Field("Email", "email", "email", "username", autofocus: true) + """<button type="submit">Email me a link</button>""", returnUrl)}
            <p><a href="{E(WithReturnUrl(AccountEndpoints.SignInPath, returnUrl))}">Back to sign in</a></p>
            """);
    }

    private static IResult ChoosePasswordPage(HttpContext http, string action, string token, string heading, string intro, string? error = null, string? extra = null)
    {
        var options = http.RequestServices.GetRequiredService<PasswordOptions>();
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        return Page(heading, $"""
            <h1>{E(heading)}</h1>
            <p class="muted">{E(intro)}</p>
            {Error(error)}
            {Form(tokens, action, $"""<input type="hidden" name="token" value="{E(token)}" />"""
                + Field("New password", "password", "password", "new-password", autofocus: true, hint: $"At least {options.MinimumLength} characters. A few unrelated words work well.",
                    extra: $"minlength=\"{options.MinimumLength}\" maxlength=\"{options.MaximumLength}\"")
                + Field("Confirm the password", "confirm", "password", "new-password")
                + """<button type="submit">Save password and sign in</button>""")}
            {extra}
            """, error is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> AcceptInvitePageAsync(HttpContext http, SignInSettingsStore store, string token, UserInvite invite, string? error = null)
    {
        var policy = await store.GetAsync(http.RequestAborted);
        var sso = policy.OrganizationIdpReady
            ? $"""<a class="button secondary" href="{AccountEndpoints.SignInPath}?provider=sso">Sign in with {E(policy.Settings.DisplayName ?? "your organization")}</a>"""
            : null;
        if (policy.OrganizationIdpReady && policy.Settings.RequireOrganizationIdp)
        {
            return Page("Accept your invitation", $"""
                <h1>Welcome, {E(invite.DisplayName)}</h1>
                <p>{E(invite.InvitedBy)} invited you to Workbench. Your organization signs in with its own account, so there's no Workbench password to choose:
                sign in as {E(invite.Email)} to accept the invitation.</p>
                {sso}
                """);
        }

        return ChoosePasswordPage(http, AcceptInvitePath, token, $"Welcome, {invite.DisplayName}",
            $"{invite.InvitedBy} invited you to Workbench. Choose a password for {invite.Email}. The invitation expires {invite.ExpiresAtUtc:MMMM d} at {invite.ExpiresAtUtc:HH:mm} UTC.",
            error, sso is null ? null : $"<div class=\"or\">or</div>{sso}");
    }

    private static IResult LinkUsedPage() => Page("Link expired", $"""
        <h1>This link has expired</h1>
        <p>Password links work once, for a limited time. Ask for a new one.</p>
        <a class="button" href="{ForgotPath}">Reset your password</a>
        <p><a href="{AccountEndpoints.SignInPath}">Back to sign in</a></p>
        """, StatusCodes.Status410Gone);

    private static IResult InviteGonePage() => Page("Invitation unavailable", $"""
        <h1>This invitation can't be used</h1>
        <p>It has expired, was replaced by a newer one, was withdrawn, or has already been accepted. If you already set your password, sign in.
        Otherwise ask your Workbench administrator to send a new invitation.</p>
        <a class="button" href="{AccountEndpoints.SignInPath}">Sign in</a>
        """, StatusCodes.Status410Gone);

    private static async Task<IResult> SecurityPageAsync(HttpContext http, string? error = null, string? notice = null)
    {
        var services = http.RequestServices;
        var email = ClaimsUser.EmailOf(http.User);
        if (email is null)
        {
            return Results.LocalRedirect(AccountEndpoints.SignInPath);
        }

        var passwords = services.GetRequiredService<PasswordAccounts>();
        var status = await passwords.GetStatusAsync(email, http.RequestAborted);
        var policy = await services.GetRequiredService<SignInSettingsStore>().GetAsync(http.RequestAborted);
        var ssoOnly = policy.OrganizationIdpReady && policy.Settings.RequireOrganizationIdp;
        var mfaRequired = await services.GetRequiredService<SignInGate>().RequiresMfaAsync(email, http.RequestAborted);
        var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        var method = http.User.FindFirst(OrganizationSso.MethodClaim)?.Value;
        var body = new System.Text.StringBuilder($"<h1>Password and sign-in</h1><p class=\"muted\">{E(email)}</p>");
        body.Append(Error(error)).Append(Notice(notice));
        if (method is "sso" or "oidc")
        {
            body.Append($"<p>You signed in with {E(policy.Settings.DisplayName ?? "your organization's account")}. Its password and two-step verification are managed there.</p>");
        }

        body.Append("<h2>Password</h2>");
        if (!Available(http) || ssoOnly)
        {
            body.Append("<p>Your organization signs in only with its own account, so Workbench passwords aren't used.</p>");
        }
        else if (status.HasPassword)
        {
            body.Append($"<p class=\"muted\">Last changed {status.PasswordChangedAtUtc:MMMM d, yyyy}.</p>");
            body.Append(Form(tokens, SecurityPath + "/password",
                Field("Current password", "current", "password", "current-password")
                + Field("New password", "password", "password", "new-password", hint: $"At least {passwords.Options.MinimumLength} characters.",
                    extra: $"minlength=\"{passwords.Options.MinimumLength}\" maxlength=\"{passwords.Options.MaximumLength}\"")
                + Field("Confirm the new password", "confirm", "password", "new-password")
                + """<button type="submit">Change password</button>"""));
        }
        else
        {
            body.Append("<p>You don't have a Workbench password. Set one to sign in with your email and password as well.</p>");
            body.Append(Form(tokens, SecurityPath + "/password-link", """<button type="submit" class="secondary">Email me a link to set a password</button>"""));
        }

        if (status.HasPassword && !ssoOnly && Available(http))
        {
            body.Append("<h2>Two-step verification</h2>");
            if (status.MfaEnabled)
            {
                body.Append($"<p>On since {status.MfaEnabledAtUtc:MMMM d, yyyy}, with an authenticator app. {status.RecoveryCodesLeft} recovery code{(status.RecoveryCodesLeft == 1 ? "" : "s")} left.</p>");
                body.Append(Form(tokens, SecurityPath + "/recovery-codes",
                    Field("Your password", "current", "password", "current-password", hint: "Needed for new recovery codes or to remove the app.")
                    + """<button type="submit" class="secondary">Get new recovery codes</button>"""
                    + (mfaRequired ? "" : $"""<button type="submit" class="danger" formaction="{SecurityPath}/mfa/remove">Remove the authenticator app</button>""")));
                body.Append($"""<p><a href="{MfaSetupPath}">Move to a different app or phone</a></p>""");
            }
            else
            {
                body.Append($"<p>{(mfaRequired ? "Your organization requires it: your next sign-in will ask you to set it up." : "Off. Add an authenticator app so a stolen password isn't enough to sign in.")}</p>");
                body.Append($"""<a class="button" href="{MfaSetupPath}">Set up an authenticator app</a>""");
            }
        }

        body.Append("<h2>Sessions</h2><p class=\"muted\">Signed in on a shared or lost device? End every session, including this one.</p>");
        body.Append(Form(tokens, SecurityPath + "/sign-out-everywhere", """<button type="submit" class="secondary">Sign out everywhere</button>"""));
        body.Append($"""<div class="links"><a href="/">Back to Workbench</a><a href="{AccountEndpoints.SignOutPath}">Sign out</a></div>""");
        return Page("Password and sign-in", body.ToString(), error is null ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }
}
