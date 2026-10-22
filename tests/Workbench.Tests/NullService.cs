using System.Collections;
using System.Reflection;

namespace Workbench.Tests;

/// <summary>
/// A do-nothing implementation of a service interface for component tests that only need a page to render: every call
/// returns one of the given values when its type fits, else an empty list, a default value or null.
/// </summary>
internal class NullService<T> : DispatchProxy
    where T : class
{
    private object[] _values = [];

    public static T Create(params object[] values)
    {
        var proxy = Create<T, NullService<T>>();
        ((NullService<T>)(object)proxy)._values = values;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Value(inner)]);
        }

        return Value(type);
    }

    private object? Value(Type type)
    {
        if (_values.FirstOrDefault(type.IsInstanceOfType) is { } given)
        {
            return given;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)))
        {
            return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
        }

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
        {
            var element = type.GetGenericArguments()[^1];
            return type.IsInterface
                ? Array.CreateInstance(element, 0)
                : Activator.CreateInstance(type);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
