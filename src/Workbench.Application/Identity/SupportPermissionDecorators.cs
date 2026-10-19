using Workbench.Contracts;
using Workbench.Contracts.Support;

namespace Workbench.Application.Identity;

/// <summary>
/// Support requests: anyone signed in sends one and reads their own; administrators list, read and update every
/// request. Someone else's request is "not found" for everybody else, so its existence isn't revealed.
/// </summary>
internal sealed class PermissionCheckingSupportService(ISupportService inner, AccessControl access) : ISupportService
{
    public Task<SupportRequestDto> SubmitAsync(SubmitSupportRequest request, SupportScreenshot? screenshot = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.SubmitAsync(request, screenshot, cancellationToken);
    }

    public Task<IReadOnlyList<SupportRequestDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListMineAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SupportRequestDto>> ListAllAsync(SupportStatus? status = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.ListAllAsync(status, cancellationToken);
    }

    public async Task<SupportRequestDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var request = await inner.GetAsync(id, cancellationToken);
        if (!string.Equals(request.ReporterEmail, access.Email, StringComparison.OrdinalIgnoreCase) && !await access.IsAdminAsync(cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Support request", id);
        }

        return request;
    }

    public async Task<SupportRequestDto> UpdateAsync(Guid id, UpdateSupportRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.UpdateAsync(id, request, cancellationToken);
    }

    public async Task<SupportScreenshotContent> OpenScreenshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await GetAsync(id, cancellationToken);
        return await inner.OpenScreenshotAsync(id, cancellationToken);
    }

    public string GetScreenshotUrl(Guid id) => inner.GetScreenshotUrl(id);
}
