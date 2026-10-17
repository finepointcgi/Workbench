using System.Text.RegularExpressions;
using Workbench.Application.Collaboration;

namespace Workbench.Infrastructure.Collaboration;

/// <summary>Stores blobs as files under a root folder (config <c>Storage:Root</c>, default <c>App_Data/blobs</c>).</summary>
public sealed partial class LocalDiskBlobStore : IBlobStore
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
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        // Keys are app-generated, but refuse anything that could escape the root anyway.
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"Invalid blob key '{key}'.", nameof(key));
        }

        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$")]
    private static partial Regex KeyPattern();
}
