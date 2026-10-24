using System.Net;
using System.Runtime.CompilerServices;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Workbench.Application.Collaboration;
using Object = Google.Apis.Storage.v1.Data.Object;

namespace Workbench.Infrastructure.Storage;

/// <summary>
/// Stores blobs in a Google Cloud Storage bucket (config <c>Storage:Provider=Gcs</c>, <c>Storage:Bucket</c>). Credentials
/// are Application Default Credentials (on Cloud Run, the service's own service account) unless
/// <c>Storage:Gcs:CredentialsFile</c> names a key file. Objects are private: the bucket must use uniform bucket-level
/// access with public access prevention, and downloads stream through the app. Google encrypts every object at rest;
/// <c>Storage:Gcs:KmsKeyName</c> switches to a customer-managed key. Downloads are checked against the object's CRC32C.
/// </summary>
public sealed class GcsBlobStore(StorageClient client, string bucket, string? prefix = null, string? kmsKeyName = null) : IBlobStore, IBlobKeyListing
{
    private readonly string _prefix = prefix ?? string.Empty;

    public static GcsBlobStore Create(BlobStorageSettings settings)
    {
        var builder = new StorageClientBuilder();
        if (!string.IsNullOrWhiteSpace(settings.Gcs.CredentialsFile))
        {
            builder.GoogleCredential = CredentialFactory.FromFile<ServiceAccountCredential>(settings.Gcs.CredentialsFile).ToGoogleCredential();
        }

        if (!string.IsNullOrWhiteSpace(settings.Gcs.BaseUri))
        {
            builder.BaseUri = settings.Gcs.BaseUri;
        }

        return new GcsBlobStore(builder.Build(), settings.Bucket!, settings.Prefix, settings.Gcs.KmsKeyName);
    }

    public async Task<long> SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        var name = NameFor(key);
        var destination = new Object
        {
            Bucket = bucket,
            Name = name,
            ContentType = "application/octet-stream",
            KmsKeyName = string.IsNullOrWhiteSpace(kmsKeyName) ? null : kmsKeyName,
        };

        // An upload is all-or-nothing in GCS: a failed one never leaves a partial object behind.
        var counting = new CountingStream(content);
        var uploaded = await client.UploadObjectAsync(destination, counting, cancellationToken: cancellationToken);
        return uploaded.Size is { } size ? (long)size : counting.BytesRead;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        // Download into a temporary file that disappears when the caller closes it: the SDK checks the CRC32C only
        // once the whole object has arrived, and callers hold the stream while they stream it to the browser.
        var temp = TempFile();
        try
        {
            await client.DownloadObjectAsync(bucket, NameFor(key), temp, cancellationToken: cancellationToken);
            temp.Position = 0;
            return temp;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            await temp.DisposeAsync();
            throw new FileNotFoundException($"Blob '{key}' was not found.");
        }
        catch
        {
            await temp.DisposeAsync();
            throw;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.DeleteObjectAsync(bucket, NameFor(key), cancellationToken: cancellationToken);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async IAsyncEnumerable<string> ListKeysAsync(string prefix = "", [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in client.ListObjectsAsync(bucket, _prefix + prefix).WithCancellation(cancellationToken))
        {
            var key = item.Name[_prefix.Length..];
            if (BlobKey.IsValid(key))
            {
                yield return key;
            }
        }
    }

    private string NameFor(string key)
    {
        BlobKey.Validate(key);
        return _prefix + key;
    }

    internal static FileStream TempFile() => new(
        Path.Combine(Path.GetTempPath(), "workbench-blob-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
}

/// <summary>Counts the bytes read through it, for stores whose upload response doesn't report the size.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Counted(int read)
    {
        BytesRead += read;
        return read;
    }
}
