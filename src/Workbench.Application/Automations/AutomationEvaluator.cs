using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Domain.Approvals;
using Workbench.Domain.Automations;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Automations;

/// <summary>Who an automation acts as, with that person's roles at the time of the run. Nothing is added to them.</summary>
public sealed record AutomationPrincipal(string Email, OrgRoles Roles, Guid? EmployeeId, bool IsActive)
{
    public static async Task<AutomationPrincipal> LoadAsync(IWorkbenchDbContext db, string email, CancellationToken ct)
    {
        var normalized = ApprovalEngine.Normalize(email);
        var active = await db.Set<Domain.Identity.AppUser>().AsNoTracking().AnyAsync(u => u.Email == normalized && u.IsActive, ct);
        var roles = active ? await Identity.RoleStore.BuiltInRolesOfAsync(db, normalized, ct) : OrgRoles.Employee;
        var employee = active ? await EmployeeLinks.FindForUserAsync(db, normalized, ct) : null;
        return new AutomationPrincipal(normalized, roles, employee?.Id, active);
    }
}

/// <summary>
/// One evaluation of an automation against one record: the record and its fields (built-in, computed, custom through
/// <see cref="IEntityFieldCatalog"/>, or the integration event's data), the actor and the principal, the change that fired
/// it, and the inputs the run log keeps. Fields the principal may not read evaluate as "no access".
/// </summary>
public sealed class AutomationScope
{
    private static readonly Regex Token = new(@"\{([A-Za-z][A-Za-z0-9_.\-]*)\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private readonly Dictionary<string, object?> _fields = new(StringComparer.OrdinalIgnoreCase);
    private OrgRoles? _actorRoles;
    private IReadOnlyList<EntityFieldDto>? _catalogFields;

    public required IWorkbenchDbContext Db { get; init; }
    public required IServiceProvider Services { get; init; }
    public required AutomationPrincipal Principal { get; init; }
    public required IAutomationPermissions Permissions { get; init; }
    public AutomationEntity? Entity { get; init; }
    public Guid? RecordId { get; init; }
    public object? Record { get; init; }
    public string? Actor { get; init; }
    public RecordChange? Change { get; init; }
    public JsonElement? Payload { get; init; }
    public string AutomationName { get; init; } = "";
    public CancellationToken Ct { get; init; }

    public Dictionary<string, string?> Inputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? RecordLabel => Record is not null && Entity is not null ? Entity.Describe(Record) : null;

    public string? Link => Entity is not null && RecordId is { } id ? Entity.Link(id, Record) : null;

    /// <summary>Whether the principal may see this record at all: their roles, or it's theirs, or they manage its project.</summary>
    public async Task<bool> PrincipalCanReadRecordAsync()
    {
        if (Entity is null || Record is null || Entity.CanReadAll(Principal.Roles))
        {
            return true;
        }

        if (Principal.EmployeeId is { } me)
        {
            if (Entity.EmployeeProperty is { } ep && Entity.ClrType.GetProperty(ep)?.GetValue(Record) is Guid owner
                && (owner == me || await Db.Set<Employee>().AsNoTracking().AnyAsync(e => e.Id == owner && e.SupervisorId == me, Ct)))
            {
                return true; // Their own record, or a direct report's.
            }

            if (await ProjectAsync() is { } projectId
                && await Db.Set<Project>().AsNoTracking().AnyAsync(p => p.Id == projectId && p.ProjectManagerId == me, Ct))
            {
                return true;
            }

            if (Entity.Key == "contract" && Record is Contract contract
                && await Db.Set<Project>().AsNoTracking().AnyAsync(p => p.ContractId == contract.Id && p.ProjectManagerId == me, Ct))
            {
                return true;
            }
        }

        if (Entity.SubmitterProperty is { } sp && Entity.ClrType.GetProperty(sp)?.GetValue(Record) is string by && ApprovalEngine.Normalize(by) == Principal.Email)
        {
            return true;
        }

        // Someone the record was routed to for approval sees it.
        if (Entity.ApprovalDocumentType is { } type && RecordId is { } id)
        {
            var engine = Services.GetService<ApprovalEngine>() ?? new ApprovalEngine(TimeProvider.System);
            return await engine.IsParticipantAsync(Db, type, id, Principal.Email, Ct);
        }

        return false;
    }

    /// <summary>Whether the principal may read a field (its permission key).</summary>
    public async Task<bool> PrincipalCanReadFieldAsync(string key)
    {
        var field = await FieldInfoAsync(key);
        return field?.PermissionKey is not { } permission || Permissions.PrincipalHas(Principal, permission);
    }

    public async Task<EntityFieldDto?> FieldInfoAsync(string key)
    {
        if (Entity is null)
        {
            return null;
        }

        _catalogFields ??= await FieldsAsync(Entity, Services, Ct);
        return _catalogFields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Built-in fields plus whatever the registered catalog adds (custom fields); built-in ones are never lost.</summary>
    public static async Task<IReadOnlyList<EntityFieldDto>> FieldsAsync(AutomationEntity entity, IServiceProvider services, CancellationToken ct)
    {
        var builtIn = AutomationEntities.Fields(entity);
        if (services.GetService<IEntityFieldCatalog>() is not { } catalog || catalog is BuiltInEntityFieldCatalog || !catalog.EntityTypes.Contains(entity.Key))
        {
            return builtIn;
        }

        var all = builtIn.ToList();
        foreach (var field in await catalog.GetFieldsAsync(entity.Key, ct))
        {
            if (!all.Any(f => string.Equals(f.Key, field.Key, StringComparison.OrdinalIgnoreCase)))
            {
                all.Add(field);
            }
        }

        return all;
    }

    /// <summary>A field's value, or <see cref="NoAccess"/> when the principal may not read it.</summary>
    public async Task<object?> FieldAsync(string key)
    {
        if (_fields.TryGetValue(key, out var cached))
        {
            return cached;
        }

        object? value;
        if (!await PrincipalCanReadFieldAsync(key))
        {
            value = NoAccess.Instance;
        }
        else if (Record is not null && Entity is not null
            && (Entity.Property(key) is not null || Entity.Computed.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))))
        {
            value = await BuiltInEntityFieldCatalog.ReadAsync(Db, Entity, Record, key, Services, Ct);
        }
        else if (Record is not null && Entity is not null && RecordId is { } id && Services.GetService<IEntityFieldCatalog>() is { } catalog && catalog is not BuiltInEntityFieldCatalog)
        {
            value = await catalog.GetValueAsync(Entity.Key, id, key, Ct);
        }
        else if (Record is null && Change?.AfterValue(key) is { } after)
        {
            value = after;
        }
        else if (Record is null && Change?.BeforeValue(key) is { } before)
        {
            value = before;
        }
        else
        {
            value = PayloadValue(key);
        }

        _fields[key] = value;
        Inputs[key] = value is NoAccess ? "(no access)" : Format(value);
        return value;
    }

