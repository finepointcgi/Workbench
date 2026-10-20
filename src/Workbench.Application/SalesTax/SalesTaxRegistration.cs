using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.SalesTax;

namespace Workbench.Application.SalesTax;

public static class SalesTaxRegistration
{
    /// <summary>
    /// Sales and use tax: rate tables, tax codes, customer exemptions, invoice sales tax (applied at approval through
    /// <see cref="InvoiceTaxApplier"/>), use tax on posted AP bills (<see cref="UseTaxAccrual"/>) and the return. The engine is
    /// the rate tables unless a host registers another <see cref="ISalesTaxEngine"/> first (TryAdd); nothing calls out by default.
    /// Call before AddWorkbenchIdentity, which adds the permission decorators (<c>AddWorkbenchSalesTaxPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchSalesTax(this IServiceCollection services)
    {
        services.TryAddScoped<ISalesTaxEngine, TableSalesTaxEngine>();
        services.AddScoped<InvoiceTaxApplier>();
        services.AddScoped<UseTaxAccrual>();
        services.AddScoped<ISalesTaxService, SalesTaxService>();
        return services;
    }
}
