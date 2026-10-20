using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Integrations;
using Workbench.Application.Integrations.Connectors;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations;

/// <summary>Records every outbound request and answers with whatever the test queues (200 by default). Nothing leaves the process.</summary>
internal sealed class FakeTransport : IOutboundTransport
{
    private readonly ConcurrentQueue<OutboundResponse> _answers = new();

    public ConcurrentQueue<OutboundRequest> Sent { get; } = new();

    public OutboundResponse Default { get; set; } = new(200, "Content-Type: text/plain", "ok", null, 5);

    public void Answer(params OutboundResponse[] answers)
    {
        foreach (var a in answers)
        {
            _answers.Enqueue(a);
        }
    }

    public static OutboundResponse Status(int code) => new(code, null, code >= 400 ? "nope" : "ok", null, 5);

    public static OutboundResponse Unreachable => new(null, null, null, "Connection refused.", 5);

    public Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken cancellationToken)
    {
        Sent.Enqueue(request);
        return Task.FromResult(_answers.TryDequeue(out var answer) ? answer : Default);
    }
}

/// <summary>DNS answers from a table; unknown names resolve to a public documentation-free address.</summary>
internal sealed class FakeDns : IOutboundDns
{
    public Dictionary<string, IPAddress[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(Answers.TryGetValue(host, out var a) ? a : [IPAddress.Parse("52.1.2.3")]);
}

internal sealed class FakeSftp : ISftpUploader
{
    public ConcurrentQueue<(SftpTarget Target, string FileName, byte[] Content)> Uploads { get; } = new();

    public Task UploadAsync(SftpTarget target, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        Uploads.Enqueue((target, fileName, content));
        return Task.CompletedTask;
    }
}

internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary><see cref="SeededHost"/> (admin@example.com is an admin) with the integration fakes and a settable clock.</summary>
internal sealed class IntegrationHost : IAsyncDisposable
{
    private IntegrationHost(SeededHost seeded, FakeTransport transport, FakeDns dns, FakeSftp sftp, SettableClock clock)
    {
        Seeded = seeded;
        Transport = transport;
        Dns = dns;
        Sftp = sftp;
        Clock = clock;
    }

    public SeededHost Seeded { get; }
    public FakeTransport Transport { get; }
    public FakeDns Dns { get; }
    public FakeSftp Sftp { get; }
    public SettableClock Clock { get; }

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public static async Task<IntegrationHost> CreateAsync(Action<IntegrationOptions>? options = null, Action<IServiceCollection>? configure = null)
    {
        var transport = new FakeTransport();
        var dns = new FakeDns();
        var sftp = new FakeSftp();
        var clock = new SettableClock(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var seeded = await SeededHost.CreateAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IOutboundTransport>(transport));
            services.Replace(ServiceDescriptor.Singleton<IOutboundDns>(dns));
            services.Replace(ServiceDescriptor.Singleton<ISftpUploader>(sftp));
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<IntegrationOptions>(o => options?.Invoke(o));
            configure?.Invoke(services);
        });
        seeded.As(SeededHost.Admin);
        return new IntegrationHost(seeded, transport, dns, sftp, clock);
    }

    public IntegrationHost As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}
