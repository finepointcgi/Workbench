using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Support;
using Workbench.Ui.Help;
using Workbench.Ui.Support;

namespace Workbench.Tests.Support;

public sealed class SupportFormTests
{
    [Fact]
    public async Task The_form_sends_what_was_typed_plus_the_page_path_and_version_only()
    {
        var service = new RecordingSupport();
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ISupportService>(service);
        var cut = ctx.Render<SupportForm>(p => p.Add(x => x.PagePath, "billing/invoices/42?search=acme&token=abc#lines"));

        Assert.Contains("/billing/invoices/42", cut.Find("[data-testid='support-captured']").TextContent);
        Assert.DoesNotContain("token", cut.Find("[data-testid='support-captured']").TextContent);

        // Nothing typed: refused with a message, nothing sent.
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => cut.Find("[data-testid='support-error']"));
        Assert.Null(service.Last);

        cut.Find("[data-testid='support-summary']").Input("Invoice total is wrong");
        cut.Find("[data-testid='support-details']").Change("It doubled the ODC line.");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("SR-000007", cut.Find("[data-testid='support-sent']").TextContent));
        var sent = service.Last!;
        Assert.Equal("Invoice total is wrong", sent.Summary);
        Assert.Equal("It doubled the ODC line.", sent.Details);
        Assert.Equal("/billing/invoices/42", sent.PagePath);
        Assert.Equal(AppInfo.Version, sent.AppVersion);
        Assert.False(sent.FromError);
    }

    private sealed class RecordingSupport : ISupportService
    {
        public SubmitSupportRequest? Last { get; private set; }

        public Task<SupportRequestDto> SubmitAsync(SubmitSupportRequest request, SupportScreenshot? screenshot = null, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(new SupportRequestDto(Guid.NewGuid(), 7, request.Kind, SupportStatus.New, request.Summary, request.Details, request.PagePath,
                request.AppVersion, request.FromError, "a@example.com", "A", null, null, null, DateTime.UtcNow, DateTime.UtcNow));
        }

        public Task<IReadOnlyList<SupportRequestDto>> ListMineAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SupportRequestDto>>([]);

        public Task<IReadOnlyList<SupportRequestDto>> ListAllAsync(SupportStatus? status = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SupportRequestDto> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SupportRequestDto> UpdateAsync(Guid id, UpdateSupportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SupportScreenshotContent> OpenScreenshotAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string GetScreenshotUrl(Guid id) => $"api/v1/support/requests/{id}/screenshot";
    }
}
