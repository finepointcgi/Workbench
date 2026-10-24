using System.Security.Cryptography;
using Workbench.Application.Collaboration;
using Workbench.Infrastructure.Collaboration;

namespace Workbench.Infrastructure.Storage;

/// <summary>What <see cref="BlobCopy"/> found or did.</summary>
public sealed record BlobCopyReport(
    bool Applied, int Files, long Bytes, int Copied, int AlreadyThere, int ToCopy, IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Failures, IReadOnlyList<string> Stray)
{
    /// <summary>True when nothing is left to do and nothing went wrong (a dry run with files to copy isn't done).</summary>
    public bool Complete => ToCopy == 0 && Conflicts.Count == 0 && Failures.Count == 0;
}

/// <summary>
/// The one-time move from a local folder to a bucket (<c>Workbench.Web --copy-blobs [--apply]</c>). Every file under the
/// folder whose path is a valid key is copied to the same key, tenant prefixes included. A dry run (the default) only
/// compares: each file's SHA-256 against the bucket's copy, if any. With <c>apply</c>, missing files are uploaded and
/// read back, and the copy counts only when the SHA-256 of what came back matches. Keys already in the bucket with
/// different content are conflicts and are left alone: after the switch the bucket is the newer copy. Running it again
/// is safe; it copies only what is still missing. Local files are never deleted.
/// </summary>
public static class BlobCopy
{
    public static async Task<BlobCopyReport> RunAsync(
        LocalDiskBlobStore source, IBlobStore destination, bool apply, TextWriter log, CancellationToken cancellationToken = default)
    {
        int files = 0, copied = 0, alreadyThere = 0, toCopy = 0;
        long bytes = 0;
        List<string> conflicts = [], failures = [];
        var stray = source.ListStrayFiles().ToList();

        var keys = new List<string>();
        await foreach (var key in source.ListKeysAsync(cancellationToken: cancellationToken))
        {
            keys.Add(key);
        }

        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files++;
            (string Hash, long Length) local;
            await using (var stream = await source.OpenReadAsync(key, cancellationToken))
            {
                local = await HashAsync(stream, cancellationToken);
            }

            bytes += local.Length;
            var remote = await TryHashAsync(destination, key, cancellationToken);
            if (remote is { } existing)
            {
                if (existing.Hash == local.Hash)
                {
                    alreadyThere++;
                }
                else
                {
                    conflicts.Add(key);
                    await log.WriteLineAsync($"CONFLICT {key}: the bucket has different content ({existing.Length} bytes, local {local.Length}); left alone.");
                }

                continue;
            }

            if (!apply)
            {
                toCopy++;
                await log.WriteLineAsync($"would copy {key} ({local.Length} bytes, sha256 {local.Hash[..12]}…)");
                continue;
            }

            try
            {
                await using (var stream = await source.OpenReadAsync(key, cancellationToken))
                {
                    await destination.SaveAsync(key, stream, cancellationToken);
                }

                var check = await TryHashAsync(destination, key, cancellationToken);
                if (check?.Hash != local.Hash)
                {
                    failures.Add(key);
                    await log.WriteLineAsync($"FAILED {key}: the copy's checksum doesn't match the local file.");
                    continue;
                }

                copied++;
                await log.WriteLineAsync($"copied {key} ({local.Length} bytes, sha256 verified)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(key);
                await log.WriteLineAsync($"FAILED {key}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var path in stray)
        {
            await log.WriteLineAsync($"skipped {path}: not a blob key (a leftover upload or a file someone added by hand).");
        }

        var report = new BlobCopyReport(apply, files, bytes, copied, alreadyThere, toCopy, conflicts, failures, stray);
        await log.WriteLineAsync(apply
            ? $"{files} files ({bytes} bytes): {copied} copied, {alreadyThere} already in the bucket, {conflicts.Count} conflicts, {failures.Count} failed."
            : $"Dry run. {files} files ({bytes} bytes): {toCopy} to copy, {alreadyThere} already in the bucket, {conflicts.Count} conflicts. Run again with --apply to copy.");
        return report;
    }

    private static async Task<(string Hash, long Length)?> TryHashAsync(IBlobStore store, string key, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await store.OpenReadAsync(key, cancellationToken);
            return await HashAsync(stream, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static async Task<(string Hash, long Length)> HashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            length += read;
        }

        return (Convert.ToHexStringLower(sha.GetHashAndReset()), length);
    }
}