    public async Task<OrgRoles> ActorRolesAsync() =>
        _actorRoles ??= Actor is null ? OrgRoles.Employee : await Identity.RoleStore.BuiltInRolesOfAsync(Db, Actor, Ct);

    public async Task<Guid?> CompanyAsync() =>
        Prop(Entity?.CompanyProperty) ?? (await EmployeeAsync())?.CompanyId
            ?? (await ProjectAsync() is { } p ? await Db.Set<Project>().AsNoTracking().Where(x => x.Id == p).Select(x => (Guid?)x.CompanyId).FirstOrDefaultAsync(Ct) : null);

    public async Task<Guid?> OrgUnitAsync() => Prop(Entity?.OrgUnitProperty) ?? (await EmployeeAsync())?.OrgUnitId;

    public Task<Guid?> ProjectAsync() => Task.FromResult(Prop(Entity?.ProjectProperty));

    public async Task<Employee?> EmployeeAsync() =>
        Prop(Entity?.EmployeeProperty) is { } id ? await Db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, Ct) : null;

    /// <summary>The emails a relationship names for this record (see <see cref="AutomationRelationships"/>).</summary>
    public async Task<IReadOnlyList<string>> RelationshipAsync(string name)
    {
        var emails = new List<string?>();
        switch (name)
        {
            case AutomationRelationships.Actor:
                emails.Add(Actor);
                break;
            case AutomationRelationships.Submitter:
                emails.Add(Entity?.SubmitterProperty is { } sp && Record is not null ? Entity.ClrType.GetProperty(sp)?.GetValue(Record) as string : null);
                if (emails[0] is null && await PendingInstanceAsync() is { } instance)
                {
                    emails[0] = instance.SubmitterEmail;
                }

                break;
            case AutomationRelationships.Employee:
                emails.Add((await EmployeeAsync())?.Email);
                break;
            case AutomationRelationships.Supervisor:
                if ((await EmployeeAsync())?.SupervisorId is { } supervisor)
                {
                    emails.Add(await Db.Set<Employee>().AsNoTracking().Where(e => e.Id == supervisor).Select(e => e.Email).FirstOrDefaultAsync(Ct));
                }

                break;
            case AutomationRelationships.ProjectManager:
                var managers = Entity?.Key == "contract" && RecordId is { } contractId
                    ? Db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contractId && p.ProjectManagerId != null).Select(p => p.ProjectManagerId!.Value)
                    : await ProjectAsync() is { } projectId
                        ? Db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId && p.ProjectManagerId != null).Select(p => p.ProjectManagerId!.Value)
                        : null;
                if (managers is not null)
                {
                    var ids = await managers.Distinct().ToListAsync(Ct);
                    emails.AddRange(await Db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => e.Email).ToListAsync(Ct));
                }

                break;
            case AutomationRelationships.Approvers or AutomationRelationships.ApproversSupervisors:
                var approvers = await ApproversAsync();
                if (name == AutomationRelationships.Approvers)
                {
                    emails.AddRange(approvers);
                }
                else
                {
                    var supervisorIds = await Db.Set<Employee>().AsNoTracking()
                        .Where(e => approvers.Contains(e.Email.ToLower()) && e.SupervisorId != null).Select(e => e.SupervisorId!.Value).ToListAsync(Ct);
                    emails.AddRange(await Db.Set<Employee>().AsNoTracking().Where(e => supervisorIds.Contains(e.Id)).Select(e => e.Email).ToListAsync(Ct));
                }

                break;
            default:
                throw new ArgumentException($"\"{name}\" isn't a relationship automations know.");
        }

        return emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => ApprovalEngine.Normalize(e)).Distinct().ToList();
    }

    /// <summary>Who must decide the record's pending approval now.</summary>
    public async Task<IReadOnlyList<string>> ApproversAsync()
    {
        if (await PendingInstanceAsync() is not { } instance)
        {
            return [];
        }

        var engine = Services.GetService<ApprovalEngine>() ?? new ApprovalEngine(TimeProvider.System);
        var result = new List<string>();
        foreach (var task in await Db.Set<ApprovalTask>().AsNoTracking().Where(t => t.InstanceId == instance.Id && t.Status == ApprovalTaskStatus.Pending).ToListAsync(Ct))
        {
            result.AddRange(await engine.EligibleAsync(Db, task, Ct));
        }

        return result.Distinct().ToList();
    }

    private async Task<ApprovalInstance?> PendingInstanceAsync()
    {
        if (RecordId is not { } id)
        {
            return null;
        }

        var types = new List<string>();
        if (Entity?.ApprovalDocumentType is { } type)
        {
            types.Add(type);
        }

        types.AddRange(await Db.Set<AutomationApprovalRequest>().AsNoTracking().Where(r => r.RecordId == id).Select(r => r.DocumentType).Distinct().ToListAsync(Ct));
        return await Db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.DocumentId == id && types.Contains(i.DocumentType) && i.Status == ApprovalInstanceStatus.Pending)
            .OrderByDescending(i => i.StartedAtUtc).FirstOrDefaultAsync(Ct);
    }

    /// <summary>The emails a recipient names: a person, a role's members, a field's employee or email, or a relationship.</summary>
    public async Task<IReadOnlyList<string>> RecipientsAsync(IEnumerable<AutomationRecipient> recipients)
    {
        var emails = new List<string>();
        foreach (var recipient in recipients)
        {
            switch (recipient.Kind)
            {
                case RecipientKind.Person:
                    emails.Add(ApprovalEngine.Normalize(recipient.Value));
                    break;
                case RecipientKind.Role:
                    emails.AddRange(await ApprovalEngine.RoleMembersAsync(Db, Enum.Parse<ApprovalOrgRole>(recipient.Value, ignoreCase: true), Ct));
                    break;
                case RecipientKind.Field:
                    var value = await FieldAsync(recipient.Value);
                    if (value is Guid employeeId)
                    {
                        emails.Add(await Db.Set<Employee>().AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.Email).FirstOrDefaultAsync(Ct) ?? "");
                    }
                    else if (value is string text && text.Contains('@'))
                    {
                        emails.Add(ApprovalEngine.Normalize(text));
                    }
                    else if (value is string maybeId && Guid.TryParse(maybeId, out var parsed))
                    {
                        emails.Add(await Db.Set<Employee>().AsNoTracking().Where(e => e.Id == parsed).Select(e => e.Email).FirstOrDefaultAsync(Ct) ?? "");
                    }

                    break;
                case RecipientKind.Relationship:
                    emails.AddRange(await RelationshipAsync(recipient.Value));
                    break;
            }
        }

        return emails.Where(e => e.Contains('@')).Select(ApprovalEngine.Normalize).Distinct().ToList();
    }

    /// <summary>
    /// Replaces <c>{field}</c> tokens with the record's values (fields the principal can't read show as •••), plus
    /// <c>{record}</c>, <c>{link}</c>, <c>{actor}</c>, <c>{automation}</c>, <c>{entity}</c> and <c>{today}</c>.
    /// </summary>
    public async Task<string> RenderAsync(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        var result = template;
        foreach (Match match in Token.Matches(template))
        {
            var key = match.Groups[1].Value;
            var text = key.ToLowerInvariant() switch
            {
                "record" => RecordLabel ?? "",
                "link" => Link ?? "",
                "actor" => Actor ?? "",
                "automation" => AutomationName,
                "entity" => Entity?.Label ?? "",
                "today" => DateTime.UtcNow.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                _ => await DisplayAsync(key),
            };
            result = result.Replace(match.Value, text, StringComparison.Ordinal);
        }

        return result;
    }

    private async Task<string> DisplayAsync(string key)
    {
        object? value;
        try
        {
            value = await FieldAsync(key);
        }
        catch (ArgumentException)
        {
            return "{" + key + "}";
        }

        if (value is NoAccess)
        {
            return "•••";
        }

        if (value is Guid id)
        {
            return await Db.Set<Employee>().AsNoTracking().Where(e => e.Id == id).Select(e => e.DisplayName).FirstOrDefaultAsync(Ct)
                ?? await Db.Set<Project>().AsNoTracking().Where(p => p.Id == id).Select(p => p.Code + " " + p.Name).FirstOrDefaultAsync(Ct)
                ?? await Db.Set<Contract>().AsNoTracking().Where(c => c.Id == id).Select(c => c.ContractNumber).FirstOrDefaultAsync(Ct)
                ?? id.ToString();
        }

        return value switch
        {
            null => "",
            decimal d => d.ToString("#,##0.##", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            DateTime time => time.ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture),
            _ => Format(value) ?? "",
        };
    }

    /// <summary>The invariant string form conditions compare.</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        NoAccess => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        decimal m => m.ToString("0.##########", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private Guid? Prop(string? name) =>
        name is null || Record is null || Entity is null ? null
        : Entity.ClrType.GetProperty(name)?.GetValue(Record) switch
        {
            Guid g => g,
            _ => null,
        };

    private string? PayloadValue(string key)
    {
        if (Payload is not { ValueKind: JsonValueKind.Object } data)
        {
            return null;
        }

        var name = key.StartsWith("data.", StringComparison.OrdinalIgnoreCase) ? key[5..] : key;
        foreach (var property in data.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
            }
        }

        return null;
    }

    /// <summary>Marks a field the principal may not read.</summary>
    public sealed class NoAccess
    {
        public static readonly NoAccess Instance = new();
    }
}

