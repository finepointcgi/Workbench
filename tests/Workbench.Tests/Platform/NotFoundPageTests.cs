using Workbench.Tests.Identity;

namespace Workbench.Tests.Platform;

/// <summary>A mistyped or old link must land on the "We can't find that page" screen, never a blank white page.</summary>
public sealed class NotFoundPageTests
{
    [Fact]
    public async Task An_unknown_page_sends_a_person_to_the_not_found_screen_which_has_the_app_shell()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var client = web.CreateClientAs("pm@example.com");

        var response = await client.GetAsync("/accounting/period-close-typo");
        var landed = response.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found
            ? await client.GetAsync(response.Headers.Location)
            : response;

        Assert.EndsWith("/not-found", landed.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("<html", await landed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_API_path_is_still_a_bare_404()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var client = web.CreateClientAs("pm@example.com");

        var response = await client.GetAsync("/api/no-such-thing");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
