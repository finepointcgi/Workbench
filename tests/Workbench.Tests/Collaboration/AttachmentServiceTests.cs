using System.Text;
using Workbench.Contracts.Collaboration;

namespace Workbench.Tests.Collaboration;

public sealed class AttachmentServiceTests
{
    [Fact]
    public async Task Upload_then_download_round_trips_and_uses_server_content_type()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync("Requirements");

        var uploaded = await host.Attachments.UploadAsync(sheetId, item.Id, @"C:\fakepath\notes.txt", new MemoryStream("hello"u8.ToArray()));

        Assert.Equal("notes.txt", uploaded.FileName);
        Assert.Equal("text/plain", uploaded.ContentType);
        Assert.Equal(5, uploaded.SizeBytes);
        await using var content = await host.Attachments.DownloadAsync(sheetId, uploaded.Id);
        Assert.Equal("hello", await new StreamReader(content.Content, Encoding.UTF8).ReadToEndAsync());
        Assert.Equal("Attached \"notes.txt\" to \"Requirements\"", (await host.Activity.ListAsync(sheetId))[0].Summary);
    }

    [Theory]
    [InlineData("run.exe")]
    [InlineData("page.html")]
    [InlineData("image.svg")]
    [InlineData("noextension")]
    public async Task Rejects_extensions_outside_the_allow_list(string fileName)
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Attachments.UploadAsync(sheetId, item.Id, fileName, new MemoryStream([1])));
        Assert.Empty(host.Blobs.Blobs);
    }

    [Fact]
    public async Task Rejects_files_over_25_MB_even_when_the_length_is_unknown()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Attachments.UploadAsync(sheetId, item.Id, "big.pdf", new NonSeekableStream(AttachmentRules.MaxSizeBytes + 1)));

        Assert.Empty(host.Blobs.Blobs);
        Assert.Empty(await host.Attachments.ListAsync(sheetId, item.Id));
    }

    [Fact]
    public async Task Accepts_exactly_25_MB()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();

        var uploaded = await host.Attachments.UploadAsync(sheetId, item.Id, "big.pdf", new NonSeekableStream(AttachmentRules.MaxSizeBytes));

        Assert.Equal(AttachmentRules.MaxSizeBytes, uploaded.SizeBytes);
    }

    [Fact]
    public async Task Delete_removes_the_blob()
    {
        await using var host = new CollaborationHost();
        var (sheetId, item) = await host.NewSheetWithItemAsync();
        var uploaded = await host.Attachments.UploadAsync(sheetId, item.Id, "a.csv", new MemoryStream([1, 2, 3]));

        await host.Attachments.DeleteAsync(sheetId, uploaded.Id);

        Assert.Empty(host.Blobs.Blobs);
        Assert.Empty(await host.Attachments.ListAsync(sheetId, item.Id));
        Assert.Contains(host.Changes, c => c.Kind == SheetChangeKind.AttachmentDeleted);
    }

    private sealed class NonSeekableStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _remaining);
            _remaining -= n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
