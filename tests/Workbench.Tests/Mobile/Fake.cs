using System.Reflection;

namespace Workbench.Tests.Mobile;

/// <summary>
/// A stand-in for a service interface: each method answers with the handler set by name (<see cref="On"/>), and every
/// call is recorded. Calling a method without a handler throws, so a test notices calls it didn't expect.
/// </summary>
public class Fake<T> : DispatchProxy where T : class
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = [];

    public List<(string Method, object?[] Args)> Calls { get; } = [];

    public static (T Service, Fake<T> Fake) Create()
    {
        var service = DispatchProxy.Create<T, Fake<T>>();
        return (service, (Fake<T>)(object)service);
    }

    /// <summary>Answers <paramref name="method"/> with the handler's result, wrapped in a task when the method is async.</summary>
    public Fake<T> On(string method, Func<object?[], object?> handler)
    {
        _handlers[method] = handler;
        return this;
    }

    public Fake<T> Returns(string method, object? value) => On(method, _ => value);

    public object?[] Single(string method) => Assert.Single(Calls, c => c.Method == method).Args;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        args ??= [];
        Calls.Add((method.Name, args));
        if (!_handlers.TryGetValue(method.Name, out var handler))
        {
            throw new NotSupportedException($"{typeof(T).Name}.{method.Name} wasn't expected.");
        }

        var returnType = method.ReturnType;
        object? result;
        try
        {
            result = handler(args);
        }
        catch (Exception ex) when (returnType == typeof(Task) || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
        {
            return returnType == typeof(Task)
                ? Task.FromException(ex)
                : typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [ex]);
        }

        if (returnType == typeof(Task))
        {
            return result as Task ?? Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            if (result is Task task && returnType.IsInstanceOfType(task))
            {
                return task;
            }

            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [result]);
        }

        return result;
    }
}
