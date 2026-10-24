using Workbench.Infrastructure.Collaboration;
using Workbench.Infrastructure.Storage;

namespace Workbench.Web.Storage;

/// <summary><c>Workbench.Web --copy-blobs [--apply]</c>: copies Storage:Root into the bucket (<see cref="BlobCopy"/>).</summary>
public static class BlobCopyCommand
{
    public static async Task<BlobCopyReport> RunAsync(IServiceProvider services, bool apply, TextWriter output, CancellationToken cancellationToken = default)
    {
        var settings = services.GetRequiredService<BlobStorageSettings>();
        if (settings.ProviderKind == BlobStorageProvider.Local)
        {
            throw new InvalidOperationException("Storage:Provider is Local. Set it to Gcs or S3 (and Storage:Bucket) before copying files into a bucket.");
        }

        if (!Directory.Exists(settings.Root))
        {
            throw new InvalidOperationException($"The local folder {Path.GetFullPath(settings.Root)} (Storage:Root) doesn't exist.");
        }

        var source = new LocalDiskBlobStore(settings.Root);
        var destination = services.GetRequiredService<UnscopedBlobStore>().Store;
        await output.WriteLineAsync($"{(apply ? "Copying" : "Dry run: comparing")} {Path.GetFullPath(settings.Root)} to {settings.ProviderKind} bucket {settings.Bucket}{(string.IsNullOrEmpty(settings.Prefix) ? "" : "/" + settings.Prefix)}");
        return await BlobCopy.RunAsync(source, destination, apply, output, cancellationToken);
    }
}
