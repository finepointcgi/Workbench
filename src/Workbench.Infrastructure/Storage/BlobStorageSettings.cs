using Microsoft.Extensions.Configuration;

namespace Workbench.Infrastructure.Storage;

/// <summary>
/// Config section <c>Storage</c>: where attachments, receipts and exports are kept. <c>Local</c> (a folder; development
/// and single-server installs), <c>Gcs</c> (a Google Cloud Storage bucket) or <c>S3</c> (Amazon S3 or an S3-compatible
/// store such as MinIO). Buckets are private: every download streams through the app, which checks permissions first.
/// See docs/operations/object-storage.md.
/// </summary>
public sealed class BlobStorageSettings
{
    public const string SectionName = "Storage";
    public const string DefaultRoot = "App_Data/blobs";

    public string Provider { get; set; } = "Local";

    /// <summary>Local: the folder. Also the source folder of <c>--copy-blobs</c>.</summary>
    public string Root { get; set; } = DefaultRoot;

    /// <summary>Gcs and S3: the bucket name.</summary>
    public string? Bucket { get; set; }

    /// <summary>Optional key prefix inside the bucket, e.g. <c>workbench/</c>, to share a bucket with other data.</summary>
    public string? Prefix { get; set; }

    public GcsSettings Gcs { get; set; } = new();
    public S3Settings S3 { get; set; } = new();

    public BlobStorageProvider ProviderKind => Provider.Trim().ToLowerInvariant() switch
    {
        "" or "local" => BlobStorageProvider.Local,
        "gcs" or "google" => BlobStorageProvider.Gcs,
        "s3" or "minio" => BlobStorageProvider.S3,
        _ => throw new InvalidOperationException($"Unsupported Storage:Provider '{Provider}'. Use Local, Gcs or S3."),
    };

    public static BlobStorageSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = configuration.GetSection(SectionName).Get<BlobStorageSettings>() ?? new BlobStorageSettings();
        if (string.IsNullOrWhiteSpace(settings.Root))
        {
            settings.Root = DefaultRoot;
        }

        return settings;
    }

    /// <summary>Problems that stop a bucket provider from working, for startup and the production configuration check.</summary>
    public IEnumerable<string> Problems()
    {
        BlobStorageProvider kind;
        try
        {
            kind = ProviderKind;
        }
        catch (InvalidOperationException ex)
        {
            return [ex.Message];
        }

        var problems = new List<string>();
        if (kind != BlobStorageProvider.Local && string.IsNullOrWhiteSpace(Bucket))
        {
            problems.Add($"Storage:Provider is {kind}: set Storage:Bucket.");
        }

        if (!string.IsNullOrEmpty(Prefix) && !BlobKey.IsValidPrefix(Prefix))
        {
            problems.Add("Storage:Prefix may only contain letters, digits, '-', '_' and '/' separators, and must end with '/'.");
        }

        if (kind == BlobStorageProvider.S3 && !string.IsNullOrWhiteSpace(S3.AccessKeyId) != !string.IsNullOrWhiteSpace(S3.SecretAccessKey))
        {
            problems.Add("Set both Storage:S3:AccessKeyId and Storage:S3:SecretAccessKey, or neither (to use the IAM role).");
        }

        return problems;
    }
}

public enum BlobStorageProvider
{
    Local,
    Gcs,
    S3,
}

public sealed class GcsSettings
{
    /// <summary>
    /// Optional path to a service account key file. Leave empty to use Application Default Credentials: the Cloud Run or
    /// GKE service account (workload identity), or <c>gcloud auth application-default login</c> on a workstation.
    /// </summary>
    public string? CredentialsFile { get; set; }

    /// <summary>Optional Cloud KMS key (projects/…/cryptoKeys/…) for customer-managed encryption. Google encrypts every object at rest regardless.</summary>
    public string? KmsKeyName { get; set; }

    /// <summary>Tests and emulators only: the JSON API base address, e.g. <c>http://localhost:4443/storage/v1/</c>.</summary>
    public string? BaseUri { get; set; }
}

public sealed class S3Settings
{
    /// <summary>The AWS region, e.g. <c>us-gov-west-1</c>; the SDK default (AWS_REGION) when empty.</summary>
    public string? Region { get; set; }

    /// <summary>An S3-compatible endpoint such as MinIO (<c>https://minio.internal:9000</c>). Empty for Amazon S3.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Path-style addressing (bucket in the path); MinIO needs it. Defaults to true when <see cref="ServiceUrl"/> is set.</summary>
    public bool? ForcePathStyle { get; set; }

    /// <summary>Optional static keys; prefer an IAM role (instance profile, ECS task role, IRSA), which the SDK finds by itself.</summary>
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }

    /// <summary>
    /// <c>AES256</c> (SSE-S3, the default), <c>aws:kms</c> (SSE-KMS, with <see cref="KmsKeyId"/> or the AWS managed key)
    /// or <c>None</c> (only for a MinIO without a KMS; the bucket's own default encryption still applies).
    /// </summary>
    public string ServerSideEncryption { get; set; } = "AES256";

    public string? KmsKeyId { get; set; }
}
