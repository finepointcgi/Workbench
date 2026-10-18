using System.Net;
using System.Net.Sockets;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;

namespace Workbench.ApiClient.Identity;

/// <summary>
/// Signs in through the system browser and catches the redirect on <c>http://127.0.0.1:{port}/</c> (RFC 8252
/// loopback redirect). It uses a raw socket rather than HttpListener, which on Windows needs a URL reservation,
/// so it behaves the same on Windows and macOS. Opening the browser is platform code, so the app passes it in.
/// </summary>
public sealed class LoopbackBrowser(Func<Uri, Task> openSystemBrowser, int port) : IBrowser
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private const string DoneHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Workbench</title></head>" +
        "<body style=\"font-family:system-ui;padding:48px\"><h1>Sign-in complete</h1><p>You can close this tab and return to Workbench.</p></body></html>";

    public int Port { get; } = port;

    public string RedirectUri => $"http://127.0.0.1:{Port}/";

    /// <summary>A free local port for the redirect listener.</summary>
    public static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        using var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        try
        {
            await openSystemBrowser(new Uri(options.StartUrl));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var target = await ReadRequestTargetAsync(client.GetStream(), timeout.Token);

                // Browsers also ask for /favicon.ico and the like; only the redirect carries code/error.
                var isCallback = target is not null && target.Contains('?') && (target.Contains("code=") || target.Contains("error="));
                await RespondAsync(client.GetStream(), isCallback, timeout.Token);
                if (isCallback)
                {
                    return new BrowserResult { ResultType = BrowserResultType.Success, Response = RedirectUri.TrimEnd('/') + target };
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = "Sign-in timed out." };
        }
        catch (OperationCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.UserCancel, Error = "Sign-in was cancelled." };
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>The request target of an HTTP request line ("GET /?code=x HTTP/1.1" → "/?code=x").</summary>
    internal static async Task<string?> ReadRequestTargetAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n"))
            {
                break;
            }
        }

        var requestLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
        return requestLine.Length >= 3 && requestLine[1].StartsWith('/') ? requestLine[1] : null;
    }

    private static async Task RespondAsync(Stream stream, bool isCallback, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(isCallback ? DoneHtml : string.Empty);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(isCallback ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
