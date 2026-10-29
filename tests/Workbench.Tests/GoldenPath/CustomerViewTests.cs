using System.Net;
using Workbench.ApiClient;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Tests.Identity;

namespace Workbench.Tests.GoldenPath;

/// <summary>
/// The customer's link on the real host and demo seed: Finance makes it, anyone holding it sees one contract's funding and sent
/// invoices without signing in, and a wrong, ended or expired link says the same thing. No costs, rates or people.
/// </summary>
public sealed class CustomerViewTests
{
    private static async Task<Guid> ContractIdAsync(HttpClient finance) =>
        (await new HttpContractService(finance).ListContractsAsync()).First(c => c.ContractNumber.Length > 0).Id;

    [Fact]
    public async Task Finance_makes_a_link_and_the_customer_sees_funding_and_invoices_without_signing_in()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        using var anonymous = web.CreateClientAs(null);
        var contractId = await ContractIdAsync(finance);
        var links = new HttpCustomerShareLinkService(finance);

        var made = await links.CreateAsync(contractId, new CreateCustomerShareLinkRequest("Army CO, J. Rivera", 30));
        Assert.StartsWith("c/", made.Path);
        Assert.True(made.Link.IsActive);

        using var page = await anonymous.GetAsync("/" + made.Path);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Funding and billing", html);
        Assert.Contains("Invoices", html);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        Assert.Contains("noindex", html);
        // Nothing internal: no cost, rate or people vocabulary on the page.
        Assert.DoesNotContain("bill rate", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost rate", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overhead", html, StringComparison.OrdinalIgnoreCase);

        // Finance sees who looked.
        var listed = (await links.ListAsync(contractId)).Single(l => l.Id == made.Link.Id);
        Assert.Equal(1, listed.ViewCount);
        Assert.NotNull(listed.LastViewedAtUtc);
    }

    [Fact]
    public async Task A_wrong_ended_or_expired_link_all_say_the_same_thing_and_only_Finance_can_make_one()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        using var anonymous = web.CreateClientAs(null);
        using var employee = web.CreateClientAs("analyst@example.com");
        var contractId = await ContractIdAsync(finance);
        var links = new HttpCustomerShareLinkService(finance);

        var made = await links.CreateAsync(contractId, new CreateCustomerShareLinkRequest("Prime PM", 7));
        using var wrong = await anonymous.GetAsync("/c/not-a-real-token");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        var wrongHtml = await wrong.Content.ReadAsStringAsync();

        var revoked = await links.RevokeAsync(contractId, made.Link.Id);
        Assert.False(revoked.IsActive);
        using var ended = await anonymous.GetAsync("/" + made.Path);
        Assert.Equal(HttpStatusCode.NotFound, ended.StatusCode);
        Assert.Equal(wrongHtml, await ended.Content.ReadAsStringAsync()); // no hint which it was

        // A label is required, and the length of time is bounded.
        await Assert.ThrowsAnyAsync<Exception>(() => links.CreateAsync(contractId, new CreateCustomerShareLinkRequest("  ", 30)));
        await Assert.ThrowsAnyAsync<Exception>(() => links.CreateAsync(contractId, new CreateCustomerShareLinkRequest("Too long", 4000)));

        // An employee can neither make nor list links.
        var asEmployee = new HttpCustomerShareLinkService(employee);
        await Assert.ThrowsAnyAsync<Exception>(() => asEmployee.CreateAsync(contractId, new CreateCustomerShareLinkRequest("Nope", 30)));
        await Assert.ThrowsAnyAsync<Exception>(() => asEmployee.ListAsync(contractId));
    }

    [Fact]
    public async Task The_token_is_stored_only_as_a_hash()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        var contractId = await ContractIdAsync(finance);

        var made = await new HttpCustomerShareLinkService(finance).CreateAsync(contractId, new CreateCustomerShareLinkRequest("Hash check", 30));
        var token = made.Path["c/".Length..];

        Assert.True(token.Length >= 43); // 256 bits, base64url
        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(made.Link));
        var dbFactory = web.Services.GetService(typeof(Workbench.Application.IWorkbenchDbContextFactory)) as Workbench.Application.IWorkbenchDbContextFactory;
        Assert.NotNull(dbFactory);
        await using var db = await dbFactory!.CreateAsync();
        var row = db.Set<Workbench.Domain.Billing.CustomerShareLink>().Single(l => l.Id == made.Link.Id);
        Assert.NotEqual(token, row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
    }
}
