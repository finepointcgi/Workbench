using Workbench.Contracts.Customization;

namespace Workbench.Application.Customization;

/// <summary>
/// The rules of form layouts, without a database: which saved layout applies (fallback), how a saved layout is brought in
/// step with today's fields, and what a layout may change.
/// </summary>
public static class FormLayoutEngine
{
    public const int MaxSections = 20;
    public const int MaxTitleLength = 100;

    /// <summary>
    /// The layout that applies to someone working in <paramref name="companyId"/> with <paramref name="roleKeys"/> (most
    /// specific first): role in the company, then everyone in the company, then the role in every company, then everyone in
    /// every company. Null: none, so the built-in layout applies.
    /// </summary>
    public static T? Pick<T>(IEnumerable<T> layouts, Func<T, Guid?> company, Func<T, string?> role, Guid? companyId, IReadOnlyList<string> roleKeys)
        where T : class
    {
        var list = layouts.ToList();
        var roles = roleKeys.Where(r => r != CustomizationRoles.Everyone).Distinct().ToList();
        IEnumerable<Guid?> companies = companyId is null ? [null] : [companyId, null];
        foreach (var c in companies)
        {
            foreach (var r in roles.Append(null))
            {
                if (list.FirstOrDefault(l => company(l) == c && role(l) == r) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A saved layout brought in step with the fields that exist now: unknown and repeated fields are dropped, fields added
    /// since (or never placed) go back into their built-in section after their built-in neighbour (custom fields at the end
    /// of the last section), and fields the domain needs are shown and required whatever the layout says.
    /// </summary>
    public static IReadOnlyList<FormSectionLayout> Normalize(IReadOnlyList<FormSectionLayout> saved, FormLayoutDto builtIn, IReadOnlyList<EntityFieldDto> fields)
    {
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sections = (saved.Count == 0 ? builtIn.Sections : saved)
            .Select(s => (s.Key, s.Title, Fields: s.Fields.Where(f => byKey.ContainsKey(f.Key) && seen.Add(f.Key)).ToList()))
            .ToList();

        // Built-in fields missing from the layout: after the field that precedes them on the built-in page.
        foreach (var section in builtIn.Sections)
        {
            for (var i = 0; i < section.Fields.Count; i++)
            {
                var key = section.Fields[i].Key;
                if (!byKey.ContainsKey(key) || !seen.Add(key))
                {
                    continue;
                }

                var target = sections.FirstOrDefault(s => s.Key == section.Key);
                if (target.Fields is null)
                {
                    target = sections[^1];
                }

                var before = i == 0 ? -1 : target.Fields.FindIndex(f => f.Key == section.Fields[i - 1].Key);
                target.Fields.Insert(before < 0 ? (i == 0 ? 0 : target.Fields.Count) : before + 1, new FormFieldPlacement(key));
            }
        }

        // Fields the built-in page doesn't know (custom fields): at the end.
        foreach (var field in fields.Where(f => seen.Add(f.Key)))
        {
            sections[^1].Fields.Add(new FormFieldPlacement(field.Key));
        }

        return sections
            .Select(s => new FormSectionLayout(s.Key, s.Title, s.Fields.Select(f => byKey[f.Key].IsRequired
                ? new FormFieldPlacement(f.Key, Hidden: false, Required: true)
                : f with { Required = f.Required && CanRequire(byKey[f.Key]), Hidden = f.Hidden && !f.Required }).ToList()))
            .ToList();
    }

    /// <summary>
    /// What a form shows: the visible fields in order and the hidden ones (by the layout, or because the viewer lacks the
    /// field's permission). Fields the domain needs are never hidden by a permission.
    /// </summary>
    public static EffectiveFormLayout Effective(
        string entityType,
        Guid? layoutId,
        string source,
        IReadOnlyList<FormSectionLayout> normalized,
        IReadOnlyList<EntityFieldDto> fields,
        IReadOnlySet<string> grantedPermissions)
    {
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var hidden = new List<string>();
        var sections = new List<EffectiveFormSection>();
        foreach (var section in normalized)
        {
            var shown = new List<EffectiveFormField>();
            foreach (var placement in section.Fields)
            {
                var field = byKey[placement.Key];
                var permitted = field.PermissionKey is null || grantedPermissions.Contains(field.PermissionKey);
                if (!field.IsRequired && (placement.Hidden || !permitted))
                {
                    hidden.Add(field.Key);
                    continue;
                }

                shown.Add(new EffectiveFormField(field.Key, field.Label, field.FieldType, field.IsCustom, field.IsRequired,
                    field.IsRequired || placement.Required, field.Options));
            }

            sections.Add(new EffectiveFormSection(section.Key, section.Title, shown));
        }

        return new EffectiveFormLayout(entityType, layoutId, source, sections, hidden);
    }

    /// <summary>Checks a layout an administrator wants to save. Throws <see cref="ArgumentException"/> saying what's wrong.</summary>
    public static void Validate(IReadOnlyList<FormSectionLayout> sections, IReadOnlyList<EntityFieldDto> fields)
    {
        if (sections is null || sections.Count == 0)
        {
            throw new ArgumentException("A layout needs at least one section.", nameof(sections));
        }

        if (sections.Count > MaxSections)
        {
            throw new ArgumentException($"A layout can have at most {MaxSections} sections.", nameof(sections));
        }

        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var sectionKeys = new HashSet<string>(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section.Key) || section.Key.Length > 64 || !sectionKeys.Add(section.Key))
            {
                throw new ArgumentException("Each section needs its own key.", nameof(sections));
            }

            if ((section.Title?.Length ?? 0) > MaxTitleLength)
            {
                throw new ArgumentException($"Section headings are at most {MaxTitleLength} characters.", nameof(sections));
            }

            foreach (var placement in section.Fields ?? [])
            {
                if (!byKey.TryGetValue(placement.Key, out var field))
                {
                    throw new ArgumentException($"There is no field \"{placement.Key}\".", nameof(sections));
                }

                if (!placed.Add(placement.Key))
                {
                    throw new ArgumentException($"\"{field.Label}\" is placed twice.", nameof(sections));
                }

                if (placement.Hidden && field.IsRequired)
                {
                    throw new ArgumentException($"\"{field.Label}\" can't be hidden: a {field.EntityType.ToLowerInvariant()} can't be saved without it.", nameof(sections));
                }

                if (placement.Hidden && placement.Required)
                {
                    throw new ArgumentException($"\"{field.Label}\" can't be both hidden and required.", nameof(sections));
                }

                if (placement.Required && !field.IsRequired && !CanRequire(field))
                {
                    throw new ArgumentException($"\"{field.Label}\" always has a value, so it can't be made required.", nameof(sections));
                }
            }
        }
    }

    /// <summary>Only fields that can be left blank can be made required (not switches or figures).</summary>
    public static bool CanRequire(EntityFieldDto field) => field.FieldType is not (BuiltInForms.Bool or BuiltInForms.Display);

    /// <summary>Who a layout is for, in words.</summary>
    public static string Describe(string? roleKey, string? companyCode) =>
        (roleKey, companyCode) switch
        {
            (null, null) => "Everyone",
            (null, { } c) => $"Everyone in {c}",
            ({ } r, null) => CustomizationRoles.NameOf(r),
            ({ } r, { } c) => $"{CustomizationRoles.NameOf(r)} in {c}",
        };
}
