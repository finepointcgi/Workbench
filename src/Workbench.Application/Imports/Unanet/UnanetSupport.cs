using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.Imports.Unanet;

/// <summary>Changes to one field (<c>Only</c>: use just these headings, not Costpoint's as well) of a Costpoint importer's field list: Unanet column names tried first, plus optional wording.</summary>
internal sealed record Tweak(string Name, string[] Aliases, bool? Required = null, string? Label = null, string? Description = null, bool Only = false);

/// <summary>
/// Unanet importers reuse the Costpoint importers' fields (so the import logic, dry run and rollback are the same) and
/// put Unanet's usual column headings in front of each field's aliases, so a Unanet export maps itself.
/// </summary>
internal static class UnanetFields
{
    public static IReadOnlyList<ImportField> Of(IReadOnlyList<ImportField> baseFields, IEnumerable<Tweak> tweaks, string[]? drop = null, params ImportField[] extra)
    {
        var byName = tweaks.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var fields = baseFields.Where(f => drop is null || !drop.Contains(f.Name)).Select(f => byName.TryGetValue(f.Name, out var t)
            ? new ImportField(f.Name, t.Label ?? f.Label, t.Required ?? f.Required, t.Description ?? f.Description, t.Only ? t.Aliases : [.. t.Aliases, .. f.Aliases.Where(a => !IsCostpointCode(a) && !t.Aliases.Contains(a, StringComparer.OrdinalIgnoreCase))])
            : f);
        return [.. fields, .. extra];
    }

    /// <summary>Costpoint's database column names (<c>EMPL_ID</c>): no use in a Unanet file, so they're not offered for it.</summary>
    private static bool IsCostpointCode(string alias) => alias.Contains('_') && alias.All(c => !char.IsLetter(c) || char.IsUpper(c));

    /// <summary>Fields the Costpoint importer doesn't have, or has under a different shape.</summary>
    public static ImportField Field(string name, string label, string description, params string[] aliases) => new(name, label, false, description, aliases);
}

/// <summary>
/// Turns Unanet-shaped rows into the rows the shared Costpoint importer logic expects (a project code plus a task code
/// become one dotted WBS id, a status word becomes Y/N...). The new rows keep the original file name and row number, so
/// every error and dry-run line still points at the row in the file the user uploaded.
/// </summary>
internal static class Canonical
{
    public static List<MappedRow> Rewrite(ImportContext context, IEnumerable<MappedRow> rows, IReadOnlyList<string> fields, Func<MappedRow, IEnumerable<IReadOnlyDictionary<string, string?>>> convert)
    {
        var files = new Dictionary<string, TabularFile>();
        var identity = fields.ToDictionary(f => f, f => (string?)f);
        var result = new List<MappedRow>();
        foreach (var row in rows)
        {
            if (!files.TryGetValue(row.FileName, out var file))
            {
                files[row.FileName] = file = new TabularFile(row.FileName, fields, []);
            }

            foreach (var values in convert(row))
            {
                var cells = fields.Select(f => values.GetValueOrDefault(f) ?? "").ToList();
                result.Add(new MappedRow(file, new TabularRow(row.Number, cells), identity, context.Run));
            }
        }

        return result;
    }

    /// <summary>The row's values for <paramref name="fields"/> as they were mapped, to change a few and pass on.</summary>
    public static Dictionary<string, string?> Copy(MappedRow row, IEnumerable<string> fields) =>
        fields.ToDictionary(f => f, f => row.Get(f));

    public static IEnumerable<IReadOnlyDictionary<string, string?>> One(IReadOnlyDictionary<string, string?> values) => [values];

    public static IEnumerable<IReadOnlyDictionary<string, string?>> None => [];
}

/// <summary>What Unanet calls things, in Workbench's words.</summary>
internal static class UnanetTerms
{
    private static readonly string[] IndirectWords = ["internal", "overhead", "indirect", "g&a", "g & a", "fringe", "b&p", "bid", "proposal", "irad", "ir&d", "admin", "unallowable"];
    private static readonly string[] LeaveWords = ["leave", "pto", "holiday", "vacation", "sick", "time off"];