/// <summary>Evaluates condition groups (AND/OR, nested) and records each comparison's result for the run log.</summary>
public static class AutomationConditions
{
    public static async Task<bool> EvaluateAsync(AutomationConditionGroup? group, AutomationScope scope, List<AutomationConditionResultDto> results)
    {
        if (group is null || group.IsEmpty)
        {
            return true;
        }

        var outcomes = new List<bool>();
        foreach (var condition in group.Conditions ?? [])
        {
            outcomes.Add(await EvaluateAsync(condition, scope, results));
        }

        foreach (var child in group.Groups ?? [])
        {
            if (!child.IsEmpty)
            {
                outcomes.Add(await EvaluateAsync(child, scope, results));
            }
        }

        return outcomes.Count == 0 || (group.Logic == GroupLogic.Any ? outcomes.Any(x => x) : outcomes.All(x => x));
    }

    private static async Task<bool> EvaluateAsync(AutomationCondition condition, AutomationScope scope, List<AutomationConditionResultDto> results)
    {
        bool matched;
        string? actual;
        switch (condition.Subject)
        {
            case ConditionSubject.ActorRole:
                var roles = await scope.ActorRolesAsync();
                var held = ApprovalOrgRoleName.All.Where(r => HasRole(roles, r)).ToList();
                actual = string.Join(", ", held);
                matched = CompareSet(condition, held);
                break;
            case ConditionSubject.Company or ConditionSubject.OrgUnit or ConditionSubject.Project:
                var id = condition.Subject switch
                {
                    ConditionSubject.Company => await scope.CompanyAsync(),
                    ConditionSubject.OrgUnit => await scope.OrgUnitAsync(),
                    _ => await scope.ProjectAsync(),
                };
                actual = id?.ToString();
                matched = CompareSet(condition, id is null ? [] : [id.Value.ToString()]);
                break;
            default:
                var key = condition.FieldKey ?? throw new ArgumentException("A field condition needs a field.");
                var value = await scope.FieldAsync(key);
                if (value is AutomationScope.NoAccess)
                {
                    results.Add(new AutomationConditionResultDto(Describe(condition), false, "(no access)"));
                    return false;
                }

                actual = AutomationScope.Format(value);
                matched = Compare(condition.Operator, actual, condition.Value, condition.Values);
                break;
        }

        results.Add(new AutomationConditionResultDto(Describe(condition), matched, actual));
        return matched;
    }

