using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Domain.Forms;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Forms;

/// <summary>The public form page through the real web host, signed out: only the form, a form token, the honeypot and the limit.</summary>
public sealed class PublicFormWebTests
{
    [Fact]
    public async Task Anyone_with_the_link_can_add_a_row_but_sees_nothing_else()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        var token = await PublicFormAsync(factory);
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

        var page = await anonymous.GetStringAsync($"/f/{token}");
        Assert.Contains("Report a problem", page);
        Assert.Contains("name=\"website\"", page);
        Assert.DoesNotContain("<script", page);
        using (var unknown = await anonymous.GetAsync("/f/0123456789abcdef0123456789abcdef0123456789abcdef"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var forged = await anonymous.PostAsync($"/f/{token}", new FormUrlEncodedContent(new Dictionary<string, string> { ["title"] = "No token" })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        using (var missing = await anonymous.PostAsync($"/f/{token}", Form(page, ("title", ""))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains("Please fill in", await missing.Content.ReadAsStringAsync());
        }

        using (var sent = await anonymous.PostAsync($"/f/{token}", Form(await anonymous.GetStringAsync($"/f/{token}"), ("title", "Badge reader broken"))))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Contains("Thanks", await sent.Content.ReadAsStringAsync());
        }

        using (var spam = await anonymous.PostAsync($"/f/{token}", Form(await anonymous.GetStringAsync($"/f/{token}"), ("title", "Cheap watches"), ("website", "http://spam.example"))))
        {
            Assert.Equal(HttpStatusCode.OK, spam.StatusCode);
        }

        for (var i = 0; i < 5; i++)
        {
            using var more = await anonymous.PostAsync($"/f/{token}", Form(await anonymous.GetStringAsync($"/f/{token}"), ("title", $"Again {i}")));
            if (more.StatusCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        using (var limited = await anonymous.PostAsync($"/f/{token}", Form(await anonymous.GetStringAsync($"/f/{token}"), ("title", "One too many"))))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
        var titles = await db.Items.Select(i => i.Title).ToListAsync();
        Assert.Contains("Badge reader broken", titles);
        Assert.DoesNotContain("Cheap watches", titles);
        Assert.DoesNotContain("One too many", titles);
    }

    /// <summary>A public form on the first seeded sheet, written straight to the database; returns its token.</summary>
    private static async Task<string> PublicFormAsync(WorkbenchWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
        var sheet = await db.Sheets.OrderBy(s => s.Name).FirstAsync();
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        db.Set<SheetForm>().Add(new SheetForm
        {
            SheetId = sheet.Id,
            Title = "Report a problem",
            Fields = [new SheetFormField { Key = "title", Label = "What's wrong?", Required = true }],
            Audience = FormAudience.Public,
            PublicToken = token,
            CreatedBy = "pm@workbench.local",
            NotifyOnSubmit = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return token;
    }

    private static FormUrlEncodedContent Form(string page, params (string Name, string Value)[] fields)
    {
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).Append(new("__RequestVerificationToken", token)));
    }
}