    /// <summary>"Direct" for a customer project, "Indirect" for overhead-type internal projects, "Leave" for time off.</summary>
    public static string Charge(string? projectType)
    {
        var text = (projectType ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return "Direct";
        }

        if (LeaveWords.Any(text.Contains))
        {
            return "Leave";
        }

        return IndirectWords.Any(text.Contains) ? "Indirect" : "Direct";
    }

    public static bool HasContract(string? projectType) => Charge(projectType) == "Direct";

    /// <summary>Unanet project statuses → Y (open) / N (closed).</summary>
    public static string Open(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "closed" or "inactive" or "complete" or "completed" or "cancelled" or "canceled" or "terminated" or "n" or "no" or "false" or "0" => "N",
        _ => "Y",
    };

    /// <summary>Unanet contract status words → what the contract importer reads.</summary>
    public static string? ContractStatus(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "" => null,
        "proposal" or "proposed" or "planned" or "not started" or "pending" or "awarded" or "bid" => "Pending",
        "closed" or "inactive" or "complete" or "completed" or "cancelled" or "canceled" or "terminated" => "Closed",
        "active" or "open" => "Active",
        var other => other,
    };

    /// <summary>Task code of the single charge code a task-less Unanet project gets.</summary>
    public const string GeneralTask = "00";

    public static string Wbs(string project, string? taskPath) => string.IsNullOrEmpty(taskPath) ? project : $"{project}.{taskPath}";
}

/// <summary>
/// Finds the Workbench WBS element for a Unanet project code and task code, however deep the task was nested when the
/// tasks were imported (<c>PROJ.PARENT.TASK</c>), so budgets, assignments and time don't need the parent task.
/// </summary>
internal sealed class UnanetWbs
{
    private readonly Dictionary<string, ProjectDto> _projects;
    private readonly Dictionary<Guid, IReadOnlyList<WbsElementDto>> _wbs = [];
    private readonly IProjectService _service;

    private UnanetWbs(IProjectService service, Dictionary<string, ProjectDto> projects)
    {
        _service = service;
        _projects = projects;
    }

    public static async Task<UnanetWbs> LoadAsync(IProjectService service, CancellationToken cancellationToken)
    {
        var projects = (await service.ListProjectsAsync(cancellationToken)).ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        return new UnanetWbs(service, projects);
    }

    public ProjectDto? Project(string code) => _projects.GetValueOrDefault(code);

    /// <summary>The element for the project and (optional) task; null with a plain-words reason when there isn't exactly one.</summary>
    public async Task<(WbsElementDto? Element, string? Problem)> FindAsync(string projectCode, string? taskCode, CancellationToken cancellationToken)
    {
        if (!_projects.TryGetValue(projectCode, out var project))
        {
            return (null, $"Project {projectCode} isn't in Workbench; import projects first.");
        }

        if (!_wbs.TryGetValue(project.Id, out var elements))
        {
            _wbs[project.Id] = elements = await _service.ListWbsAsync(project.Id, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(taskCode))
        {
            // Unanet lets time go to a project without tasks. Workbench charges WBS elements, so such projects get one "General" element.
            var general = elements.FirstOrDefault(e => string.Equals(e.Code, UnanetTerms.Wbs(projectCode, UnanetTerms.GeneralTask), StringComparison.OrdinalIgnoreCase));
            if (general is not null)
            {
                return (general, null);
            }

            return elements.Count == 1 ? (elements[0], null) : (null, $"Project {projectCode} has tasks, so the row needs a task code.");
        }

        var task = taskCode.Trim();
        var exact = elements.FirstOrDefault(e => string.Equals(e.Code, $"{projectCode}.{task}", StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return (exact, null);
        }

        var ends = elements.Where(e => e.Code.EndsWith("." + task, StringComparison.OrdinalIgnoreCase)).ToList();
        return ends.Count switch
        {
            1 => (ends[0], null),
            0 => (null, $"Task {task} isn't a WBS element of project {projectCode}; import projects and tasks first."),
            _ => (null, $"Task {task} matches more than one WBS element of project {projectCode} ({string.Join(", ", ends.Take(3).Select(e => e.Code))}); the task code must be unique in the project."),
        };
    }
}
