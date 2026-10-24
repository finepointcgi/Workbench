using System.Text.RegularExpressions;

namespace Workbench.Infrastructure.Storage;

/// <summary>
/// The shape every blob key must have, in every store: app-generated segments of letters, digits, '-' and '_' joined by
/// '/'. Refusing anything else keeps keys from escaping the local root or a bucket prefix.
/// </summary>
public static partial class BlobKey
{
    public static bool IsValid(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static void Validate(string key)
    {
        if (!IsValid(key))
        {
            throw new ArgumentException($"Invalid blob key '{key}'.", nameof(key));
        }
    }

    public static bool IsValidPrefix(string prefix) => prefix.EndsWith('/') && IsValid(prefix[..^1]);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$")]
    private static partial Regex KeyPattern();
}

/// <summary>A blob store whose keys can be listed: used by <c>--copy-blobs</c> and the demo reset.</summary>
public interface IBlobKeyListing
{
    /// <summary>Every key under <paramref name="prefix"/> ("" for all), in no particular order.</summary>
    IAsyncEnumerable<string> ListKeysAsync(string prefix = "", CancellationToken cancellationToken = default);
}
