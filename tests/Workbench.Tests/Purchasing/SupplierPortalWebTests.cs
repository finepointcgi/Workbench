using System.Net;
using System.Text.RegularExpressions;
using Workbench.Tests.Identity;
using Workbench.Web.Identity;

namespace Workbench.Tests.Purchasing;

/// <summary>The supplier portal through the real web host: its own sign-in, kept apart from the internal app both ways.</summary>
public sealed class SupplierPortalWebTests
{
    // SupplierPortal:DemoContactPassword in appsettings.Development.json, which the seeded sample data uses.
    private const string Password = "portal-demo-2026";
    private const string Contact = "jordan.reyes@primavera.example";

    [Fact]
    public async Task A_vendor_signs_in_to_the_portal_and_gets_nothing_internal()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var vendor = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

        // Signed out: portal pages go to the portal's sign-in, not the internal one.
        using (var home = await vendor.GetAsync("/supplier/orders"))
        {
            Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
            Assert.StartsWith("http://localhost/supplier/signin", home.Headers.Location!.ToString());
        }

        // A post without the form token is refused.
        using (var forged = await vendor.PostAsync("/supplier/signin/password", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = Contact, ["password"] = Password })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        var signInPage = await vendor.GetStringAsync("/supplier/signin");
        Assert.DoesNotContain("dev@workbench.local", signInPage);
        using (var wrong = await vendor.PostAsync("/supplier/signin/password", Form(signInPage, ("email", Contact), ("password", "not the password"))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        signInPage = await vendor.GetStringAsync("/supplier/signin");
        string portalCookie;
        using (var signIn = await vendor.PostAsync("/supplier/signin/password", Form(signInPage, ("email", Contact), ("password", Password))))
        {
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal("/supplier", signIn.Headers.Location!.ToString());
            var cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".Workbench.Supplier=", StringComparison.Ordinal));
            Assert.Contains("path=/supplier", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            portalCookie = cookie.Split(';')[0];
        }

        // The portal: the seeded RFQ and PRIMA's awarded order.
        var rfqs = await vendor.GetStringAsync("/supplier/rfqs");
        Assert.Contains("RFQ-000001", rfqs);
        var orders = await vendor.GetStringAsync("/supplier/orders");
        Assert.Contains("Accepted", orders);
        Assert.Contains("Primavera", await vendor.GetStringAsync("/supplier"));

        // Nothing internal: the API and pages don't know this vendor, even with the portal cookie sent by hand.
        using var raw = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        foreach (var path in new[] { "/api/v1/purchasing/rfqs", "/api/v1/users/me/context", "/api/v1/sheets" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", portalCookie);
            using var response = await raw.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/purchasing/rfqs"))
        {
            request.Headers.Add("Cookie", portalCookie);
            using var page = await raw.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.Contains(DevSignIn.Path, page.Headers.Location!.ToString());
        }

        // And the other way round: an internal user's cookie doesn't open the portal.
        using var staff = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var devPage = await staff.GetStringAsync(DevSignIn.Path);
        using (await staff.PostAsync(DevSignIn.Path, Form(devPage, ("email", "dev@workbench.local"), ("returnUrl", "/"))))
        {
        }

        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/purchasing/rfqs")).StatusCode);
        using (var portal = await staff.GetAsync("/supplier/orders"))
        {
            Assert.Equal(HttpStatusCode.Redirect, portal.StatusCode);
            Assert.StartsWith("http://localhost/supplier/signin", portal.Headers.Location!.ToString());
        }
    }

    private static FormUrlEncodedContent Form(string page, params (string Name, string Value)[] fields)
    {
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).Append(new("__RequestVerificationToken", token)));
    }
}
