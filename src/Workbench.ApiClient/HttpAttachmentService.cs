using System.Net.Http.Headers;
using Workbench.Contracts.Collaboration;

namespace Workbench.ApiClient;

public sealed class HttpAttachmentService(HttpClient http) : IAttachmentService
{
    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/items/{itemId}/attachments", cancellationToken);
        return await response.ReadAsync<List<AttachmentDto>>(cancellationToken);
    }

    public async Task<AttachmentDto> UploadAsync(Guid sheetId, Guid itemId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(AttachmentRules.ContentTypeFor(fileName));
        form.Add(file, "file", fileName);
        using var response = await http.PostAsync($"api/v1/sheets/{sheetId}/items/{itemId}/attachments", form, cancellationToken);
        return await response.ReadAsync<AttachmentDto>(cancellationToken);
    }

    public async Task<AttachmentContent> DownloadAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(GetDownloadUrl(sheetId, attachmentId), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            await response.EnsureOkAsync(cancellationToken);
            var headers = response.Content.Headers;
            var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? attachmentId.ToString();
            var attachment = new AttachmentDto(attachmentId, Guid.Empty, fileName,
                headers.ContentType?.MediaType ?? AttachmentRules.ContentTypeFor(fileName), headers.ContentLength ?? 0, string.Empty, default);
            return new AttachmentContent(attachment, new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken)));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public string GetDownloadUrl(Guid sheetId, Guid attachmentId) =>
        new Uri(http.BaseAddress!, $"api/v1/sheets/{sheetId}/attachments/{attachmentId}/content").ToString();

    public async Task DeleteAsync(Guid sheetId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/attachments/{attachmentId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    /// <summary>Keeps the response alive until the caller is done reading.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
