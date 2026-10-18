using Workbench.Infrastructure.Collaboration;

namespace Workbench.Tests.Collaboration;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-blobs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Local_disk_store_saves_opens_and_deletes()
    {
        var store = new LocalDiskBlobStore(_root);

        var size = await store.SaveAsync("sheet/file", new MemoryStream([1, 2, 3]));
        await using (var stream = await store.OpenReadAsync("sheet/file"))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal([1, 2, 3], copy.ToArray());
        }

        await store.DeleteAsync("sheet/file");

        Assert.Equal(3, size);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync("sheet/file"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("a\\b")]
    public async Task Local_disk_store_rejects_keys_that_could_escape_the_root(string key)
    {
        var store = new LocalDiskBlobStore(_root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(key, new MemoryStream([1])));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
