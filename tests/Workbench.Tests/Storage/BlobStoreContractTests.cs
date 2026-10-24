using Amazon.Runtime;
using Amazon.S3;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Workbench.Application.Collaboration;
using Workbench.Infrastructure.Collaboration;
using Workbench.Infrastructure.Storage;

namespace Workbench.Tests.Storage;

/// <summary>
/// The behaviour every <see cref="IBlobStore"/> must have, run against the local folder, S3 (through the real AWS SDK
/// to a fake S3 that checks SigV4 signatures) and Google Cloud Storage (the real Google SDK to a fake JSON API).
/// </summary>
public abstract class BlobStoreContractTests : IAsyncLifetime
{
    protected IBlobStore Store { get; set; } = null!;

    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    [Fact]
    public async Task Saves_reads_back_and_reports_the_size()
    {
        var bytes = RandomBytes(70_000);

        var size = await Store.SaveAsync("files/receipt/abc123", new MemoryStream(bytes));

        Assert.Equal(bytes.Length, size);
        Assert.Equal(bytes, await ReadAsync("files/receipt/abc123"));
    }

    [Fact]
    public async Task Saving_again_replaces_the_blob()
    {
        await Store.SaveAsync("sheet1/att1", new MemoryStream([1, 2, 3]));
        await Store.SaveAsync("sheet1/att1", new MemoryStream([9]));

        Assert.Equal([9], await ReadAsync("sheet1/att1"));
    }

    [Fact]
    public async Task Accepts_streams_that_cannot_seek_and_empty_blobs()
    {
        var bytes = RandomBytes(5_000);

        Assert.Equal(bytes.Length, await Store.SaveAsync("sheet1/unseekable", new ForwardOnlyStream(bytes)));
        Assert.Equal(0, await Store.SaveAsync("sheet1/empty", new MemoryStream()));

        Assert.Equal(bytes, await ReadAsync("sheet1/unseekable"));
        Assert.Empty(await ReadAsync("sheet1/empty"));
    }

    [Fact]
    public async Task Missing_blobs_throw_FileNotFound_and_deleting_them_is_fine()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Store.OpenReadAsync("sheet1/missing"));
        await Store.DeleteAsync("sheet1/missing");
    }

    [Fact]
    public async Task Delete_removes_the_blob()
    {
        await Store.SaveAsync("sheet1/gone", new MemoryStream([1]));

        await Store.DeleteAsync("sheet1/gone");

        await Assert.ThrowsAsync<FileNotFoundException>(() => Store.OpenReadAsync("sheet1/gone"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("")]
    public async Task Rejects_keys_that_could_escape_the_root_or_prefix(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(key, new MemoryStream([1])));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.OpenReadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeleteAsync(key));
    }

    [Fact]
    public async Task Lists_keys_under_a_prefix()
    {
        foreach (var key in new[] { "tenants/acme/a/1", "tenants/acme/b/2", "tenants/beta/a/1", "files/x/3" })
        {
            await Store.SaveAsync(key, new MemoryStream([1]));
        }

        var listing = Assert.IsAssignableFrom<IBlobKeyListing>(Store);
        Assert.Equal(["tenants/acme/a/1", "tenants/acme/b/2"], (await ListAsync(listing, "tenants/acme/")).Order(StringComparer.Ordinal));
        Assert.Equal(4, (await ListAsync(listing, "")).Count);
    }

    protected async Task<byte[]> ReadAsync(string key)
    {
        await using var stream = await Store.OpenReadAsync(key);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    internal static async Task<List<string>> ListAsync(IBlobKeyListing listing, string prefix)
    {
        var keys = new List<string>();
        await foreach (var key in listing.ListKeysAsync(prefix))
        {
            keys.Add(key);
        }

        return keys;
    }

    internal static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Like a request body: readable once, front to back, length unknown.</summary>
    internal sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class LocalDiskBlobStoreContractTests : BlobStoreContractTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-blobs-" + Guid.NewGuid().ToString("N"));

    public override Task InitializeAsync()
    {
        Store = new LocalDiskBlobStore(_root);
        return Task.CompletedTask;
    }

    public override Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }
}

public sealed class S3BlobStoreContractTests : BlobStoreContractTests
{
    internal FakeS3Server Server { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        Server = await FakeS3Server.StartNewAsync();
        Server.PageSize = 2; // Listing must follow continuation tokens.
        Store = Create(Server);
    }

    public override async Task DisposeAsync() => await Server.DisposeAsync();

