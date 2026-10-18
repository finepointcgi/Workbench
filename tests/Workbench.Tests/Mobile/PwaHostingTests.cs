using System.Net;
using System.Text.Json;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Mobile;

/// <summary>The installable-app files through the real web host: served anonymously with the right types.</summary>
public sealed class PwaHostingTests
{
    [Fact]
    public async Task Manifest_service_worker_and_icons_are_served_with_their_content_types()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClientAs(null);

        using var manifest = await client.GetAsync("/manifest.webmanifest");
        using var worker = await client.GetAsync("/sw.js");
        using var offline = await client.GetAsync("/offline.html");
        using var icon = await client.GetAsync("/icons/icon-512.png");

        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, worker.StatusCode);
        Assert.Contains(worker.Content.Headers.ContentType?.MediaType, new[] { "text/javascript", "application/javascript" });
        Assert.Equal("text/html", offline.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        Assert.Equal("/m", json.RootElement.GetProperty("start_url").GetString());
        Assert.Equal("standalone", json.RootElement.GetProperty("display").GetString());
        var sizes = json.RootElement.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("sizes").GetString()).ToList();
        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
    }

    [Fact]
    public async Task Service_worker_never_caches_pages_or_api_responses()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClientAs(null);

        var script = await client.GetStringAsync("/sw.js");

        // Navigations always go to the network (offline page as the fallback) and are never put in the cache.
        Assert.Contains("request.mode === \"navigate\"", script);
        Assert.Contains("\"/api/\"", script);
        Assert.Contains("\"/_blazor\"", script);
        Assert.Contains("no-store", script);
    }

    [Fact]
    public async Task Pages_link_the_manifest_and_register_the_service_worker()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var client = factory.CreateClientAs("analyst@example.com");

        using var response = await client.GetAsync("/m");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("rel=\"manifest\" href=\"manifest.webmanifest\"", page);
        Assert.Contains("name=\"theme-color\"", page);
        Assert.Contains("js/pwa", page);
    }
}
