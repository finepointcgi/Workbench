using System.Reflection;

namespace Workbench.Tests.Navigation;

/// <summary>
/// A stand-in for a large service interface: answers the methods it was given by name and throws
/// <see cref="NotSupportedException"/> for the rest.
/// </summary>
public class Stub<T> : DispatchProxy where T : class
{
    private Dictionary<string, Func<object?[]?, object?>> _handlers = [];

    public static T Create(params (string Method, Func<object?[]?, object?> Handler)[] handlers)
    {
        var proxy = Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy)._handlers = handlers.ToDictionary(h => h.Method, h => h.Handler);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod is not null && _handlers.TryGetValue(targetMethod.Name, out var handler)
            ? handler(args)
            : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod?.Name} isn't stubbed.");
}