    internal static S3BlobStore Create(FakeS3Server server, string? prefix = null, string encryption = "AES256", string? kmsKeyId = null) =>
        S3BlobStore.Create(new BlobStorageSettings
        {
            Provider = "S3",
            Bucket = FakeS3Server.Bucket,
            Prefix = prefix,
            S3 = new S3Settings
            {
                ServiceUrl = server.BaseAddress.AbsoluteUri.TrimEnd('/'),
                Region = FakeS3Server.Region,
                AccessKeyId = FakeS3Server.AccessKey,
                SecretAccessKey = FakeS3Server.SecretKey,
                ServerSideEncryption = encryption,
                KmsKeyId = kmsKeyId,
            },
        });

    [Fact]
    public async Task Every_request_is_signed_with_SigV4_and_uploads_are_encrypted_private_and_checksummed()
    {
        await Store.SaveAsync("files/receipt/r1", new MemoryStream(RandomBytes(1_000)));
        await ReadAsync("files/receipt/r1");
        await Store.DeleteAsync("files/receipt/r1");

        Assert.Empty(Server.SignatureFailures);
        Assert.All(Server.Requests, r => Assert.StartsWith($"AWS4-HMAC-SHA256 Credential={FakeS3Server.AccessKey}/", r.Header("authorization")));
        var put = Assert.Single(Server.Requests, r => r.Method == "PUT");
        Assert.Equal("AES256", put.Header("x-amz-server-side-encryption"));
        Assert.Null(put.Header("x-amz-acl")); // No ACL: objects stay private under Block Public Access.
        Assert.NotNull(put.Header("content-md5"));
        Assert.Contains("x-amz-content-sha256", put.Header("authorization")); // The body hash is part of the signature.
    }

    [Fact]
    public async Task A_wrong_secret_key_is_refused_by_the_signature_check()
    {
        var wrong = new S3BlobStore(
            new AmazonS3Client(new BasicAWSCredentials(FakeS3Server.AccessKey, "not-the-secret"),
                new AmazonS3Config { ServiceURL = Server.BaseAddress.AbsoluteUri, ForcePathStyle = true, AuthenticationRegion = FakeS3Server.Region }),
            FakeS3Server.Bucket);

        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => wrong.SaveAsync("sheet1/x", new MemoryStream([1])));

        Assert.Equal("SignatureDoesNotMatch", error.ErrorCode);
        Assert.Empty(Server.Objects);
    }

    [Fact]
    public async Task Kms_encryption_names_the_key_and_the_prefix_scopes_every_key()
    {
        var store = Create(Server, prefix: "workbench/", encryption: "aws:kms", kmsKeyId: "arn:aws:kms:us-east-1:111122223333:key/test");

        await store.SaveAsync("sheet1/a", new MemoryStream([7]));

        var put = Assert.Single(Server.Requests, r => r.Method == "PUT");
        Assert.Equal("aws:kms", put.Header("x-amz-server-side-encryption"));
        Assert.Equal("arn:aws:kms:us-east-1:111122223333:key/test", put.Header("x-amz-server-side-encryption-aws-kms-key-id"));
        Assert.Equal(["workbench/sheet1/a"], Server.Objects.Keys);
        Assert.Equal(["sheet1/a"], await ListAsync(store, ""));
    }
}

public sealed class GcsBlobStoreContractTests : BlobStoreContractTests
{
    private const string Token = "ya29.test-token";

    internal FakeGcsServer Server { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        Server = await FakeGcsServer.StartNewAsync();
        Server.PageSize = 2; // Listing must follow page tokens.
        Store = Create(Server);
    }

    public override async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        Assert.True(Server.Errors.IsEmpty, string.Join("\n", Server.Errors));
    }

    internal static GcsBlobStore Create(FakeGcsServer server, string? prefix = null, string? kmsKeyName = null) => new(
        new StorageClientBuilder { BaseUri = server.StorageBaseUri, GoogleCredential = GoogleCredential.FromAccessToken(Token) }.Build(),
        FakeGcsServer.Bucket, prefix, kmsKeyName);

    [Fact]
    public async Task Requests_carry_the_service_account_token_and_uploads_name_the_kms_key()
    {
        var store = Create(Server, prefix: "workbench/", kmsKeyName: "projects/p/locations/l/keyRings/r/cryptoKeys/k");

        await store.SaveAsync("sheet1/a", new MemoryStream([1, 2]));
        await store.DeleteAsync("sheet1/a");

        Assert.All(Server.Requests.Where(r => !r.RawTarget.Contains("upload_id")), r => Assert.Equal($"Bearer {Token}", r.Header("authorization")));
        Assert.Equal("projects/p/locations/l/keyRings/r/cryptoKeys/k", Server.Metadata["workbench/sheet1/a"].GetProperty("kmsKeyName").GetString());
        Assert.False(Server.Metadata["workbench/sheet1/a"].TryGetProperty("acl", out _)); // No object ACLs: uniform bucket-level access.
        Assert.Empty(Server.Objects);
    }
}
