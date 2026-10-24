using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Workbench.Tests.Storage;

/// <summary>A request the fake store received: what the SDK actually put on the wire.</summary>
internal sealed record RecordedRequest(string Method, string RawTarget, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>A real HTTP listener on 127.0.0.1 (a random port), so the real AWS and Google SDKs talk to it unmodified.</summary>
internal abstract class FakeHttpServer : IAsyncDisposable
{
    private WebApplication? _app;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public ConcurrentQueue<string> Errors { get; } = new();
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public Uri BaseAddress { get; private set; } = null!;

    protected async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(async context =>
        {
            using var buffer = new MemoryStream();
            if (context.Request.Headers.ContentEncoding.ToString() == "gzip")
            {
                // The Google SDK gzips JSON request bodies.
                await using var gzip = new System.IO.Compression.GZipStream(context.Request.Body, System.IO.Compression.CompressionMode.Decompress);
                await gzip.CopyToAsync(buffer);
            }
            else
            {
                await context.Request.Body.CopyToAsync(buffer);
            }

            var raw = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
            var headers = context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
            var request = new RecordedRequest(context.Request.Method, raw, headers, buffer.ToArray());
            Requests.Enqueue(request);
            try
            {
                await HandleAsync(context, request);
            }
            catch (Exception ex)
            {
                Errors.Enqueue($"{request.Method} {raw}: {ex}");
                throw;
            }
        });
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseAddress = new Uri(address.TrimEnd('/') + "/");
    }

    protected abstract Task HandleAsync(HttpContext context, RecordedRequest request);

    protected static (string Path, Dictionary<string, string> Query) Split(string rawTarget)
    {
        var q = rawTarget.IndexOf('?');
        var path = q < 0 ? rawTarget : rawTarget[..q];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
        {
            foreach (var pair in rawTarget[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                query[Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        return (path, query);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}

/// <summary>
/// An S3 bucket in path-style addressing (PUT, GET, DELETE, ListObjectsV2) that checks every request's Signature
/// Version 4 signature against <see cref="SecretKey"/> and the body against its x-amz-content-sha256 and Content-MD5.
/// A bad signature gets 403 SignatureDoesNotMatch, as from S3.
/// </summary>
internal sealed class FakeS3Server : FakeHttpServer
{
    public const string AccessKey = "AKIAFAKEFAKEFAKE";
    public const string SecretKey = "fake/secret/key/for/tests/only/0123456789";
    public const string Region = "us-east-1";
    public const string Bucket = "workbench-test";

    public int PageSize { get; set; } = 1000;
    public List<string> SignatureFailures { get; } = [];

    public static async Task<FakeS3Server> StartNewAsync()
    {
        var server = new FakeS3Server();
        await server.StartAsync();
        return server;
    }

    protected override async Task HandleAsync(HttpContext context, RecordedRequest request)
    {
        if (VerifySignature(request) is { } problem)
        {
            SignatureFailures.Add(problem);
            await XmlError(context, 403, "SignatureDoesNotMatch", problem);
            return;
        }

        var (path, query) = Split(request.RawTarget);
        var segments = path.TrimStart('/').Split('/', 2);
        if (segments[0] != Bucket)
        {
            await XmlError(context, 404, "NoSuchBucket", "The specified bucket does not exist");
            return;
        }

        var key = segments.Length > 1 ? Uri.UnescapeDataString(segments[1]) : "";
        switch (request.Method)
        {
            case "PUT" when key.Length > 0:
                if (request.Header("content-md5") is { } md5 && md5 != Convert.ToBase64String(MD5.HashData(request.Body)))
                {
                    await XmlError(context, 400, "BadDigest", "The Content-MD5 you specified did not match what we received.");
                    return;
                }

                Objects[key] = request.Body;
                context.Response.Headers.ETag = $"\"{Convert.ToHexStringLower(MD5.HashData(request.Body))}\"";
                return;
            case "GET" when key.Length > 0:
                if (!Objects.TryGetValue(key, out var bytes))
                {
                    await XmlError(context, 404, "NoSuchKey", "The specified key does not exist.");
                    return;
                }

                context.Response.ContentLength = bytes.Length;
                context.Response.ContentType = "application/octet-stream";
                await context.Response.Body.WriteAsync(bytes);
                return;
            case "DELETE" when key.Length > 0:
                Objects.TryRemove(key, out _);
                context.Response.StatusCode = 204;
                return;
            case "GET" when query.ContainsKey("list-type"):
                await ListAsync(context, query);
                return;
            default:
                await XmlError(context, 400, "InvalidRequest", "Not supported by the fake.");
                return;
        }
    }

    private async Task ListAsync(HttpContext context, Dictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? "";
        var start = int.TryParse(query.GetValueOrDefault("continuation-token"), out var s) ? s : 0;
        var all = Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var page = all.Skip(start).Take(PageSize).ToList();
        var truncated = start + page.Count < all.Count;
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">")
            .Append($"<Name>{Bucket}</Name><Prefix>{WebUtility.HtmlEncode(prefix)}</Prefix><KeyCount>{page.Count}</KeyCount><MaxKeys>{PageSize}</MaxKeys>")
            .Append($"<IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>");
        if (truncated)
        {
            xml.Append($"<NextContinuationToken>{start + page.Count}</NextContinuationToken>");
        }

        foreach (var key in page)
        {
            xml.Append($"<Contents><Key>{WebUtility.HtmlEncode(key)}</Key><Size>{Objects[key].Length}</Size><LastModified>2026-10-23T00:00:00.000Z</LastModified><ETag>\"x\"</ETag><StorageClass>STANDARD</StorageClass></Contents>");
        }

        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(xml.Append("</ListBucketResult>").ToString());
    }

    /// <summary>Recomputes the SigV4 signature (AWS "Create a signed AWS API request"); null when it matches.</summary>
    public static string? VerifySignature(RecordedRequest request)
    {
        var authorization = request.Header("authorization");
        if (authorization is null || !authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
        {
            return "not signed with AWS4-HMAC-SHA256";
        }

        var parts = authorization["AWS4-HMAC-SHA256 ".Length..].Split(',', StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var credential = parts["Credential"].Split('/');
        if (credential[0] != AccessKey)
        {
            return $"wrong access key {credential[0]}";
        }

        var (date, region, service) = (credential[1], credential[2], credential[3]);
        var amzDate = request.Header("x-amz-date") ?? "";
        var payloadHash = request.Header("x-amz-content-sha256") ?? "";
        if (payloadHash != "UNSIGNED-PAYLOAD" && payloadHash != Convert.ToHexStringLower(SHA256.HashData(request.Body)))
        {
            return "x-amz-content-sha256 doesn't match the body";
        }

        var signedHeaders = parts["SignedHeaders"].Split(';');
        var (path, query) = Split(request.RawTarget);
        var canonicalQuery = string.Join('&', query.OrderBy(q => Encode(q.Key), StringComparer.Ordinal).Select(q => $"{Encode(q.Key)}={Encode(q.Value)}"));
        var canonicalHeaders = string.Concat(signedHeaders.Select(h => $"{h}:{CollapseSpaces(request.Header(h) ?? "")}\n"));
        var canonicalRequest = $"{request.Method}\n{path}\n{canonicalQuery}\n{canonicalHeaders}\n{parts["SignedHeaders"]}\n{payloadHash}";
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + SecretKey), date), region), service), "aws4_request");
        var expected = Convert.ToHexStringLower(Hmac(key, stringToSign));
        return expected == parts["Signature"] ? null : "signature mismatch";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string CollapseSpaces(string value) => string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Encode(string value)
    {
        var text = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            {
                text.Append(c);
            }
            else
            {
                text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static async Task XmlError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{WebUtility.HtmlEncode(message)}</Message><RequestId>fake</RequestId></Error>");
    }
}

/// <summary>
/// A Google Cloud Storage bucket behind the JSON API: resumable and multipart uploads, media downloads, deletes and
/// paged listing. Records the bearer token and the object metadata each upload asked for (e.g. kmsKeyName).
/// </summary>
internal sealed class FakeGcsServer : FakeHttpServer
{
    public const string Bucket = "workbench-test";

    private readonly ConcurrentDictionary<string, (string Name, MemoryStream Data, JsonElement Metadata)> _uploads = new();

    public ConcurrentDictionary<string, JsonElement> Metadata { get; } = new(StringComparer.Ordinal);
    public int PageSize { get; set; } = 1000;

    public string StorageBaseUri => new Uri(BaseAddress, "storage/v1/").AbsoluteUri;

    public static async Task<FakeGcsServer> StartNewAsync()
    {
        var server = new FakeGcsServer();
        await server.StartAsync();
        return server;
    }

    protected override async Task HandleAsync(HttpContext context, RecordedRequest request)
    {
        var (path, query) = Split(request.RawTarget);
        var uploadPrefix = $"/upload/storage/v1/b/{Bucket}/o";
        var objectPrefix = $"/storage/v1/b/{Bucket}/o";
        if (path == uploadPrefix && request.Method == "POST" && query.GetValueOrDefault("uploadType") == "resumable")
        {
            using var json = JsonDocument.Parse(request.Body);
            var name = json.RootElement.GetProperty("name").GetString()!;
            var id = Guid.NewGuid().ToString("N");
            _uploads[id] = (name, new MemoryStream(), json.RootElement.Clone());
            context.Response.Headers.Location = new Uri(BaseAddress, $"upload/storage/v1/b/{Bucket}/o?uploadType=resumable&upload_id={id}").AbsoluteUri;
            return;
        }

        if (path == uploadPrefix && request.Method == "PUT" && query.TryGetValue("upload_id", out var uploadId) && _uploads.TryGetValue(uploadId, out var upload))
        {
            // Content-Range: "bytes 0-9/10", "bytes 0-9/*" (more to come) or "bytes */10" (finish with what was sent).
            var range = request.Header("content-range") ?? "";
            upload.Data.Write(request.Body);
            var total = range[(range.LastIndexOf('/') + 1)..];
            if (total == "*")
            {
                context.Response.StatusCode = 308;
                if (upload.Data.Length > 0)
                {
                    context.Response.Headers["Range"] = $"bytes=0-{upload.Data.Length - 1}";
                }

                return;
            }

            _uploads.TryRemove(uploadId, out _);
            Objects[upload.Name] = upload.Data.ToArray();
            Metadata[upload.Name] = upload.Metadata;
            await ObjectJson(context, upload.Name);
            return;
        }

        if (path.StartsWith(objectPrefix + "/", StringComparison.Ordinal))
        {
            var name = Uri.UnescapeDataString(path[(objectPrefix.Length + 1)..]);
            if (!Objects.TryGetValue(name, out var bytes))
            {
                await JsonError(context, 404, "No such object: " + name);
                return;
            }

            if (request.Method == "DELETE")
            {
                Objects.TryRemove(name, out _);
                context.Response.StatusCode = 204;
                return;
            }

            if (query.GetValueOrDefault("alt") == "media")
            {
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers["x-goog-hash"] = $"md5={Convert.ToBase64String(MD5.HashData(bytes))}";
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes);
                return;
            }

            await ObjectJson(context, name);
            return;
        }

        if (path == objectPrefix && request.Method == "GET")
        {
            var prefix = query.GetValueOrDefault("prefix") ?? "";
            var start = int.TryParse(query.GetValueOrDefault("pageToken"), out var s) ? s : 0;
            var all = Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            var page = all.Skip(start).Take(PageSize).ToList();
            var result = new Dictionary<string, object?>
            {
                ["kind"] = "storage#objects",
                ["items"] = page.Select(n => new { kind = "storage#object", bucket = Bucket, name = n, size = Objects[n].Length.ToString(CultureInfo.InvariantCulture) }).ToList(),
            };
            if (start + page.Count < all.Count)
            {
                result["nextPageToken"] = (start + page.Count).ToString(CultureInfo.InvariantCulture);
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(result));
            return;
        }

        await JsonError(context, 400, "Not supported by the fake: " + request.Method + " " + path);
    }

    private Task ObjectJson(HttpContext context, string name)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            kind = "storage#object",
            bucket = Bucket,
            name,
            size = Objects[name].Length.ToString(CultureInfo.InvariantCulture),
            md5Hash = Convert.ToBase64String(MD5.HashData(Objects[name])),
        }));
    }

    private static Task JsonError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code = status, message, errors = new[] { new { message, reason = status == 404 ? "notFound" : "invalid" } } } }));
    }
}
