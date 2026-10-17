using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Sheets;

namespace Workbench.Api;

/// <summary>REST endpoints used by the desktop app; opened to customers in v1.1.</summary>
public static class WorkbenchApi
{
    public static IEndpointRouteBuilder MapWorkbenchApi(this IEndpointRouteBuilder app)
    {
        // TODO: require OIDC bearer auth once identity is wired up.
        var v1 = app.MapGroup("/api/v1");

        var sheets = v1.MapGroup("/sheets").WithTags("Sheets");
        sheets.MapGet("/", (ISheetService service, CancellationToken ct) => service.ListAsync(ct));
        sheets.MapPost("/", async (CreateSheetRequest request, ISheetService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(CreateSheetRequest.Name)] = ["Name is required."],
                });
            }

            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/sheets/{created.Id}", created);
        });

        return app;
    }
}