    public static bool HasRole(OrgRoles roles, string role) => role switch
    {
        "Employee" => true,
        _ when Enum.TryParse<ApprovalOrgRole>(role, true, out var r) => ApprovalEngine.HasRole(roles, r),
        _ => false,
    };

    private static bool CompareSet(AutomationCondition c, IReadOnlyList<string> actual)
    {
        var wanted = (c.Values ?? []).Concat(c.Value is null ? [] : [c.Value]).ToList();
        bool Any() => actual.Any(a => wanted.Contains(a, StringComparer.OrdinalIgnoreCase));
        return c.Operator switch
        {
            ConditionOperator.IsEmpty => actual.Count == 0,
            ConditionOperator.IsNotEmpty => actual.Count > 0,
            ConditionOperator.NotEquals or ConditionOperator.NotIn or ConditionOperator.NotContains => !Any(),
            _ => Any(),
        };
    }

    /// <summary>Compares invariant strings: as numbers when both are numbers, as dates when both are dates, else as text (ignoring case).</summary>
    public static bool Compare(ConditionOperator op, string? actual, string? expected, IReadOnlyList<string>? values = null)
    {
        switch (op)
        {
            case ConditionOperator.IsEmpty:
                return string.IsNullOrWhiteSpace(actual);
            case ConditionOperator.IsNotEmpty:
                return !string.IsNullOrWhiteSpace(actual);
            case ConditionOperator.In or ConditionOperator.NotIn:
                var any = (values ?? (expected is null ? [] : expected.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
                    .Any(v => Compare(ConditionOperator.Equals, actual, v));
                return op == ConditionOperator.In ? any : !any;
            case ConditionOperator.Contains:
                return actual is not null && expected is not null && actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
            case ConditionOperator.NotContains:
                return actual is null || expected is null || !actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
            case ConditionOperator.StartsWith:
                return actual is not null && expected is not null && actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
        }

        int? order = null;
        if (decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var b))
        {
            order = a.CompareTo(b);
        }
        else if (DateOnly.TryParse(actual?.Length >= 10 ? actual[..10] : actual, CultureInfo.InvariantCulture, out var da)
            && DateOnly.TryParse(expected, CultureInfo.InvariantCulture, out var db))
        {
            order = da.CompareTo(db);
        }

        var equal = order is { } o ? o == 0 : string.Equals(actual ?? "", expected ?? "", StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            ConditionOperator.Equals => equal,
            ConditionOperator.NotEquals => !equal,
            ConditionOperator.GreaterThan => order is > 0,
            ConditionOperator.GreaterOrEqual => order is >= 0,
            ConditionOperator.LessThan => order is < 0,
            ConditionOperator.LessOrEqual => order is <= 0,
            _ => false,
        };
    }

    public static string Describe(AutomationCondition c)
    {
        var subject = c.Subject switch
        {
            ConditionSubject.Field => c.FieldKey ?? "?",
            ConditionSubject.ActorRole => "the actor's role",
            ConditionSubject.Company => "the company",
            ConditionSubject.OrgUnit => "the org unit",
            _ => "the project",
        };
        var op = c.Operator switch
        {
            ConditionOperator.Equals => "is",
            ConditionOperator.NotEquals => "is not",
            ConditionOperator.GreaterThan => ">",
            ConditionOperator.GreaterOrEqual => "≥",
            ConditionOperator.LessThan => "<",
            ConditionOperator.LessOrEqual => "≤",
            ConditionOperator.Contains => "contains",
            ConditionOperator.NotContains => "doesn't contain",
            ConditionOperator.StartsWith => "starts with",
            ConditionOperator.IsEmpty => "is empty",
            ConditionOperator.IsNotEmpty => "is not empty",
            ConditionOperator.In => "is one of",
            _ => "is not one of",
        };
        var value = c.Operator is ConditionOperator.IsEmpty or ConditionOperator.IsNotEmpty ? ""
            : " " + string.Join(", ", (c.Values ?? []).Concat(c.Value is null ? [] : [c.Value]));
        return $"{subject} {op}{value}".TrimEnd();
    }
}
