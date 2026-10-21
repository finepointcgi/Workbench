using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

/// <summary>
/// Masks <see cref="SensitiveFieldAttribute"/> properties the caller may not see: null, or zero for a non-nullable number.
/// Walks nested contract records and lists, and copies only what changes (records are cloned, never mutated). Permission
/// decorators call it on what they return, so the UI, the API, exports built from those reads and the assistant's tools
/// all get the masked copy. A relationship that reveals a field (a project's manager, the employee themself) is passed
/// in by the caller as an extra grant.
/// </summary>
public static class FieldMasker
{
    // Per-type reflection plans. Types never change at run time, so this cache holds no tenant or user state.
    private static readonly ConcurrentDictionary<Type, Plan> Plans = new();

    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary><paramref name="value"/> with every field <paramref name="granted"/> doesn't reveal masked.</summary>
    public static T Mask<T>(T value, PermissionSet granted) => value is null ? value : (T)MaskObject(value, granted)!;

    /// <summary>The sensitive fields a type carries (deeply), as "Property" → permission key.</summary>
    public static IReadOnlyList<(string Field, string Permission)> FieldsOf(Type type)
    {
        var result = new List<(string, string)>();
        Collect(type, result, []);
        return result;
    }

    /// <summary>The permissions <paramref name="granted"/> lacks for fields of <paramref name="type"/>: what a page should say is hidden.</summary>
    public static IReadOnlyList<string> WithheldPermissions(Type type, PermissionSet granted) =>
        FieldsOf(type).Select(f => f.Permission).Distinct().Where(p => !granted.Has(p)).ToList();

    private static void Collect(Type type, List<(string, string)> result, HashSet<Type> seen)
    {
        if (!seen.Add(type))
        {
            return;
        }

        var plan = PlanOf(type);
        result.AddRange(plan.Sensitive.Select(s => (s.Property.Name, s.Permission)));
        foreach (var nested in plan.Nested)
        {
            Collect(ElementOrSelf(nested.PropertyType), result, seen);
        }
    }

    private static object? MaskObject(object? value, PermissionSet granted)
    {
        if (value is null || value is string || granted.IsEverything)
        {
            return value;
        }

        var type = value.GetType();
        if (value is IEnumerable sequence && ElementTypeOf(type) is { } element)
        {
            return PlanOf(element).IsEmpty ? value : MaskSequence(sequence, type, element, granted);
        }

        var plan = PlanOf(type);
        if (plan.IsEmpty)
        {
            return value;
        }

        object? copy = null;
        foreach (var field in plan.Sensitive.Where(f => !granted.Has(f.Permission)))
        {
            copy ??= Clone(value);
            Set(copy, field.Property, field.Masked);
        }

        foreach (var nested in plan.Nested)
        {
            var current = nested.GetValue(copy ?? value);
            var masked = MaskObject(current, granted);
            if (!ReferenceEquals(current, masked))
            {
                copy ??= Clone(value);
                Set(copy, nested, masked);
            }
        }

        return copy ?? value;
    }

    private static object MaskSequence(IEnumerable sequence, Type type, Type element, PermissionSet granted)
    {
        var original = sequence.Cast<object?>().ToList();
        var items = original.Select(item => MaskObject(item, granted)).ToList();
        if (items.Zip(original).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            return sequence; // Nothing withheld: keep the caller's list.
        }

        if (type.IsArray)
        {
            var array = Array.CreateInstance(element, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                array.SetValue(items[i], i);
            }

            return array;
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static object Clone(object value) =>
        value.GetType().GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public) is { } recordClone
            ? recordClone.Invoke(value, null)!
            : MemberwiseCloneMethod.Invoke(value, null)!;

    private static void Set(object target, PropertyInfo property, object? value)
    {
        if (property.SetMethod is { } setter)
        {
            setter.Invoke(target, [value]);
            return;
        }

        var backing = property.DeclaringType!.GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{property.DeclaringType.Name}.{property.Name} is a sensitive field but can't be masked (no setter or backing field).");
        backing.SetValue(target, value);
    }

    private sealed record SensitiveProperty(PropertyInfo Property, string Permission, object? Masked);

    private sealed record Plan(IReadOnlyList<SensitiveProperty> Sensitive, IReadOnlyList<PropertyInfo> Nested)
    {
        public static Plan Empty { get; } = new([], []);

        public bool IsEmpty => Sensitive.Count == 0 && Nested.Count == 0;
    }

    private static Plan PlanOf(Type type) => Plans.TryGetValue(type, out var plan) ? plan : Build(type, []);

    private static Plan Build(Type type, HashSet<Type> building)
    {
        if (Plans.TryGetValue(type, out var known))
        {
            return known;
        }

        // Only Workbench contract types carry sensitive fields; a type already being built is a cycle.
        if (type.Assembly != typeof(SensitiveFieldAttribute).Assembly || !building.Add(type))
        {
            return Plan.Empty;
        }

        var sensitive = new List<SensitiveProperty>();
        var nested = new List<PropertyInfo>();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0 && p.CanRead))
        {
            if (property.GetCustomAttribute<SensitiveFieldAttribute>() is { } attribute)
            {
                var t = property.PropertyType;
                sensitive.Add(new SensitiveProperty(property, attribute.Permission, t.IsValueType && Nullable.GetUnderlyingType(t) is null ? Activator.CreateInstance(t) : null));
            }
            else if (IsSettable(property) && !Build(ElementOrSelf(property.PropertyType), building).IsEmpty)
            {
                nested.Add(property);
            }
        }

        building.Remove(type);
        var plan = sensitive.Count == 0 && nested.Count == 0 ? Plan.Empty : new Plan(sensitive, nested);
        Plans[type] = plan;
        return plan;
    }

    private static bool IsSettable(PropertyInfo property) =>
        property.SetMethod is not null || property.DeclaringType!.GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) is not null;

    private static Type ElementOrSelf(Type type) => ElementTypeOf(type) ?? type;

    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        // Any sequence (lists, collection expressions); a dictionary's pairs aren't contract records, so it's left alone.
        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null)
        {
            return enumerable.GetGenericArguments()[0];
        }

        return null;
    }
}
