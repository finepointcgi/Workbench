using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Licensing;

namespace Workbench.Tests;

internal static class TestEdition
{
    /// <summary>Test hosts run with every Business feature on, so tests of those features don't each need a license. Licensing tests replace this.</summary>
    public static IServiceCollection UseBusinessEdition(this IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Singleton(new LicenseOptions { EditionOverride = "Business" }));
}
