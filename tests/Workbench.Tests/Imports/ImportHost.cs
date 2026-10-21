using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Application.Imports.Smartsheet;
using Workbench.Contracts.Collaboration;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;
using Workbench.Contracts.Imports;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Imports;

/// <summary>
/// The real application registrations over an in-memory database, signed in as a Finance user who owns the
/// "Projects" workspace, with fiscal year 2027 (Oct 2026 – Sep 2027) and a fake Smartsheet API.
/// </summary>
internal sealed class ImportHost : IAsyncDisposable
{
    public const string Finance = "finance@example.com";
    public const string Employee = "employee@example.com";
    public const string Token = "test-token-1234567890";

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private ImportHost()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestDb.CreateFactory());
        services.AddSingleton<IBlobStore>(Blobs);
        services.AddSingleton<ICurrentUser>(User);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddWorkbenchApplication();
        services.AddSingleton<ISmartsheetClient>(_ => new SmartsheetApiClient(
            new HttpClient(Smartsheet) { BaseAddress = SmartsheetApiClient.DefaultBaseAddress }, null, new NoWaitRetry()));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateAsyncScope();
    }

    public MemoryBlobStore Blobs { get; } = new();
    public TestUser User { get; } = new() { Email = Finance };
    public FakeSmartsheet Smartsheet { get; } = new();

    public IImportService Imports => Get<IImportService>();

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public static async Task<ImportHost> CreateAsync()
    {
        var host = new ImportHost();
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var workspace = new Workspace { Name = "Projects" };
            db.Workspaces.Add(workspace);
            await RoleStore.AddUserAsync(db, new AppUser { Email = Finance, DisplayName = "Fin" }, OrgRoles.Finance);
            await RoleStore.AddUserAsync(db, new AppUser { Email = Employee, DisplayName = "Emp" }, OrgRoles.Employee);
            db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = Finance, Role = WorkspaceRole.Owner });
            await db.SaveChangesAsync();
        }

        await host.Get<IFiscalCalendarService>().CreateFiscalYearAsync(2027, new DateOnly(2026, 10, 1));
        return host;
    }

    /// <summary>Uploads <paramref name="files"/>, keeps the suggested mapping (or applies <paramref name="adjust"/>), validates and dry-runs.</summary>
    public async Task<ImportJobDetailDto> PrepareAsync(ImportSource source, ImportKind kind, IReadOnlyList<UploadFileRequest> files,
        ImportOptionsDto? options = null, Func<ImportMappingDto, ImportMappingDto>? adjust = null)
    {
        var job = await Imports.UploadAsync(new UploadImportRequest(source, kind, files, options));
        var mapping = adjust is null ? job.Mapping : adjust(job.Mapping);
        await Imports.SaveMappingAsync(job.Job.Id, new SaveImportMappingRequest(mapping, options ?? job.Options));
        await Imports.ValidateAsync(job.Job.Id);
        return await Imports.DryRunAsync(job.Job.Id);
    }

    /// <summary>Uploads, maps, validates, dry-runs and imports.</summary>
    public async Task<ImportJobDetailDto> ImportAsync(ImportSource source, ImportKind kind, IReadOnlyList<UploadFileRequest> files,
        ImportOptionsDto? options = null, Func<ImportMappingDto, ImportMappingDto>? adjust = null)
    {
        var dry = await PrepareAsync(source, kind, files, options, adjust);
        return await Imports.ImportAsync(dry.Job.Id);
    }

    public Task<ImportJobDetailDto> ImportCostpointAsync(ImportKind kind, string sampleFile, ImportOptionsDto? options = null) =>
        ImportAsync(ImportSource.Costpoint, kind, [Samples.File($"samples/import/costpoint/{sampleFile}")], options);

    /// <summary>Imports every sample Costpoint file, in dependency order.</summary>
    public async Task ImportAllCostpointSamplesAsync()
    {
        foreach (var (kind, file) in Samples.CostpointOrder)
        {
            var job = await ImportCostpointAsync(kind, file);
            Assert.True(job.Job.Status == ImportJobStatus.Imported, $"{file}: {job.Job.Status} {job.Job.Message}");
            Assert.True(job.Job.Counts.Errors == 0, $"{file}: {string.Join("; ", job.Errors.Select(e => $"{e.Row}: {e.Message}"))}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private sealed class NoWaitRetry : SmartsheetRetryPolicy
    {
        public override Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Waits.Add(delay);
            return Task.CompletedTask;
        }

        public static ConcurrentBag<TimeSpan> Waits { get; } = [];
    }
}

/// <summary>Repository files (samples and recorded Smartsheet responses) located from the test output folder.</summary>
internal static class Samples
{
    public static readonly (ImportKind Kind, string File)[] CostpointOrder =
    [
        (ImportKind.OrgUnits, "organizations.csv"),
        (ImportKind.LaborCategories, "labor-categories.csv"),
        (ImportKind.Employees, "employees.csv"),
        (ImportKind.CostRates, "cost-rates.csv"),
        (ImportKind.Customers, "customers.csv"),
        (ImportKind.Contracts, "contracts.csv"),
        (ImportKind.Clins, "clins.csv"),
        (ImportKind.Projects, "projects.csv"),
        (ImportKind.BillRates, "bill-rates.csv"),
        (ImportKind.Budgets, "budgets.csv"),
    ];

    private static readonly Lazy<string> Root = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "Workbench.Server.slnf")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Can't find the repository root.");
    });

    public static string PathOf(string relative) => Path.Combine(Root.Value, relative);

    public static UploadFileRequest File(string relative) => new(Path.GetFileName(relative), System.IO.File.ReadAllBytes(PathOf(relative)));

    public static UploadFileRequest Csv(string fileName, string content) => new(fileName, Encoding.UTF8.GetBytes(content));

    public static string Fixture(string name) => System.IO.File.ReadAllText(PathOf($"tests/Workbench.Tests/Imports/Fixtures/{name}"));
}

/// <summary>
/// A Smartsheet API that answers from recorded JSON fixtures by path. Requests are recorded (with their
/// Authorization header) and responses can be queued ahead to simulate rate limiting.
/// </summary>
internal sealed class FakeSmartsheet : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _routes = new();

    public ConcurrentQueue<HttpStatusCode> Throttle { get; } = new();
    public List<(string Path, string? Auth)> Requests { get; } = [];

    /// <summary>Answers GET <paramref name="pathAndQuery"/> (no leading slash, e.g. <c>2.0/sheets?includeAll=true</c>) with JSON.</summary>
    public void Json(string pathAndQuery, string json) =>
        _routes[pathAndQuery] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public void Bytes(string absoluteUrl, byte[] content) =>
        _routes[absoluteUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var key = uri.Host == "api.smartsheet.com" ? uri.PathAndQuery.TrimStart('/') : uri.ToString();
        lock (Requests)
        {
            Requests.Add((key, request.Headers.Authorization?.ToString()));
        }

        if (Throttle.TryDequeue(out var status))
        {
            var throttled = new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"errorCode":4003,"message":"Rate limit exceeded."}""", Encoding.UTF8, "application/json"),
            };
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(throttled);
        }

        if (uri.Host == "api.smartsheet.com" && request.Headers.Authorization?.Parameter != ImportHost.Token)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"errorCode":1002,"message":"Your Access Token is invalid."}"""),
            });
        }

        return Task.FromResult(_routes.TryGetValue(key, out var route)
            ? route()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($$"""{"errorCode":1006,"message":"Not Found: {{key}}"}""") });
    }
}
