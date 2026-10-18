using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;

namespace Workbench.Web.Security;

/// <summary>
/// SCIM 2.0 (RFC 7643/7644) user provisioning for the identity provider (Entra ID, Okta): create, get, list with
/// <c>filter=userName eq "..."</c>, PATCH/PUT of <c>active</c> and the name, and DELETE, which deactivates (users are
/// never deleted). Secured by <see cref="ScimAuthenticationHandler"/>; every call is a security event. Roles stay
/// with Workbench administrators: SCIM users start as Employees.
/// </summary>
public static partial class ScimEndpoints
{
    public const string BasePath = "/scim/v2";
    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string ListSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    private const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    private const string ContentType = "application/scim+json";

    public static IServiceCollection AddWorkbenchScim(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder().AddPolicy(ScimAuthenticationHandler.PolicyName, p =>
            p.AddAuthenticationSchemes(ScimAuthenticationHandler.SchemeName).RequireClaim("scim_client"));
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchScim(this IEndpointRouteBuilder app)
    {
        var settings = app.ServiceProvider.GetRequiredService<IConfiguration>().GetSection(ScimSettings.SectionName).Get<ScimSettings>() ?? new ScimSettings();
        if (!settings.Enabled)
        {
            return app;
        }

        var scim = app.MapGroup(BasePath).RequireAuthorization(ScimAuthenticationHandler.PolicyName).ExcludeFromDescription().AddEndpointFilter(LogAndMapErrors);
        scim.MapGet("/ServiceProviderConfig", () => Json(new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"),
            ["patch"] = new JsonObject { ["supported"] = true },
            ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
            ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = 200 },
            ["changePassword"] = new JsonObject { ["supported"] = false },
            ["sort"] = new JsonObject { ["supported"] = false },
            ["etag"] = new JsonObject { ["supported"] = false },
            ["authenticationSchemes"] = new JsonArray(new JsonObject
            {
                ["type"] = "oauthbearertoken",
                ["name"] = "Bearer token",
                ["description"] = "The token configured in Scim:BearerToken",
            }),
        }));

        scim.MapGet("/Users", ListAsync);
        scim.MapGet("/Users/{id}", GetAsync);
        scim.MapPost("/Users", CreateAsync);
        scim.MapPatch("/Users/{id}", PatchAsync);
        scim.MapPut("/Users/{id}", ReplaceAsync);
        scim.MapDelete("/Users/{id}", DeleteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext http, IWorkbenchDbContextFactory dbFactory, string? filter, int? startIndex, int? count)
    {
        await using var db = await dbFactory.CreateAsync(http.RequestAborted);
        var users = db.Set<AppUser>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var match = FilterRegex().Match(filter.Trim());
            if (!match.Success)
            {
                return Error(StatusCodes.Status400BadRequest, "Only 'userName eq \"...\"' and 'externalId eq \"...\"' filters are supported.", "invalidFilter");
            }

            var value = match.Groups["value"].Value.Replace("\\\"", "\"");
            users = match.Groups["attr"].Value.Equals("externalId", StringComparison.OrdinalIgnoreCase)
                ? users.Where(u => u.ExternalId == value)
                : users.Where(u => u.Email == AccessControl.Normalize(value));
        }

