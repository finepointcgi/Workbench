using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Web.Tenancy;

/// <summary>
/// <c>GET /api/v1/organization/export</c> (MultiTenant, organization administrators): everything in the tenant's database as
/// one zip of JSON-lines files, one per table. It is the way out for a hosted organization whose trial or subscription has
/// lapsed (read-only mode never blocks it) and works at any time. Secrets are left out: password hashes, authenticator and
/// recovery secrets, one-time link and API key hashes. Each export is written to the security log.
/// </summary>
public static class OrganizationExportEndpoints
{
    public const string Path = "/api/v1/organization/export";

    private static readonly string[] ExcludedTypeFragments = ["PasswordCredential", "AccountToken", "ApiKey", "RefreshToken", "SigningKey", "DataProtection"];
    private static readonly string[] ExcludedPropertyFragments = ["Hash", "Secret", "Password", "Totp", "RecoveryCode", "ProtectedValue", "ProtectedConnection"];

    public static IEndpointRouteBuilder MapWorkbenchOrganizationExport(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<TenancyOptions>().IsMultiTenant)
        {
            return app;
        }

        app.MapGet(Path, async (HttpContext http, AccessControl access, ICurrentUser user, SecurityEventLog log, IDbContextFactory<WorkbenchDbContext> dbFactory, TimeProvider clock) =>
        {
            if (TenantScope.Current is not { } tenant)
            {
                return Results.NotFound();
            }

            try
            {
                await access.RequireAsync(Permission.AdminOrganization, "export the organization's data", http.RequestAborted);
            }
            catch (Workbench.Contracts.WorkbenchForbiddenException ex)
            {
                // Outside the /api/v1 group, so the group's error mapping doesn't apply.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }

            await log.RecordAsync("export.organization", tenant.Slug, detail: "Full data export (zip of JSON lines)", actor: user.Email, cancellationToken: http.RequestAborted);

            // The zip is built in a temporary file (deleted when closed): the response stream doesn't allow the synchronous writes ZipArchive makes,
            // and a failure part-way then yields an error instead of a truncated download.
            var file = new FileStream(System.IO.Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
            var manifest = new List<object>();
            foreach (var entity in db.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.ClrType != typeof(Dictionary<string, object>) && e.FindPrimaryKey() is not null).OrderBy(e => e.ClrType.Name))
            {
                if (ExcludedTypeFragments.Any(f => entity.ClrType.Name.Contains(f, StringComparison.Ordinal)))
                {
                    continue;
                }

                var properties = entity.GetProperties()
                    .Where(p => p.PropertyInfo is not null && !ExcludedPropertyFragments.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.PropertyInfo!).ToList();
                var rows = 0;
                string? failure = null;
                var item = zip.CreateEntry($"{entity.ClrType.Name}.jsonl", CompressionLevel.Fastest);
                await using (var stream = item.Open())
                {
                    try
                    {
                        var query = (System.Collections.IEnumerable)set.MakeGenericMethod(entity.ClrType).Invoke(db, null)!;
                        foreach (var row in query)
                        {
                            await WriteRowAsync(stream, row, properties, http.RequestAborted);
                            rows++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Say so in the manifest rather than hand over a silently incomplete export.
                        failure = ex.GetBaseException().Message;
                        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OrganizationExportEndpoints))
                            .LogWarning(ex, "Exporting {Table} for tenant {Tenant} failed after {Rows} rows", entity.ClrType.Name, tenant.Slug, rows);
                    }
                }

                manifest.Add(new { table = entity.ClrType.Name, rows, error = failure });
            }

            var summary = zip.CreateEntry("manifest.json");
            await using (var stream = summary.Open())
            {
                await JsonSerializer.SerializeAsync(stream, new { organization = tenant.DisplayName, exportedAtUtc = clock.GetUtcNow().UtcDateTime, tables = manifest },
                    new JsonSerializerOptions { WriteIndented = true }, http.RequestAborted);
            }

            zip.Dispose();
            file.Position = 0;
            await using (file)
            {
                http.Response.ContentType = "application/zip";
                http.Response.ContentLength = file.Length;
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{tenant.Slug}-export-{clock.GetUtcNow():yyyyMMdd}.zip\"";
                await file.CopyToAsync(http.Response.Body, http.RequestAborted);
            }

            return Results.Empty;
        }).RequireAuthorization().ExcludeFromDescription();
        return app;
    }

    private static async Task WriteRowAsync(Stream stream, object row, List<PropertyInfo> properties, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(properties.Count);
        foreach (var property in properties)
        {
            values[property.Name] = property.GetValue(row);
        }

        await JsonSerializer.SerializeAsync(stream, values, cancellationToken: ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
    }
}
