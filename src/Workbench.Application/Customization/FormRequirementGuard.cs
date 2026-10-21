using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Customization;

namespace Workbench.Application.Customization;

/// <summary>
/// The server's half of "required" on a form layout: when a person saves a record, every field their layout makes required
/// must have a value. The value is read from the save request's property with the field's key (see
/// <see cref="BuiltInForms"/>). It only adds requirements: the service's own rules still run after it, and with the
/// built-in layout it checks nothing. Jobs and the system user aren't filling in a form, so they aren't checked.
/// </summary>
public sealed class FormRequirementGuard(FormLayoutService layouts, ICustomizationAccess access)
{
    /// <summary>Which form each save request belongs to.</summary>
    public static IReadOnlyDictionary<Type, string> EntityByRequest { get; } =
        BuiltInForms.All.SelectMany(f => f.RequestTypes.Select(t => (t, f.EntityType))).ToDictionary(x => x.t, x => x.EntityType);

    public async Task EnsureAsync(string entityType, object request, CancellationToken ct)
    {
        var caller = await access.GetCallerAsync(ct);
        if (caller.Email.Length == 0)
        {
            return;
        }

        var layout = await layouts.GetEffectiveAsync(entityType, ct);
        if (layout.IsBuiltIn)
        {
            return;
        }

        var missing = Missing(layout, request);
        if (missing.Count > 0)
        {
            throw new ArgumentException($"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} required.");
        }
    }

    /// <summary>Labels of fields the layout makes required that the request leaves blank (built-in fields; custom ones are the catalog's).</summary>
    public static IReadOnlyList<string> Missing(EffectiveFormLayout layout, object request)
    {
        var properties = request.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        return layout.Sections.SelectMany(s => s.Fields)
            .Where(f => f.Required && !f.DomainRequired && !f.IsCustom)
            .Where(f => properties.TryGetValue(f.Key, out var p) && IsBlank(p.GetValue(request)))
            .Select(f => f.Label)
            .ToList();
    }

    public static bool IsBlank(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        Guid g => g == Guid.Empty,
        ICollection c => c.Count == 0,
        IEnumerable e and not string => !e.Cast<object>().Any(),
        _ => false,
    };
}

/// <summary>
/// Wraps a service so that every call taking one of the form save requests (<see cref="FormRequirementGuard.EntityByRequest"/>)
/// is checked by <see cref="FormRequirementGuard"/> first. Previews aren't saves and aren't checked. One proxy instead of a
/// hand-written decorator per service keeps the eleven save paths identical.
/// </summary>
public class FormRequirementProxy<T> : DispatchProxy
    where T : class
{
    private T _inner = default!;
    private IServiceProvider _services = default!;

    public static T Create(T inner, IServiceProvider services)
    {
        var proxy = Create<T, FormRequirementProxy<T>>();
        var self = (FormRequirementProxy<T>)(object)proxy;
        self._inner = inner;
        self._services = services;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        var request = method.Name.StartsWith("Preview", StringComparison.Ordinal)
            ? null
            : args?.FirstOrDefault(a => a is not null && FormRequirementGuard.EntityByRequest.ContainsKey(a.GetType()));
        if (request is null)
        {
            return Call(method, args);
        }

        var entityType = FormRequirementGuard.EntityByRequest[request.GetType()];
        var ct = args!.OfType<CancellationToken>().FirstOrDefault();
        if (method.ReturnType == typeof(Task))
        {
            return GuardedAsync(entityType, request, ct, method, args);
        }

        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return typeof(FormRequirementProxy<T>).GetMethod(nameof(GuardedResultAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0])
                .Invoke(this, [entityType, request, ct, method, args]);
        }

        return Call(method, args);
    }

    private async Task GuardedAsync(string entityType, object request, CancellationToken ct, MethodInfo method, object?[]? args)
    {
        await _services.GetRequiredService<FormRequirementGuard>().EnsureAsync(entityType, request, ct);
        await (Task)Call(method, args)!;
    }

    private async Task<TResult> GuardedResultAsync<TResult>(string entityType, object request, CancellationToken ct, MethodInfo method, object?[]? args)
    {
        await _services.GetRequiredService<FormRequirementGuard>().EnsureAsync(entityType, request, ct);
        return await (Task<TResult>)Call(method, args)!;
    }

    private object? Call(MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
}
