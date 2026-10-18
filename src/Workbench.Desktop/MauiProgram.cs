using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Workbench.ApiClient;

namespace Workbench.Desktop;

public static class MauiProgram
{
    // The desktop app is a thin client: all data, Costpoint access and authorization stay on the server.
    private const string DefaultServerUrl = "https://localhost:7154/";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMudServices();

        // WORKBENCH_TENANT_URL (or WORKBENCH_SERVER_URL + WORKBENCH_TENANT) for a SaaS tenant; WORKBENCH_SERVER_URL when self-hosted.
        var serverUrl = WorkbenchServerAddress.FromEnvironment(DefaultServerUrl);
        builder.Services.AddDesktopSignIn();
        builder.Services.AddWorkbenchApiClient(serverUrl);

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
