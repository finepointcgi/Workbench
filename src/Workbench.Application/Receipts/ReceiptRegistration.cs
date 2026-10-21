using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Files;
using Workbench.Contracts.Receipts;

namespace Workbench.Application.Receipts;

public static class ReceiptRegistration
{
    /// <summary>
    /// The receipt library ("My receipts") and receipt reading (PDF text layer; the AI assistant only when an admin turned it
    /// on). Its permission decorator is added by <c>AddWorkbenchIdentity</c>.
    /// </summary>
    public static IServiceCollection AddWorkbenchReceipts(this IServiceCollection services)
    {
        services.TryAddScoped<FileStorage>();
        services.AddScoped<ReceiptReader>();
        services.AddScoped<IReceiptLibraryService, ReceiptLibraryService>();
        return services;
    }
}
