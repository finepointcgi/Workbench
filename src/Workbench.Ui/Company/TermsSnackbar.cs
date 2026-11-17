using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Workbench.Contracts.Company;

namespace Workbench.Ui.Company;

/// <summary>
/// MudBlazor's snackbar with the company type's words applied to text messages, so a validation message thrown by a
/// service ("This ceiling was deactivated") reads "not-to-exceed" for a professional-services firm without every page
/// rewording <c>ex.Message</c>. <c>MainLayout</c> sets <see cref="Terms"/> once it has read the company type.
/// </summary>
public sealed class TermsSnackbar(ISnackbar inner) : ISnackbar, IDisposable
{
    public ITerms? Terms { get; set; }

    public IEnumerable<Snackbar> ShownSnackbars => inner.ShownSnackbars;

    public SnackbarConfiguration Configuration => inner.Configuration;

    public event Action? OnSnackbarsUpdated
    {
        add => inner.OnSnackbarsUpdated += value;
        remove => inner.OnSnackbarsUpdated -= value;
    }

    public Snackbar? Add(string message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) =>
        inner.Add(Terms.Reword(message), severity, configure, key ?? message);

    public Snackbar? Add(MarkupString message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) =>
        inner.Add(message, severity, configure, key);

    public Snackbar? Add(RenderFragment message, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) =>
        inner.Add(message, severity, configure, key);

    public Snackbar? Add<T>(Dictionary<string, object>? componentParameters = null, Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null, string? key = null) where T : IComponent =>
        inner.Add<T>(componentParameters, severity, configure, key);

    public void Clear() => inner.Clear();

    public void Remove(Snackbar snackbar) => inner.Remove(snackbar);

    public void RemoveByKey(string key) => inner.RemoveByKey(key);

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

public static class TermsSnackbarRegistration
{
    /// <summary>Call after <c>AddMudServices()</c>: puts the company type's words on snackbar text.</summary>
    public static IServiceCollection AddCompanyWordsInSnackbars(this IServiceCollection services)
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(ISnackbar));
        if (original?.ImplementationType is not { } implementation)
        {
            return services;
        }

        services.Remove(original);
        services.Add(new ServiceDescriptor(implementation, implementation, original.Lifetime));
        services.Add(new ServiceDescriptor(typeof(ISnackbar), sp => new TermsSnackbar((ISnackbar)sp.GetRequiredService(implementation)), original.Lifetime));
        return services;
    }
}
