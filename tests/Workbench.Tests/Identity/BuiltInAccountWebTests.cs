using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Application.Identity.Passwords;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;
using Workbench.Web.Identity;

namespace Workbench.Tests.Identity;

/// <summary>
/// Built-in Workbench accounts end to end through the real web host (Production, no server OIDC): invites by email,
/// passwords, lockout, forgot-password links, the authenticator app and organizations that require their own SSO.
/// Emails are read from the pickup folder, as a person would read them from their inbox.
/// </summary>
public sealed class BuiltInAccountWebTests
{
    private const string Admin = "admin@acme.example";
    private const string Pat = "pat@acme.example";
    private const string GoodPassword = "correct mule staple orbit";

    [Fact]
    public async Task An_invite_link_sets_a_password_and_signs_the_person_in()
    {
        await using var web = await AccountWeb.CreateAsync();
        using var admin = web.Api(Admin);
        using var created = await admin.PostAsJsonAsync("/api/v1/admin/invites", new CreateInviteRequest(Pat, "Pat Doe", OrgRoles.Timekeeper));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var link = web.LinkInMail(Pat, "/account/accept-invite");
        using var browser = web.Browser();
        var page = await browser.GetStringAsync(link);
        Assert.Contains("Welcome, Pat Doe", page);

        using var weak = await web.PostFormAsync(browser, link, new() { ["token"] = Token(link), ["password"] = "password2026!", ["confirm"] = "password2026!" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("too common", await weak.Content.ReadAsStringAsync());

        using var accepted = await web.PostFormAsync(browser, link, new() { ["token"] = Token(link), ["password"] = GoodPassword, ["confirm"] = GoodPassword });
        using var me = await browser.GetAsync("/api/v1/users/me/context");

        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal("/", accepted.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(Pat, await me.Content.ReadAsStringAsync());
        Assert.True((await web.RolesAsync(Pat)).HasFlag(OrgRoles.Timekeeper));

        // The link works once; afterwards the password signs in.
        using var again = await web.Browser().GetAsync(link);
        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        using var fresh = web.Browser();
        using var signIn = await web.SignInAsync(fresh, Pat, GoodPassword, "/sheets");
        Assert.Equal("/sheets", signIn.Headers.Location!.OriginalString);

        var events = await web.EventsAsync();
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.PasswordSet && e.Target == Pat && e.Detail == "Set from an invitation");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.InviteAccepted && e.Target == Pat);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.SignInSuccess && e.Actor == Pat && e.Detail!.StartsWith("password"));
        Assert.DoesNotContain(events, e => (e.Detail ?? "").Contains(GoodPassword));
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_account_for_a_while()
    {
        await using var web = await AccountWeb.CreateAsync();
        await web.AddUserAsync(Pat, GoodPassword);
        using var browser = web.Browser();

        using var unknown = await web.SignInAsync(browser, "nobody@acme.example", GoodPassword);
        var unknownText = await unknown.Content.ReadAsStringAsync();
        for (var i = 0; i < 4; i++)
        {
            using var wrong = await web.SignInAsync(browser, Pat, "not the password at all");
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Contains("match", await wrong.Content.ReadAsStringAsync());
        }

        using var fifth = await web.SignInAsync(browser, Pat, "still not the password");
        using var right = await web.SignInAsync(browser, Pat, GoodPassword);
        using var me = await browser.GetAsync("/api/v1/users/me/context");

        Assert.Contains("match", unknownText); // an unknown email gets the same answer as a wrong password
        Assert.Contains("paused", await fifth.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);
        Assert.Contains("paused", await right.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        var events = await web.EventsAsync();
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.LockedOut && e.Target == Pat);
        Assert.Equal(5, events.Count(e => e.EventType == SecurityEventTypes.SignInFailure && e.Target == Pat && e.Detail!.Contains("wrong password")));
    }

    [Fact]
    public async Task Sign_in_attempts_are_rate_limited()
    {
        await using var web = await AccountWeb.CreateAsync(new() { ["RateLimiting:SignInPerMinute"] = "3" });
        using var browser = web.Browser();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await browser.PostAsync(AccountEndpoints.SignInPath, new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = Pat, ["password"] = "x" }));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task Reset_links_answer_the_same_for_unknown_emails_expire_and_work_once()
    {
        await using var web = await AccountWeb.CreateAsync();
        await web.AddUserAsync(Pat, GoodPassword);
        using var oldSession = web.Browser();
        await web.SignInAsync(oldSession, Pat, GoodPassword);

        using var browser = web.Browser();
        using var known = await web.PostFormAsync(browser, PasswordSignIn.ForgotPath, new() { ["email"] = Pat });
        using var unknown = await web.PostFormAsync(browser, PasswordSignIn.ForgotPath, new() { ["email"] = "nobody@acme.example" });
        Assert.Equal((await known.Content.ReadAsStringAsync()).Replace(Pat, "EMAIL"), (await unknown.Content.ReadAsStringAsync()).Replace("nobody@acme.example", "EMAIL"));
        Assert.Empty(web.MailTo("nobody@acme.example"));

        var link = web.LinkInMail(Pat, "/account/reset-password");
        using var reset = await web.PostFormAsync(browser, link, new() { ["token"] = Token(link), ["password"] = "a brand new long secret", ["confirm"] = "a brand new long secret" });
        using var reused = await web.Browser().GetAsync(link);
        using var old = await oldSession.GetAsync("/api/v1/users/me/context");
        using var withNew = await web.SignInAsync(web.Browser(), Pat, "a brand new long secret");
        using var withOld = await web.SignInAsync(web.Browser(), Pat, GoodPassword);

        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/users/me/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode); // resetting the password ended the other sessions
        Assert.Equal(HttpStatusCode.Redirect, withNew.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withOld.StatusCode);

