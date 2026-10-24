using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Workbench.Application.Collaboration;

namespace Workbench.Infrastructure.Storage;

/// <summary>
/// Stores blobs in Amazon S3 or an S3-compatible store such as MinIO (config <c>Storage:Provider=S3</c>,
/// <c>Storage:Bucket</c>, <c>Storage:S3:*</c>). Credentials come from the SDK's default chain (an IAM role) unless
/// static keys are configured. Every object is written with server-side encryption (SSE-S3 by default, or SSE-KMS) and
/// no ACL, so it stays private under the bucket's Block Public Access and Object Ownership settings; downloads stream
/// through the app. Uploads carry a Content-MD5 that S3 checks before storing the object.
/// </summary>
public sealed class S3BlobStore(IAmazonS3 client, string bucket, string? prefix = null, string serverSideEncryption = "AES256", string? kmsKeyId = null)
    : IBlobStore, IBlobKeyListing
{
    private readonly string _prefix = prefix ?? string.Empty;

    public static S3BlobStore Create(BlobStorageSettings settings)
    {
        var s3 = settings.S3;
        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(s3.ServiceUrl))
        {
            config.ServiceURL = s3.ServiceUrl;
            config.ForcePathStyle = s3.ForcePathStyle ?? true;

            // S3-compatible stores don't all accept the SDK's default trailing CRC checksums; Content-MD5 covers integrity.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
            if (!string.IsNullOrWhiteSpace(s3.Region))
            {
                config.AuthenticationRegion = s3.Region;
            }
        }
        else
        {
            config.ForcePathStyle = s3.ForcePathStyle ?? false;
            if (!string.IsNullOrWhiteSpace(s3.Region))
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(s3.Region);
            }
        }

        var client = string.IsNullOrWhiteSpace(s3.AccessKeyId)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(s3.AccessKeyId, s3.SecretAccessKey), config);
        return new S3BlobStore(client, settings.Bucket!, settings.Prefix, s3.ServerSideEncryption, s3.KmsKeyId);
    }

    public async Task<long> SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        var name = NameFor(key);

        // A PUT needs the length up front and a seekable body for retries, so copy unseekable input to a temp file.
        await using var temp = content.CanSeek ? null : GcsBlobStore.TempFile();
        var body = content;
        if (temp is not null)
        {
            await content.CopyToAsync(temp, cancellationToken);
            temp.Position = 0;
            body = temp;
        }

        var start = body.Position;
        var length = body.Length - start;
        var md5 = await Md5Base64Async(body, cancellationToken);
        body.Position = start;

        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = name,
            InputStream = body,
            AutoCloseStream = false,
            ContentType = "application/octet-stream",
            MD5Digest = md5,

            // Sign the whole body in one go rather than in aws-chunked frames: every S3-compatible store accepts it.
            UseChunkEncoding = false,
        };
        request.Headers.ContentLength = length;
        ApplyEncryption(request);
        await client.PutObjectAsync(request, cancellationToken);
        return length;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // The response stream is the object body; disposing it releases the connection.
            var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = NameFor(key) }, cancellationToken);
            return new OwnedResponseStream(response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Blob '{key}' was not found.");
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // S3 answers 204 for missing keys too.
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = NameFor(key) }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async IAsyncEnumerable<string> ListKeysAsync(string prefix = "", [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? token = null;
        do
        {
            var page = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket, Prefix = _prefix + prefix, ContinuationToken = token }, cancellationToken);
            foreach (var item in page.S3Objects ?? [])
            {
                var key = item.Key[_prefix.Length..];
                if (BlobKey.IsValid(key))
                {
                    yield return key;
                }
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }

    private void ApplyEncryption(PutObjectRequest request)
    {
        switch (serverSideEncryption.Trim().ToLowerInvariant())
        {
            case "none":
                break;
            case "aws:kms":
            case "kms":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                if (!string.IsNullOrWhiteSpace(kmsKeyId))
                {
                    request.ServerSideEncryptionKeyManagementServiceKeyId = kmsKeyId;
                }

                break;
            default:
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
                break;
        }
    }

    private string NameFor(string key)
    {
        BlobKey.Validate(key);
        return _prefix + key;
    }

    private static async Task<string> Md5Base64Async(Stream stream, CancellationToken cancellationToken)
    {
        using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            md5.AppendData(buffer, 0, read);
        }

        return Convert.ToBase64String(md5.GetHashAndReset());
    }

    /// <summary>The object body; disposes the whole response (and so the connection) with it.</summary>
    private sealed class OwnedResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.ContentLength;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