        var start = Math.Max(1, startIndex ?? 1);
        var take = Math.Clamp(count ?? 100, 0, 200);
        var total = await users.CountAsync(http.RequestAborted);
        var page = await users.OrderBy(u => u.Email).Skip(start - 1).Take(take).ToListAsync(http.RequestAborted);
        var resources = new JsonArray(page.Select(u => (JsonNode)ToResource(u, http.Request)).ToArray());
        return Json(new JsonObject
        {
            ["schemas"] = new JsonArray(ListSchema),
            ["totalResults"] = total,
            ["startIndex"] = start,
            ["itemsPerPage"] = page.Count,
            ["Resources"] = resources,
        });
    }

    private static async Task<IResult> GetAsync(HttpContext http, IWorkbenchDbContextFactory dbFactory, string id)
    {
        var user = await FindAsync(dbFactory, id, http.RequestAborted);
        return user is null ? NotFound(id) : Json(ToResource(user, http.Request));
    }

    private static async Task<IResult> CreateAsync(HttpContext http, UserLifecycle lifecycle)
    {
        var body = await ReadBodyAsync(http);
        var email = (body["userName"]?.GetValue<string>() ?? PrimaryEmail(body))?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return Error(StatusCodes.Status400BadRequest, "userName (the user's email) is required.", "invalidValue");
        }

        try
        {
            var user = await lifecycle.CreateAsync(email, DisplayName(body), OrgRoles.Employee, ReadActive(body["active"]) ?? true,
                body["externalId"]?.GetValue<string>(), UserLifecycle.ScimActor, http.RequestAborted);
            var resource = ToResource(user, http.Request);
            return Results.Text(resource.ToJsonString(), ContentType, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status409Conflict, ex.Message, "uniqueness");
        }
    }

    private static async Task<IResult> PatchAsync(HttpContext http, IWorkbenchDbContextFactory dbFactory, UserLifecycle lifecycle, string id)
    {
        var user = await FindAsync(dbFactory, id, http.RequestAborted);
        if (user is null)
        {
            return NotFound(id);
        }

        var body = await ReadBodyAsync(http);
        if (body["Operations"] is not JsonArray operations)
        {
            return Error(StatusCodes.Status400BadRequest, $"Expected a {PatchSchema} body with Operations.", "invalidSyntax");
        }

        bool? active = null;
        string? displayName = null, givenName = null, familyName = null, externalId = null;
        foreach (var operation in operations.OfType<JsonObject>())
        {
            var op = operation["op"]?.GetValue<string>()?.ToLowerInvariant();
            if (op is not ("add" or "replace" or "remove"))
            {
                return Error(StatusCodes.Status400BadRequest, $"Unsupported op '{op}'.", "invalidSyntax");
            }

            var path = operation["path"]?.GetValue<string>();
            var values = path is null
                ? (operation["value"] as JsonObject)?.Select(p => (p.Key, p.Value)).ToList() ?? []
                : [(path, operation["value"])];
            foreach (var (key, value) in values)
            {
                switch (key.ToLowerInvariant())
                {
                    case "active":
                        active = op == "remove" ? false : ReadActive(value);
                        break;
                    case "displayname":
                        displayName = Text(value);
                        break;
                    case "name.givenname":
                        givenName = Text(value);
                        break;
                    case "name.familyname":
                        familyName = Text(value);
                        break;
                    case "name.formatted":
                        displayName ??= Text(value);
                        break;
                    case "name" when value is JsonObject nameValue:
                        givenName = Text(nameValue["givenName"]);
                        familyName = Text(nameValue["familyName"]);
                        displayName ??= Text(nameValue["formatted"]);
                        break;
                    case "externalid":
                        externalId = Text(value);
                        break;
                    case "username":
                        if (!string.Equals(AccessControl.Normalize(Text(value)), user.Email, StringComparison.Ordinal))
                        {
                            return Error(StatusCodes.Status400BadRequest, "userName (email) can't change: history is recorded by email. Create a new user instead.", "mutability");
                        }

                        break;
                    default:
                        // Attributes Workbench doesn't store (title, phone numbers, addresses) are accepted and ignored.
                        break;
                }
            }
        }

        var name = displayName ?? JoinName(givenName, familyName);
        if (name is not null || externalId is not null)
        {
            await lifecycle.UpdateProfileAsync(user.Email, name, externalId, UserLifecycle.ScimActor, http.RequestAborted);
        }

        if (active is { } isActive)
        {
            await lifecycle.SetActiveAsync(user.Email, isActive, UserLifecycle.ScimActor, "Identity provider (SCIM)", http.RequestAborted);
        }

        return Json(ToResource((await FindAsync(dbFactory, id, http.RequestAborted))!, http.Request));
    }

    private static async Task<IResult> ReplaceAsync(HttpContext http, IWorkbenchDbContextFactory dbFactory, UserLifecycle lifecycle, string id)
    {
        var user = await FindAsync(dbFactory, id, http.RequestAborted);
        if (user is null)
        {
            return NotFound(id);
        }

        var body = await ReadBodyAsync(http);
        if (body["userName"]?.GetValue<string>() is { } userName && AccessControl.Normalize(userName) != user.Email)
        {
            return Error(StatusCodes.Status400BadRequest, "userName (email) can't change.", "mutability");
        }

        await lifecycle.UpdateProfileAsync(user.Email, DisplayName(body), body["externalId"]?.GetValue<string>(), UserLifecycle.ScimActor, http.RequestAborted);
        if (ReadActive(body["active"]) is { } active)
        {
            await lifecycle.SetActiveAsync(user.Email, active, UserLifecycle.ScimActor, "Identity provider (SCIM)", http.RequestAborted);
        }

        return Json(ToResource((await FindAsync(dbFactory, id, http.RequestAborted))!, http.Request));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, IWorkbenchDbContextFactory dbFactory, UserLifecycle lifecycle, string id)
    {
        var user = await FindAsync(dbFactory, id, http.RequestAborted);
        if (user is null)
        {
            return NotFound(id);
        }

        // Deactivate, never delete: the user's timesheets, approvals and audit records keep their author.
        await lifecycle.SetActiveAsync(user.Email, false, UserLifecycle.ScimActor, "Deleted in the identity provider (SCIM)", http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>Records every SCIM call and turns lifecycle errors into SCIM errors.</summary>
    private static async ValueTask<object?> LogAndMapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        object? result;
        try
        {
            result = await next(context);
        }
        catch (ArgumentException ex)
        {
            result = Error(StatusCodes.Status400BadRequest, ex.Message, "invalidValue");
        }
        catch (WorkbenchNotFoundException ex)
        {
            result = Error(StatusCodes.Status404NotFound, ex.Message, null);
        }
        catch (JsonException)
        {
            result = Error(StatusCodes.Status400BadRequest, "The body isn't valid JSON.", "invalidSyntax");
        }
        catch (InvalidOperationException)
        {
            result = Error(StatusCodes.Status400BadRequest, "A value has the wrong type.", "invalidValue");
        }

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var log = http.RequestServices.GetRequiredService<SecurityEventLog>();
        await log.RecordAsync(SecurityEventTypes.Scim, $"{http.Request.Method} {http.Request.Path}{http.Request.QueryString}",
            status < 400 ? SecurityOutcomes.Success : SecurityOutcomes.Failure, $"HTTP {status}", actor: UserLifecycle.ScimActor);
        return result;
    }

    internal static Task WriteErrorAsync(HttpResponse response, int status, string detail)
    {
        response.ContentType = ContentType;
        return response.WriteAsync(ErrorBody(status, detail, null).ToJsonString());
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpContext http) =>
        await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject
            ?? throw new JsonException("Expected a JSON object.");

    private static async Task<AppUser?> FindAsync(IWorkbenchDbContextFactory dbFactory, string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return null;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == guid, cancellationToken);
    }

    private static JsonObject ToResource(AppUser user, HttpRequest request)
    {
        var parts = user.DisplayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var location = $"{request.Scheme}://{request.Host}{BasePath}/Users/{user.Id}";
        return new JsonObject
        {
            ["schemas"] = new JsonArray(UserSchema),
            ["id"] = user.Id.ToString(),
            ["externalId"] = user.ExternalId,
            ["userName"] = user.Email,
            ["displayName"] = user.DisplayName,
            ["name"] = new JsonObject
            {
                ["formatted"] = user.DisplayName,
                ["givenName"] = parts.ElementAtOrDefault(0),
                ["familyName"] = parts.ElementAtOrDefault(1),
            },
            ["emails"] = new JsonArray(new JsonObject { ["value"] = user.Email, ["type"] = "work", ["primary"] = true }),
            ["active"] = user.IsActive,
            ["meta"] = new JsonObject
            {
                ["resourceType"] = "User",
                ["created"] = user.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["lastModified"] = (user.DeactivatedAtUtc ?? user.CreatedAtUtc).ToString("O", CultureInfo.InvariantCulture),
                ["location"] = location,
            },
        };
    }

    private static string? PrimaryEmail(JsonObject body) =>
        (body["emails"] as JsonArray)?.OfType<JsonObject>()
            .OrderByDescending(e => e["primary"]?.GetValue<bool>() == true)
            .Select(e => Text(e["value"]))
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string? DisplayName(JsonObject body) =>
        Text(body["displayName"]) ?? Text(body["name"]?["formatted"]) ?? JoinName(Text(body["name"]?["givenName"]), Text(body["name"]?["familyName"]));

    private static string? JoinName(string? given, string? family)
    {
        var name = $"{given} {family}".Trim();
        return name.Length == 0 ? null : name;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>Entra ID sends <c>active</c> as a string ("False"); others send a boolean.</summary>
    private static bool? ReadActive(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) => parsed,
        _ => null,
    };

    private static IResult Json(JsonObject body) => Results.Text(body.ToJsonString(), ContentType);

    private static IResult NotFound(string id) => Error(StatusCodes.Status404NotFound, $"User {id} not found.", null);

    private static IResult Error(int status, string detail, string? scimType) =>
        Results.Text(ErrorBody(status, detail, scimType).ToJsonString(), ContentType, statusCode: status);

    private static JsonObject ErrorBody(int status, string detail, string? scimType)
    {
        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(ErrorSchema),
            ["status"] = status.ToString(CultureInfo.InvariantCulture),
            ["detail"] = detail,
        };
        if (scimType is not null)
        {
            body["scimType"] = scimType;
        }

        return body;
    }

    [GeneratedRegex("""^(?<attr>userName|externalId)\s+eq\s+"(?<value>(?:[^"\\]|\\.)*)"$""", RegexOptions.IgnoreCase)]
    private static partial Regex FilterRegex();
}