        // An expired link is refused, even unused.
        await web.PostFormAsync(web.Browser(), PasswordSignIn.ForgotPath, new() { ["email"] = Pat });
        var second = web.LinkInMail(Pat, "/account/reset-password");
        await web.ExpireLinksAsync(Pat);
        using var expired = await web.PostFormAsync(web.Browser(), second, new() { ["token"] = Token(second), ["password"] = "another long secret here", ["confirm"] = "another long secret here" },
            tokenPage: PasswordSignIn.ForgotPath);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Contains(await web.EventsAsync(), e => e.EventType == SecurityEventTypes.PasswordResetRequested && e.Target == "nobody@acme.example" && e.Outcome == SecurityOutcomes.Failure);
    }

    [Fact]
    public async Task People_set_up_an_authenticator_app_and_then_sign_in_with_its_codes()
    {
        await using var web = await AccountWeb.CreateAsync();
        await web.AddUserAsync(Pat, GoodPassword);
        using var browser = web.Browser();
        await web.SignInAsync(browser, Pat, GoodPassword);

        var setup = await browser.GetStringAsync(PasswordSignIn.MfaSetupPath);
        var secret = SecretOn(setup);
        Assert.Contains("<svg", setup);
        using var enrolled = await web.PostFormAsync(browser, PasswordSignIn.MfaSetupPath, new() { ["code"] = CodeNow(secret), ["password"] = GoodPassword });
        var codesPage = await enrolled.Content.ReadAsStringAsync();
        var recoveryCodes = Regex.Matches(codesPage, @"<li>([a-z0-9]{5}-[a-z0-9]{5})</li>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, recoveryCodes.Count);

        // Next sign-in: the password alone isn't enough.
        using var next = web.Browser();
        using var password = await web.SignInAsync(next, Pat, GoodPassword, "/sheets");
        using var halfway = await next.GetAsync("/api/v1/users/me/context");
        using var wrong = await web.PostFormAsync(next, PasswordSignIn.MfaPath, new() { ["code"] = "000000" });
        using var right = await web.PostFormAsync(next, PasswordSignIn.MfaPath, new() { ["code"] = CodeNow(secret, ahead: 1) });
        using var me = await next.GetAsync("/api/v1/users/me/context");

        Assert.Equal(PasswordSignIn.MfaPath, password.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, halfway.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("/sheets", right.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // A recovery code works once.
        using var lostPhone = web.Browser();
        await web.SignInAsync(lostPhone, Pat, GoodPassword);
        using var recovered = await web.PostFormAsync(lostPhone, PasswordSignIn.MfaPath, new() { ["code"] = recoveryCodes[0].ToUpperInvariant() });
        using var lostAgain = web.Browser();
        await web.SignInAsync(lostAgain, Pat, GoodPassword);
        using var reused = await web.PostFormAsync(lostAgain, PasswordSignIn.MfaPath, new() { ["code"] = recoveryCodes[0] });

        Assert.Equal(HttpStatusCode.Redirect, recovered.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        var events = await web.EventsAsync();
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.MfaEnabled && e.Target == Pat);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.RecoveryCodeUsed && e.Target == Pat);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.SignInSuccess && e.Actor == Pat && e.Detail == "password; amr=pwd,otp");
    }

    [Fact]
    public async Task When_the_organization_requires_mfa_people_without_an_app_set_one_up_before_they_are_in()
    {
        await using var web = await AccountWeb.CreateAsync();
        await web.AddUserAsync(Pat, GoodPassword);
        await web.ConfigureSignInAsync(s => s.MfaRequirement = MfaRequirementLevel.Everyone);
        using var browser = web.Browser();

        using var password = await web.SignInAsync(browser, Pat, GoodPassword);
        using var halfway = await browser.GetAsync("/api/v1/users/me/context");
        var setup = await browser.GetStringAsync(PasswordSignIn.MfaSetupPath);
        using var done = await web.PostFormAsync(browser, PasswordSignIn.MfaSetupPath, new() { ["code"] = CodeNow(SecretOn(setup)) });
        using var me = await browser.GetAsync("/api/v1/users/me/context");

        Assert.Equal(PasswordSignIn.MfaSetupPath, password.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, halfway.StatusCode);
        Assert.Contains("requires two-step verification", setup);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Contains("recovery codes", await done.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // An administrator resets it (lost phone): the person is signed out and must set up a new app.
        using var admin = web.Api(Admin);
        using var reset = await admin.PostAsync($"/api/v1/admin/users/{Pat}/reset-mfa", null);
        var user = await reset.Content.ReadFromJsonAsync<UserAdminDto>();
        using var afterReset = await browser.GetAsync("/api/v1/users/me/context");
        using var again = await web.SignInAsync(web.Browser(), Pat, GoodPassword);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(user!.MfaEnabled);
        Assert.True(user.HasPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReset.StatusCode);
        Assert.Equal(PasswordSignIn.MfaSetupPath, again.Headers.Location!.OriginalString);
        Assert.Contains(await web.EventsAsync(), e => e.EventType == SecurityEventTypes.MfaDisabled && e.Target == Pat && e.Actor == Admin);
    }

    [Fact]
    public async Task Organizations_that_require_their_own_sso_refuse_passwords()
    {
        await using var web = await AccountWeb.CreateAsync();
        await web.AddUserAsync(Pat, GoodPassword);
        using var browser = web.Browser();

        // SSO offered next to passwords: both work.
        await web.ConfigureSignInAsync(s =>
        {
            s.SsoEnabled = true;
            s.DisplayName = "Acme Okta";
            s.Authority = "https://idp.test/acme";
            s.ClientId = "acme-workbench";
        });
        var page = await browser.GetStringAsync(AccountEndpoints.SignInPath);
        Assert.Contains("Sign in with Acme Okta", page);
        Assert.Contains("name=\"password\"", page);

        await web.ConfigureSignInAsync(s => s.RequireOrganizationIdp = true);
        using var challenge = await browser.GetAsync(AccountEndpoints.SignInPath);
        using var refused = await browser.PostAsync(AccountEndpoints.SignInPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AccountWeb.AntiforgeryToken(page),
            ["email"] = Pat,
            ["password"] = GoodPassword,
        }));
        using var me = await browser.GetAsync("/api/v1/users/me/context");

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith("https://idp.test/acme/authorize", challenge.Headers.Location!.ToString());
        Assert.Equal($"{AccountEndpoints.AccessDeniedPath}?reason=org_idp", refused.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Contains(await web.EventsAsync(), e => e.EventType == SecurityEventTypes.SignInFailure && e.Target == Pat && e.Detail!.Contains("own identity provider"));
    }

    [Fact]
    public async Task People_change_their_password_and_sign_out_everywhere()
    {
        await using var web = await AccountWeb.CreateAsync();
        await web.AddUserAsync(Pat, GoodPassword);
        using var laptop = web.Browser();
        using var phone = web.Browser();
        await web.SignInAsync(laptop, Pat, GoodPassword);
        await web.SignInAsync(phone, Pat, GoodPassword);

        using var changed = await web.PostFormAsync(laptop, PasswordSignIn.SecurityPath + "/password",
            new() { ["current"] = GoodPassword, ["password"] = "the second long secret", ["confirm"] = "the second long secret" }, tokenPage: PasswordSignIn.SecurityPath);
        Assert.Contains("Your password was changed", await changed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/v1/users/me/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/v1/users/me/context")).StatusCode);

        using var everywhere = await web.PostFormAsync(laptop, PasswordSignIn.SecurityPath + "/sign-out-everywhere", new(), tokenPage: PasswordSignIn.SecurityPath);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/v1/users/me/context")).StatusCode);
        Assert.Contains(await web.EventsAsync(), e => e.EventType == SecurityEventTypes.SessionsRevoked && e.Target == Pat && e.Detail == "Signed out everywhere by the user");
    }

    [Fact]
    public async Task The_demo_shows_the_user_picker_next_to_the_real_sign_in()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var signIn = await browser.GetStringAsync(AccountEndpoints.SignInPath);
        var picker = await browser.GetStringAsync(DevSignIn.Path);

        Assert.Contains("name=\"password\"", signIn);
        Assert.Contains(DevSignIn.Path, signIn);
        Assert.Contains(AccountEndpoints.SignInPath, picker);
        Assert.Contains("dev@workbench.local", picker);
    }

    [Fact]
    public void Passwords_must_be_long_and_uncommon()
    {
        var options = new PasswordOptions();

        Assert.NotNull(PasswordPolicy.Problem("short1!", Pat, options));
        Assert.NotNull(PasswordPolicy.Problem("Password2026!", Pat, options));
        Assert.NotNull(PasswordPolicy.Problem("123456789012", Pat, options));
        Assert.NotNull(PasswordPolicy.Problem("qwertyuiop12", Pat, options));
        Assert.NotNull(PasswordPolicy.Problem("pat@acme.example1", Pat, options));
        Assert.NotNull(PasswordPolicy.Problem("aaaaaaaaaaaaaaaa", Pat, options));
        Assert.Null(PasswordPolicy.Problem(GoodPassword, Pat, options));
        Assert.Null(PasswordPolicy.Problem("Tr4ffic-Cone-Lantern", Pat, options));
    }

    [Fact]
    public void Totp_codes_match_the_rfc_6238_test_vectors()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");

        Assert.Equal("287082", Totp.Code(secret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(59))));
        Assert.Equal("081804", Totp.Code(secret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(1111111109))));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(secret));
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.Match(secret, "081804", now, null);
        Assert.NotNull(step);
        Assert.Null(Totp.Match(secret, "081804", now, step)); // no replay
        Assert.Null(Totp.Match(secret, "081804", now.AddMinutes(5), null));
    }

    // ---------- Helpers ----------

    private static string Token(string link) => Uri.UnescapeDataString(new Uri(link).Query.Split("token=")[1]);

    private static string SecretOn(string setupPage) => Regex.Match(setupPage, @"<code>([A-Z2-7 ]+)</code>").Groups[1].Value.Replace(" ", "");

    private static string CodeNow(string base32, int ahead = 0) => Totp.Code(Base32Decode(base32), Totp.StepAt(DateTimeOffset.UtcNow) + ahead);

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return bytes.ToArray();
    }

    private sealed class AccountWeb(WorkbenchWebFactory factory, string mailFolder) : IAsyncDisposable
    {
        public static async Task<AccountWeb> CreateAsync(Dictionary<string, string>? extra = null)
        {
            var mail = Path.Combine(Path.GetTempPath(), $"workbench-mail-{Guid.NewGuid():N}");
            var settings = new Dictionary<string, string>
            {
                ["Email:Mode"] = "Pickup",
                ["Email:PickupFolder"] = mail,
                ["Notifications:PublicBaseUrl"] = "https://localhost",
                ["RateLimiting:SignInPerMinute"] = "1000",
            };
            foreach (var (key, value) in extra ?? [])
            {
                settings[key] = value;
            }

            var factory = new WorkbenchWebFactory("Production", testAuth: true, settings: settings);
            factory.Services.GetRequiredService<OrganizationSsoBackchannel>().Handler = new MockOidcProvider("https://idp.test/acme", "acme-workbench", "secret");
            await using var scope = factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            await RoleStore.AddUserAsync(db, new AppUser { Email = Admin, DisplayName = "Ada Admin", CreatedAtUtc = DateTime.UtcNow }, OrgRoles.Admin);
            await db.SaveChangesAsync();
            return new AccountWeb(factory, mail);
        }

        /// <summary>A browser: cookies, no automatic redirects, HTTPS (the auth cookie is Secure outside Development).</summary>
        public HttpClient Browser() =>
            factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost/") });

        /// <summary>API calls as <paramref name="email"/> through the test header, standing in for a signed-in admin.</summary>
        public HttpClient Api(string email)
        {
            var client = Browser();
            client.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, email);
            return client;
        }

        public static string AntiforgeryToken(string page) =>
            Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

        /// <summary>Loads <paramref name="tokenPage"/> (default: the page itself) for the form's antiforgery token, then posts the form.</summary>
        public async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string path, Dictionary<string, string> form, string? tokenPage = null)
        {
            var page = await browser.GetStringAsync(tokenPage ?? path).ConfigureAwait(false);
            form["__RequestVerificationToken"] = AntiforgeryToken(page);
            return await browser.PostAsync(path.Split('?')[0], new FormUrlEncodedContent(form));
        }

        public async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string email, string password, string? returnUrl = null)
        {
            var page = await browser.GetStringAsync(AccountEndpoints.SignInPath);
            var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = AntiforgeryToken(page), ["email"] = email, ["password"] = password };
            if (returnUrl is not null)
            {
                form["returnUrl"] = returnUrl;
            }

            return await browser.PostAsync(AccountEndpoints.SignInPath, new FormUrlEncodedContent(form));
        }

        public async Task AddUserAsync(string email, string password)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using (var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync())
            {
                await RoleStore.AddUserAsync(db, new AppUser { Email = email, DisplayName = "Pat Doe", CreatedAtUtc = DateTime.UtcNow }, OrgRoles.Employee);
                await db.SaveChangesAsync();
            }

            await scope.ServiceProvider.GetRequiredService<PasswordAccounts>().SetPasswordAsync(email, password, "Test setup");
        }

        public async Task ConfigureSignInAsync(Action<SignInSettings> configure)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using (var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync())
            {
                var settings = await db.Set<SignInSettings>().FirstOrDefaultAsync();
                if (settings is null)
                {
                    settings = new SignInSettings();
                    db.Set<SignInSettings>().Add(settings);
                }

                configure(settings);
                settings.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            scope.ServiceProvider.GetRequiredService<SignInSettingsStore>().Invalidate();
        }

        public async Task ExpireLinksAsync(string email)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            foreach (var token in await db.Set<AccountToken>().Where(t => t.Email == email).ToListAsync())
            {
                token.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            }

            await db.SaveChangesAsync();
        }

        public async Task<List<SecurityEvent>> EventsAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            return await db.Set<SecurityEvent>().AsNoTracking().ToListAsync();
        }

        public async Task<OrgRoles> RolesAsync(string email)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            var user = await db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Email == email);
            return BuiltInRoles.FlagsOf(await RoleStore.RoleIdsOfAsync(db, user.Id, default));
        }

        /// <summary>The decoded text of every email to <paramref name="to"/> in the pickup folder, oldest first.</summary>
        public List<string> MailTo(string to) =>
            (Directory.Exists(mailFolder) ? Directory.GetFiles(mailFolder, "*.eml") : [])
                .Select(f => (Time: File.GetLastWriteTimeUtc(f), Text: Decode(File.ReadAllText(f))))
                .Where(m => m.Text.Contains($"To: {to}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Time)
                .Select(m => m.Text)
                .ToList();

        /// <summary>The newest link to <paramref name="path"/> in an email to <paramref name="to"/>.</summary>
        public string LinkInMail(string to, string path)
        {
            var links = MailTo(to).SelectMany(m => Regex.Matches(m, $@"https://localhost{Regex.Escape(path)}\?token=[A-Za-z0-9_\-]+").Select(x => x.Value)).ToList();
            Assert.NotEmpty(links);
            return links[^1];
        }

        /// <summary>The headers plus each MIME part decoded (base64 or quoted-printable).</summary>
        private static string Decode(string eml)
        {
            var text = new StringBuilder(eml);
            foreach (Match part in Regex.Matches(eml, @"Content-Transfer-Encoding: (base64|quoted-printable)\r?\n(?:[^\r\n]+\r?\n)*\r?\n(.*?)(?=\r?\n--|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var body = part.Groups[2].Value;
                text.AppendLine();
                if (part.Groups[1].Value.Equals("base64", StringComparison.OrdinalIgnoreCase))
                {
                    text.Append(Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, @"\s", ""))));
                }
                else
                {
                    var joined = Regex.Replace(body, @"=\r?\n", "");
                    text.Append(Regex.Replace(joined, "=([0-9A-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()));
                }
            }

            return text.ToString();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(mailFolder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return factory.DisposeAsync();
        }
    }
}
