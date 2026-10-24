using System.Runtime.CompilerServices;
using Workbench.Application.Collaboration;
using Workbench.Infrastructure.Storage;

namespace Workbench.Infrastructure.Collaboration;

/// <summary>Stores blobs as files under a root folder (config <c>Storage:Root</c>, default <c>App_Data/blobs</c>).</summary>
public sealed class LocalDiskBlobStore : IBlobStore, IBlobKeyListing
{
    private readonly string _root;

    public LocalDiskBlobStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<long> SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp file first so a failed or oversized upload never leaves a partial blob behind.
        var temp = path + ".partial";
        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
            return new FileInfo(path).Length;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Blob '{key}' was not found.");
        }

        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>Every file under the root whose relative path is a valid key (leftover <c>.partial</c> files and strays are skipped).</summary>
    public async IAsyncEnumerable<string> ListKeysAsync(string prefix = "", [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (BlobKey.IsValid(key) && key.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return key;
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>Files under the root that aren't valid keys, so <c>--copy-blobs</c> can report them instead of silently skipping.</summary>
    public IEnumerable<string> ListStrayFiles() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(key => !BlobKey.IsValid(key));

    private string PathFor(string key)
    {
        // Keys are app-generated, but refuse anything that could escape the root anyway.
        BlobKey.Validate(key);
        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }
}
