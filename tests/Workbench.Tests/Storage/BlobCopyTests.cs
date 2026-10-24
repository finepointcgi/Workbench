using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Collaboration;
using Workbench.Infrastructure.Storage;

namespace Workbench.Tests.Storage;

/// <summary><c>--copy-blobs</c>: the one-time move from the local folder into a bucket (here the fake S3).</summary>
public sealed class BlobCopyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-copy-" + Guid.NewGuid().ToString("N"));
    private FakeS3Server _s3 = null!;
    private LocalDiskBlobStore _local = null!;

    public async Task InitializeAsync()
    {
        _s3 = await FakeS3Server.StartNewAsync();
        _local = new LocalDiskBlobStore(_root);
        await _local.SaveAsync("files/receipt/r1", new MemoryStream(BlobStoreContractTests.RandomBytes(3_000)));
        await _local.SaveAsync("sheet1/att1", new MemoryStream([1, 2, 3]));
        await _local.SaveAsync("tenants/acme/sheet2/att2", new MemoryStream([4, 5]));
        await File.WriteAllTextAsync(Path.Combine(_root, "sheet1", "att9.partial"), "leftover");
    }

    public async Task DisposeAsync()
    {
        await _s3.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_dry_run_compares_without_writing()
    {
        var log = new StringWriter();

        var report = await BlobCopy.RunAsync(_local, S3BlobStoreContractTests.Create(_s3), apply: false, log);

        Assert.False(report.Applied);
        Assert.Equal(3, report.Files);
        Assert.Equal(3_005, report.Bytes);
        Assert.Equal(3, report.ToCopy);
        Assert.Equal(0, report.Copied);
        Assert.False(report.Complete);
        Assert.Equal(["sheet1/att9.partial"], report.Stray);
        Assert.Empty(_s3.Objects);
        Assert.DoesNotContain(_s3.Requests, r => r.Method is "PUT" or "DELETE");
        Assert.Contains("would copy tenants/acme/sheet2/att2", log.ToString());
        Assert.Contains("--apply", log.ToString());
    }

    [Fact]
    public async Task Apply_copies_every_key_as_is_verifies_checksums_and_is_safe_to_rerun()
    {
        var bucket = S3BlobStoreContractTests.Create(_s3, prefix: "workbench/");

        var report = await BlobCopy.RunAsync(_local, bucket, apply: true, TextWriter.Null);

        Assert.True(report.Complete);
        Assert.Equal(3, report.Copied);
        Assert.Equal(["workbench/files/receipt/r1", "workbench/sheet1/att1", "workbench/tenants/acme/sheet2/att2"], _s3.Objects.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_root, "files", "receipt", "r1")), _s3.Objects["workbench/files/receipt/r1"]);
        Assert.True(File.Exists(Path.Combine(_root, "sheet1", "att1"))); // The local copy stays until someone removes the volume.

        var again = await BlobCopy.RunAsync(_local, bucket, apply: true, TextWriter.Null);

        Assert.True(again.Complete);
        Assert.Equal(0, again.Copied);
        Assert.Equal(3, again.AlreadyThere);
    }

    [Fact]
    public async Task Different_content_already_in_the_bucket_is_a_conflict_and_left_alone()
    {
        var bucket = S3BlobStoreContractTests.Create(_s3);
        await bucket.SaveAsync("sheet1/att1", new MemoryStream([9, 9]));

        var report = await BlobCopy.RunAsync(_local, bucket, apply: true, TextWriter.Null);

        Assert.Equal(["sheet1/att1"], report.Conflicts);
        Assert.False(report.Complete);
        Assert.Equal([9, 9], _s3.Objects["sheet1/att1"]);
        Assert.Equal(2, report.Copied);
    }

    [Fact]
    public async Task A_copy_whose_checksum_does_not_match_is_a_failure()
    {
        var corrupting = new CorruptingStore();

        var report = await BlobCopy.RunAsync(_local, corrupting, apply: true, TextWriter.Null);

        Assert.Equal(3, report.Failures.Count);
        Assert.Equal(0, report.Copied);
        Assert.False(report.Complete);
    }

    [Fact]
    public async Task The_command_refuses_a_local_provider_and_copies_into_the_configured_bucket()
    {
        var local = Services(new() { ["Storage:Root"] = _root });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Web.Storage.BlobCopyCommand.RunAsync(local, apply: true, TextWriter.Null));

        var s3 = Services(new()
        {
            ["Storage:Provider"] = "S3",
            ["Storage:Root"] = _root,
            ["Storage:Bucket"] = FakeS3Server.Bucket,
            ["Storage:S3:ServiceUrl"] = _s3.BaseAddress.AbsoluteUri,
            ["Storage:S3:Region"] = FakeS3Server.Region,
            ["Storage:S3:AccessKeyId"] = FakeS3Server.AccessKey,
            ["Storage:S3:SecretAccessKey"] = FakeS3Server.SecretKey,
        });
        var report = await Web.Storage.BlobCopyCommand.RunAsync(s3, apply: true, TextWriter.Null);

        Assert.Equal(3, report.Copied);
        Assert.Equal(3, _s3.Objects.Count);
    }

    internal static ServiceProvider Services(Dictionary<string, string?> settings, bool multiTenant = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TenancyOptions { Mode = multiTenant ? TenancyMode.MultiTenant : TenancyMode.SingleTenant });
        services.AddCollaborationInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    /// <summary>Stores something other than what it was given, like a broken proxy.</summary>
    private sealed class CorruptingStore : IBlobStore
    {
        private readonly Dictionary<string, byte[]> _blobs = [];

        public async Task<long> SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            var bytes = copy.ToArray();
            if (bytes.Length > 0)
            {
                bytes[0] ^= 0xFF;
            }

            _blobs[key] = bytes;
            return bytes.Length;
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            _blobs.TryGetValue(key, out var bytes) ? Task.FromResult<Stream>(new MemoryStream(bytes)) : throw new FileNotFoundException(key);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _blobs.Remove(key);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Storage:Provider picks the store; incomplete bucket settings stop startup.</summary>
public sealed class BlobStoreRegistrationTests
{
    [Fact]
    public void Local_is_the_default()
    {
        using var services = BlobCopyTests.Services(new() { ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "wb-" + Guid.NewGuid().ToString("N")) });

        Assert.IsType<LocalDiskBlobStore>(services.GetRequiredService<IBlobStore>());
    }

    [Fact]
    public void S3_is_chosen_by_config_and_tenants_get_their_own_prefix()
    {
        Dictionary<string, string?> settings = new()
        {
            ["Storage:Provider"] = "S3",
            ["Storage:Bucket"] = "b",
            ["Storage:S3:ServiceUrl"] = "http://127.0.0.1:9000",
            ["Storage:S3:AccessKeyId"] = "k",
            ["Storage:S3:SecretAccessKey"] = "s",
        };
        using var single = BlobCopyTests.Services(settings);
        using var multi = BlobCopyTests.Services(settings, multiTenant: true);

        Assert.IsType<S3BlobStore>(single.GetRequiredService<IBlobStore>());
        Assert.IsType<S3BlobStore>(multi.GetRequiredService<UnscopedBlobStore>().Store);
        Assert.IsNotType<S3BlobStore>(multi.GetRequiredService<IBlobStore>());
    }

    [Theory]
    [InlineData("Gcs", null, null, "Storage:Bucket")]
    [InlineData("S3", "b", "only-the-key", "Storage:S3:SecretAccessKey")]
    [InlineData("Azure", "b", null, "Unsupported Storage:Provider")]
    public void Incomplete_settings_stop_startup(string provider, string? bucket, string? accessKey, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BlobCopyTests.Services(new()
        {
            ["Storage:Provider"] = provider,
            ["Storage:Bucket"] = bucket,
            ["Storage:S3:AccessKeyId"] = accessKey,
        }));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_prefix_must_be_a_valid_key_prefix()
    {
        Assert.NotEmpty(new BlobStorageSettings { Provider = "Gcs", Bucket = "b", Prefix = "../x/" }.Problems());
        Assert.NotEmpty(new BlobStorageSettings { Provider = "Gcs", Bucket = "b", Prefix = "nodelimiter" }.Problems());
        Assert.Empty(new BlobStorageSettings { Provider = "Gcs", Bucket = "b", Prefix = "workbench/prod/" }.Problems());
    }

    [Fact]
    public void Production_refuses_unencrypted_S3()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
            ["Storage:Bucket"] = "b",
            ["Storage:S3:ServerSideEncryption"] = "None",
        }).Build();
        var production = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };

        Assert.Contains(Web.Security.ProductionConfigurationCheck.StorageProblems(configuration, production), p => p.Contains("ServerSideEncryption"));
    }
}
